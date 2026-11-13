using System.Text.RegularExpressions;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record DocumentPriorityMatch(bool High, string Reason, string[] References);

public static class DocumentPriorityEvidence
{
    public static DocumentHints? ValidHints(EngagementProject project)
    {
        var document = project.ExplorationDocument;
        if (document?.Hints is null || ExplorationDocumentService.Documents(document).Count == 0) return null;
        return DocumentHintService.IsUsable(document.Hints, DocumentHintService.Sha256(ExplorationDocumentService.CombinedContent(document))) ? document.Hints : null;
    }

    public static DocumentPriorityMatch? Match(ObservedRequest request, DocumentHints? hints, bool includePageTitle = true)
    {
        if (hints is null || hints.Status is not ("completed" or "partial")) return null;
        // Match observed paths and semantic summaries, not secret query/body values.
        var path = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : "";
        var text = string.Join(' ', new[] { path, includePageTitle ? request.PageTitle : "" }
            .Concat(request.FormFieldNames ?? []).Concat(DiagnosticPriorityPolicy.ScoringFields(request))
            .Concat(DiagnosticPriorityPolicy.ScoringOperations(request)).Concat((request.RequestOperations ?? [])
                .Where(x => DiagnosticPriorityPolicy.ScoringFields(request).Any(field => field == x.Name || x.Name.StartsWith(field + ".", StringComparison.OrdinalIgnoreCase))).Select(x => x.DisplayValue))
            .Concat(RequestOperationIdentity.QueryValues(request.Url).Select(RequestOperationIdentity.DisplayValue)).Concat(DiagnosticPriorityPolicy.ScoringSignals(request).Select(Label)));
        var matches = new List<(bool High, string Reason, string Reference)>();
        foreach (var endpoint in hints.Endpoints ?? [])
            if (uri is not null && Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var documented) &&
                uri.GetLeftPart(UriPartial.Authority).Equals(documented.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
                PathPattern(uri.AbsolutePath) == PathPattern(documented.AbsolutePath))
                matches.Add((endpoint.Priority == "high", $"文書記載のエンドポイントに一致：{endpoint.Reason}", Reference(hints, endpoint.SourceReference)));
        foreach (var rule in hints.PriorityRules ?? [])
            if ((rule.Keywords ?? []).Any(keyword => MatchesKeyword(text, keyword) ||
                DiagnosticPriorityPolicy.ScoringOperations(request).Contains(keyword, StringComparer.OrdinalIgnoreCase)))
                matches.Add((rule.Priority == "high", $"文書の重要機能・データ規則に一致：{rule.Category}（{rule.Note}）", Reference(hints, rule.SourceReference)));
        if (matches.Count == 0) return null;
        return new(matches.Any(x => x.High), string.Join("／", matches.Select(x => x.Reason).Distinct().Take(5)), matches.Select(x => x.Reference).Distinct().ToArray());
    }

    private static string Reference(DocumentHints hints, string reference) => $"入力ドキュメント {hints.SourceDocumentName}：{(string.IsNullOrWhiteSpace(reference) ? "抽出済み補助情報（詳細出典未記載）" : reference)}";
    private static bool MatchesKeyword(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2 || keyword.Length > 64) return false;
        if (new[] { "get", "post", "put", "patch", "delete", "api", "http", "https", "id", "request", "response" }.Contains(keyword, StringComparer.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(keyword, "^[A-Za-z0-9_]+$"))
            return Regex.IsMatch(text, "(?<![A-Za-z0-9_])" + Regex.Escape(keyword) + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
    private static string PathPattern(string path) => UrlPatternNormalizer.NormalizeForSelection("https://scopepilot.invalid" + path);
    private static string Label(string signal) => signal switch
    {
        "personal-data" => "個人情報 personal-data email address phone", "financial-data" => "金額 決済 口座 financial-data payment balance",
        "authorization-data" => "権限 authorization role permission", "authentication-data" => "認証 authentication password",
        "file-upload" => "ファイルアップロード file upload", "external-target" => "外部接続 external URL redirect", "ai-input" => "AI入力 prompt", _ => ""
    };
}
