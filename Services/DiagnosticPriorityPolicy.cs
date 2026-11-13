using System.Text.RegularExpressions;
using ScopePilot.Domain;

namespace ScopePilot.Services;

// ScopePilot's triage weights, not scores defined by the referenced standards.
// Score only observed metadata; never treat missing observations as proof of safety.
public static class DiagnosticPriorityPolicy
{
    public const int Version = 4;
    private static readonly Regex Authentication = Words("login|logout|signin|signout|auth|oauth|sso|password|passwd|reset|session|token|mfa|register|signup");
    private static readonly Regex Authorization = Words("admin|role|roles|permission|permissions|account|accounts|member|members|user|users|profile|tenant|organization|organisation|private");
    private static readonly Regex Files = Words("upload|download|file|files|export|import|attachment|attachments|document|documents|report|reports");
    private static readonly Regex Integration = Words("webhook|callback|redirect|integration|proxy|fetch|url");
    private static readonly Regex Business = Words("payment|payments|checkout|purchase|invoice|invoices|order|orders|approve|approval|transfer|balance|billing");
    private static readonly Regex Input = Words("search|query|filter|form|contact|cart|submit");
    private static readonly Regex Api = Words("api|apis|graphql|ajax|rpc|rest|v[0-9]+");
    private static readonly Regex Ai = Words("ai|llm|chat|prompt|completion|completions|model|models|agent|agents|rag|embedding|embeddings|inference");
    private static readonly Regex Cloud = Words("actuator|metrics|health|ready|readiness|k8s|kube|metadata|openapi|swagger");
    private static readonly Regex ObjectId = new(@"(?:^|/)(?:\d+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:/|$)", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> CacheKeys = new(StringComparer.OrdinalIgnoreCase)
        { "_", "v", "ver", "version", "cb", "cache", "cachebuster", "rev", "hash" };
    private static readonly Regex IdentityKey = new(@"^(?:(?:user|account|tenant|owner|member|document|file|order|invoice|object|record)[_-]?)?id$", RegexOptions.IgnoreCase);

    public static bool HasSensitiveContext(ObservedRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) return true;
        // A logo or stylesheet named login/auth/user is not an authentication
        // operation. Retain directory, input, ID, header and response evidence.
        var contextPath = uri.AbsolutePath;
        if (GuidelineSelectionPolicy.HasAssetExtension(contextPath))
            contextPath = contextPath[..(contextPath.LastIndexOf('/') + 1)];
        var path = SplitWords(Uri.UnescapeDataString(contextPath));
        return Authentication.IsMatch(path) || Authorization.IsMatch(path) || Files.IsMatch(path) ||
            Integration.IsMatch(path) || Business.IsMatch(path) || Api.IsMatch(path) || Ai.IsMatch(path) ||
            Cloud.IsMatch(path) || Input.IsMatch(path) || ObjectId.IsMatch(uri.AbsolutePath) ||
            QueryKeys(uri).Length > 0 || request.FormCount > 0 || (request.FormFieldNames?.Length ?? 0) > 0 ||
            request.RequestBodyCaptured || (request.RequestFieldNames?.Length ?? 0) > 0 ||
            (request.RequestSemanticSignals?.Length ?? 0) > 0 || (request.RequestHeaderNames ?? []).Contains("Authorization", StringComparer.OrdinalIgnoreCase) ||
            request.StatusCode is >= 300 and < 400 or 401 or 403 or >= 500;
    }

    public static PriorityAssessment Evaluate(ObservedRequest request, SelectionAssessment selection, GuidelineSelectionOptions options, DocumentHints? documentHints = null)
    {
        var documented = DocumentPriorityEvidence.Match(request, documentHints, !selection.StaticAsset);
        if ((selection.StaticAsset || selection.PassiveHtml) && documented is null)
            return new(0, "0/100：通常の機能別診断からの除外候補。" + selection.Reason + " 安全性の証明ではありません。", []);
        var factors = new List<(int Points, string Reason, string[] References)>();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return new(20, "20/100：URLを解析できないため、対象外とせず手動確認を残します。", []);
        var path = SplitWords(Uri.UnescapeDataString(uri.AbsolutePath));
        var query = QueryKeys(uri);
        var method = (request.Method ?? "GET").ToUpperInvariant();
        var primary = new List<(int Points, string Reason, string[] References)>();
        var fields = (request.FormFieldNames ?? []).Concat(ScoringFields(request)).Select(SplitWords).ToArray();
        var semantics = ScoringSignals(request);
        var operations = ScoringOperations(request).Concat(RequestOperationIdentity.QueryValues(request.Url)).Select(x => x.ToLowerInvariant()).ToArray();
        var contentType = request.ContentType ?? string.Empty;
        Add(primary, Authentication.IsMatch(path) || fields.Any(Authentication.IsMatch), 45, "認証・セッションの操作候補", "authentication");
        Add(primary, Authorization.IsMatch(path) && !Authentication.IsMatch(path), 50, "権限境界・利用者データの確認候補", "authorization");
        Add(primary, Files.IsMatch(path) || fields.Any(Files.IsMatch) || contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase), 35, "ファイルの取扱い・取得権限の確認候補", "files");
        Add(primary, Integration.IsMatch(path) || query.Any(x => Integration.IsMatch(SplitWords(x))), 40, "外部接続・転送先の確認候補", "integration");
        Add(primary, Business.IsMatch(path), 45, "購入・承認・金額など業務上重要な操作候補", "business");
        Add(primary, Input.IsMatch(path) || query.Length > 0 || request.FormCount > 0 || (request.RequestFieldNames?.Length ?? 0) > 0 || fields.Length > 0, 20, "入力処理の確認候補", "input");
        Add(primary, Api.IsMatch(path) || contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase), 15, "APIの入力・出力の確認候補", "api");
        Add(primary, Ai.IsMatch(path), 35, "AI機能を示すURL（実機能と適用可否は要確認）", "ai");
        Add(primary, Cloud.IsMatch(path), 35, "運用・クラウド関連URL（対象構成への適用は要確認）", "cloud");
        Add(primary, semantics.Contains("personal-data"), 40, "要求本文に個人情報の項目・形式を観測（値は採点資料へ複写しない）", "authorization");
        Add(primary, semantics.Contains("financial-data") || operations.Any(x => x is "payment" or "purchase" or "checkout" or "approve" or "transfer"), 45, "要求内容に金額・決済・承認等の情報を観測", "business");
        Add(primary, semantics.Contains("authorization-data") || operations.Any(x => x is "admin" or "administrator" or "superuser" or "owner"), 50,
            request.RequestChangeBaselineMatched ? "要求本文の差分にロール・権限の項目を観測（反映結果は要確認）" : "要求本文にロール・権限の入力項目を観測（変更の有無は未確認）", "authorization");
        Add(primary, semantics.Contains("authentication-data"), 45, "要求本文に認証情報の項目を観測（秘密値は保持しない）", "authentication");
        Add(primary, semantics.Contains("file-upload"), 35, "要求本文にファイル送信を観測", "files");
        Add(primary, semantics.Contains("external-target"), 40, "要求本文に外部接続・転送先の項目を観測", "integration");
        Add(primary, semantics.Contains("ai-input"), 35, "要求本文にAI入力の項目を観測（AI機能への適用は要確認）", "ai");
        Add(primary, operations.Any(x => x is "delete" or "remove" or "update" or "write" or "put" or "post" or "patch"), 35, "要求に削除・更新等の処理指定を観測", "state");
        if (primary.Count > 0)
        {
            // One category bonus, even if multiple standards/categories overlap.
            var maximum = primary.Max(x => x.Points);
            factors.Add((maximum, string.Join("、", primary.Where(x => x.Points == maximum).Select(x => x.Reason)),
                primary.SelectMany(x => x.References).Distinct(StringComparer.Ordinal).ToArray()));
        }
        Add(factors, method is not ("GET" or "HEAD" or "OPTIONS"), 12, $"{method}による状態変更・本文入力の可能性", "state");
        Add(factors, query.Length > 0 || request.FormCount > 0 || fields.Length > 0 || (request.RequestFieldNames?.Length ?? 0) > 0, 10, "クエリ・要求本文・画面の入力点を観測", "input");
        Add(factors, ObjectId.IsMatch(uri.AbsolutePath) || query.Any(x => IdentityKey.IsMatch(x)) || ScoringFields(request).Any(x => IdentityKey.IsMatch(x.Split('.').Last())), 10, "オブジェクト識別子による認可境界の確認", "authorization");
        Add(factors, ObservedRequestIdentity.IsAuthenticatedRole(request.Role), 5, "認証済みロールでの通信を観測（権限の成立は要確認）", "authorization");
        Add(factors, request.StatusCode is 401 or 403, 8, "401/403のアクセス制御応答を観測", "authorization");
        Add(factors, request.StatusCode >= 500, 8, "5xxの例外応答を観測", "errors");
        Add(factors, request.StatusCode is >= 300 and < 400, 5, "リダイレクト応答を観測", "redirect");
        var references = factors.SelectMany(x => x.References).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var raw = 20 + factors.Sum(x => x.Points);
        var score = Math.Clamp(raw, 0, 100);
        var beforeDocument = score;
        if (documented is not null) score = documented.High ? Math.Max(80, score) : Math.Min(100, score + 5);
        references = references.Concat(documented?.References ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var reason = $"{score}/100：基本20点（不明な通信を対象外にしない）" +
            string.Concat(factors.Select(x => $"／+{x.Points} {x.Reason} [{string.Join("、", x.References)}]")) +
            (factors.Count == 0 ? "。選択されたガイドラインに対応する追加の判断材料はありません。" : "。主要機能の加点は最大1件、同じ観点は重複加点しません。") +
            (raw > 100 ? $" 合計{raw}点を上限100点へ丸めています。" : "") +
            (documented is null ? " 文書由来の重要機能との一致は未確認（文書なし・抽出未完了・該当なしを含む）。" :
                $" 文書照合：{documented.Reason} [{string.Join("、", documented.References)}]。" +
                (documented.High ? $"文書で高優先の機能に一致したため、最低80点として{beforeDocument}→{score}点。" : $"文書の機能と対応したため+5点、{beforeDocument}→{score}点。") +
                " 文書の記載であり、機能の実装・安全性を確認した事実とは区別します。") +
            (request.RequestBodyCaptured ? " 要求本文から項目名・情報種別を解析済み。" : " 要求本文は未取得のため、本文の内容は未評価。") +
            (request.RequestChangeBaselineMatched ? $" 同一捕捉・同一URL・同一認証文脈の先行応答と比較し、変更{(request.RequestChangedFieldNames?.Length ?? 0)}項目を採点。未変更の同梱項目は重要機能の加点に使わず、要求全体の入力点は残します。" :
                request.RequestBodyCaptured ? " 変更差分は未確認であり、項目の存在だけで権限・システム設定の変更を断定しません。" : "") +
            string.Join(" ", request.RequestContentLimitations ?? []);
        return new(score, reason, references);

        void Add(List<(int Points, string Reason, string[] References)> target, bool match, int points, string reason, string focus)
        {
            if (!match) return;
            var references = References(options, focus, Ai.IsMatch(path) || semantics.Contains("ai-input"), Cloud.IsMatch(path));
            if (references.Length > 0) target.Add((points, reason, references));
        }
    }

    public static PriorityAssessment WithRoles(PriorityAssessment assessment, IEnumerable<string> roles, GuidelineSelectionOptions options)
    {
        // Historical anonymous labels can include the interactive login transition.
        // Require two named authenticated roles before adding a cross-role bonus;
        // an anonymous/authenticated pair alone is not proof of that boundary.
        var knownRoles = roles.Select(ObservedRequestIdentity.NormalizeRole).Where(ObservedRequestIdentity.IsAuthenticatedRole).Distinct(StringComparer.OrdinalIgnoreCase);
        if (assessment.Score == 0 || knownRoles.Count() < 2) return assessment;
        var references = References(options, "authorization", assessment.References.Any(x => x.Contains("AISVS", StringComparison.Ordinal)),
            assessment.References.Any(x => x.Contains("Cloud Native", StringComparison.Ordinal)));
        if (references.Length == 0) return assessment;
        var score = Math.Min(100, assessment.Score + 5);
        return new(score, assessment.Reason + $"／+5 複数の認証済みロールを観測（ロール間の応答差・認可は要確認） [{string.Join("、", references)}]。最終{score}/100。",
            assessment.References.Concat(references).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static string[] ScoringFields(ObservedRequest request) => (request.RequestPriorityEvidence?.Length ?? 0) > 0 ? request.RequestPriorityEvidence!.SelectMany(x => x.FieldNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : request.RequestFieldNames ?? [];
    public static string[] ScoringSignals(ObservedRequest request) => (request.RequestPriorityEvidence?.Length ?? 0) > 0 ? request.RequestPriorityEvidence!.SelectMany(x => x.SemanticSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : request.RequestSemanticSignals ?? [];
    public static string[] ScoringOperations(ObservedRequest request) => (request.RequestPriorityEvidence?.Length ?? 0) > 0 ? request.RequestPriorityEvidence!.SelectMany(x => x.OperationTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : request.RequestOperationTerms ?? [];

    private static string[] References(GuidelineSelectionOptions options, string focus, bool aiContext = false, bool cloudContext = false)
    {
        // Chapter/test areas indicate topics to verify, never a detected violation.
        var general = focus is not ("ai" or "cloud");
        var result = new List<string>();
        if (general && options.WebAppPentestGuidelines) result.Add("WebAppPentestGuidelines v1.2（" + Topic(focus) + "）");
        if (general && options.OwaspTop10) result.Add("OWASP Top 10:2025 " + (focus switch
        {
            "authentication" => "A07", "authorization" or "files" or "integration" or "state" or "redirect" => "A01",
            "business" => "A06", "input" or "api" => "A05", "errors" => "A10", _ => "A02"
        }));
        if (general && options.Asvs) result.Add("OWASP ASVS 5.0 " + (focus switch
        {
            "authentication" => "V6/V7", "authorization" or "state" => "V8", "files" => "V5",
            "integration" or "input" => "V1/V2", "business" => "V2", "api" => "V4", "errors" => "V16", "redirect" => "V3", _ => "V13"
        }));
        if (general && options.Wstg) result.Add("OWASP WSTG 4.2（" + Topic(focus) + "）");
        if (general && options.Ds221) result.Add("デジタル庁 DS-221（" + Topic(focus) + "・案件への適用要確認）");
        if (focus == "ai" && options.Aisvs) result.Add("OWASP AISVS 1.0（AI機能への適用要確認）");
        if (focus == "cloud" && options.CloudNativeTop10) result.Add("OWASP Cloud Native Top 10（構成への適用要確認）");
        if (general && aiContext && options.Aisvs) result.Add("OWASP AISVS 1.0（" + Topic(focus) + "・AI機能への適用要確認）");
        if (general && cloudContext && options.CloudNativeTop10) result.Add("OWASP Cloud Native Top 10（" + Topic(focus) + "・構成への適用要確認）");
        return result.ToArray();
    }

    private static string Topic(string focus) => focus switch
    {
        "authentication" => "認証・セッション", "authorization" => "認可", "files" => "ファイル処理",
        "integration" => "外部接続・転送", "business" => "業務ロジック", "input" => "入力検証",
        "api" => "API", "state" => "状態変更・認可", "errors" => "例外処理", "redirect" => "リダイレクト", _ => "情報収集"
    };
    private static string[] QueryKeys(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => Uri.UnescapeDataString(x.Split('=', 2)[0])).Where(x => !string.IsNullOrWhiteSpace(x) && !CacheKeys.Contains(x))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string SplitWords(string value) => Regex.Replace(value, "([a-z])([A-Z])", "$1/$2");
    private static Regex Words(string words) => new($@"(?:^|[/_.-])(?:{words})(?=$|[/_.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

public sealed record PriorityAssessment(int Score, string Reason, string[] References);
