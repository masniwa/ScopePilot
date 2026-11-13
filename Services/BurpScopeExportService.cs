using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record BurpScopeExportResult(string Directory, string RegexPath, string CombinedRegexPath, string UrlPath,
    string JsonPath, string ChecklistPath, int SelectedCount, string[] CombinedRegexes);

public sealed class BurpScopeExportService
{
    private readonly string? _exportsDirectory;

    public BurpScopeExportService(string? exportsDirectory = null) => _exportsDirectory = exportsDirectory;

    public async Task<BurpScopeExportResult> ExportAsync(EngagementProject project)
    {
        var selected = project.Candidates.Where(x => x.Selected).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("採用された診断対象候補がありません。候補一覧で採用してください。");

        foreach (var candidate in selected) ApiRequestFormatter.EnsureAllowed(candidate.Representative.Url, project.AllowedOrigins);
        var regexes = selected.Select(BuildScopeRegex).Distinct(StringComparer.Ordinal).ToArray();
        var combinedRegexes = CombineRegexes(regexes);
        if (selected.Any(candidate => !combinedRegexes.Any(regex => Regex.IsMatch(candidate.Representative.Url, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))))
            throw new InvalidDataException("採用URLに一致しないScope正規表現があります。候補を再計算して確認してください。");

        var root = _exportsDirectory is null ? ProjectPaths.Category(project, "exports") : Path.Combine(_exportsDirectory, project.Id.ToString("N"));
        var directory = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(directory);

        var urls = selected.Select(x => x.Representative.Url).Where(x => Uri.TryCreate(x, UriKind.Absolute, out _))
            .Distinct(StringComparer.Ordinal).ToArray();
        var regexPath = Path.Combine(directory, "burp-scope-regex.txt");
        var combinedRegexPath = Path.Combine(directory, "burp-scope-combined-regex.txt");
        var urlPath = Path.Combine(directory, "burp-scope-urls.txt");
        var jsonPath = Path.Combine(directory, "burp-scope.json");
        var checklistPath = Path.Combine(directory, "diagnostic-checklist.tsv");

        await File.WriteAllLinesAsync(regexPath, regexes, new UTF8Encoding(false));
        await File.WriteAllLinesAsync(combinedRegexPath, combinedRegexes, new UTF8Encoding(false));
        await File.WriteAllLinesAsync(urlPath, urls, new UTF8Encoding(false));
        await File.WriteAllLinesAsync(checklistPath,
            new[] { "Method\tURL\tRole\tCategory\tReason\tDiagnosticCaution" }.Concat(selected.Select(x => string.Join('\t',
                Clean(x.Representative.Method), Clean(x.Representative.Url), Clean(x.Representative.Role), Clean(x.Category), Clean(x.Reason), Clean(x.DiagnosticCaution)))),
            new UTF8Encoding(true));
        var manifest = new
        {
            generatedAt = DateTimeOffset.Now,
            projectId = project.Id,
            projectName = project.Name,
            allowedOrigins = project.AllowedOrigins.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            selectedCandidates = selected.Select(x => new { x.Pattern, x.Category, x.Representative.Url, scopeRegex = BuildScopeRegex(x) }).ToArray(),
            deferredCandidatePatterns = project.DeferredCandidatePatterns,
            limitations = new[]
            {
                "Burp Suite Community EditionではScope設定の自動変更を行いません。burp-scope-combined-regex.txtをBurpのTarget > Scopeへ手動登録してください。",
                "Scopeは通信を許可・遮断する機能ではなく、Burpの対象表示・対象機能を絞る設定です。実際の診断範囲は案件の許可Originと照合してください。",
                "URLのScopeはHTTPメソッドや要求本文の処理指定を区別しません。個別の診断対象は候補一覧・Request IDと照合してください。"
            }
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "README.txt"),
            "Burp Suite Community Editionへの登録手順\r\n\r\n1. BurpのTarget > Scopeを開きます。\r\n2. Include in scopeのURL欄で、burp-scope-combined-regex.txtの各行をURL regexとして追加します。\r\n3. 対象Originが案件の許可範囲と一致することを確認します。\r\n4. 除外した候補はこのScope出力には含めていません。\r\n\r\nScopePilotのBurp連携タブでは、まとめた正規表現を順番にコピーできます。代表URLはburp-scope-urls.txt、診断作業表はdiagnostic-checklist.tsv、証跡と選定理由はburp-scope.jsonを参照してください。\r\n", new UTF8Encoding(false));
        return new(directory, regexPath, combinedRegexPath, urlPath, jsonPath, checklistPath, selected.Length, combinedRegexes);
    }

    internal static string BuildScopeRegex(DiagnosticCandidate candidate)
    {
        var source = candidate.Pattern;
        var separator = source.IndexOf(' ');
        if (separator >= 0 && separator + 1 < source.Length) source = source[(separator + 1)..];
        source = source.Split('#', 2)[0];
        var representative = ApiRequestFormatter.ValidateUrl(candidate.Representative.Url);
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            source = representative.GetLeftPart(UriPartial.Authority) + (source.StartsWith('/') ? source : "/" + source);

        var pathStart = source.IndexOf('/', source.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart < 0) pathStart = source.Length;
        var authority = source[..pathStart];
        var pathAndQuery = source[pathStart..];
        var queryStart = pathAndQuery.IndexOf('?');
        var path = queryStart >= 0 ? pathAndQuery[..queryStart] : pathAndQuery;
        // Old projects stored lower-cased static segments. Recover their spelling
        // from the corresponding observed URL while retaining ID placeholders.
        var templateSegments = path.Split('/');
        var observedSegments = representative.AbsolutePath.TrimEnd('/').Split('/');
        if (templateSegments.Length == observedSegments.Length)
            for (var index = 0; index < templateSegments.Length; index++)
                templateSegments[index] = RestoreObservedSegmentCase(templateSegments[index], observedSegments[index]);
        path = string.Join('/', templateSegments).TrimEnd('/');
        var regex = "^(?i:" + RegexEscape(authority) + ")" + RegexEscape(path);
        regex = ReplacePlaceholders(regex);
        return regex + "/?(?:\\?.*)?$";
    }

    private static string RegexEscape(string value) => System.Text.RegularExpressions.Regex.Escape(value);

    private static string RestoreObservedSegmentCase(string template, string observed)
    {
        var parts = Regex.Split(template, "(\\{(?:guid|date|id|value)\\})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var expression = "^" + string.Concat(parts.Select((part, index) => index % 2 == 0
            ? "(?<literal" + index + ">" + RegexEscape(part) + ")" : "[^/?#]+")) + "$";
        var match = Regex.Match(observed, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return !match.Success ? template : string.Concat(parts.Select((part, index) => index % 2 == 0 ? match.Groups["literal" + index].Value : part));
    }

    private static string ReplacePlaceholders(string value) => value
        .Replace(RegexEscape("{guid}"), "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace(RegexEscape("{date}"), "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace(RegexEscape("{id}"), "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace(RegexEscape("{value}"), "[^/?#]+", StringComparison.OrdinalIgnoreCase);

    private static string[] CombineRegexes(string[] regexes)
    {
        const int chunkSize = 20;
        return regexes.Chunk(chunkSize)
            .Select(chunk => "(?:" + string.Join('|', chunk.Select(x => "(?:" + x + ")")) + ")")
            .ToArray();
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
