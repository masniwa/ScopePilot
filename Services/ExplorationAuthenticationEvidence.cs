using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScopePilot.Services;

// Correlate the browser's manual authentication preparation with this run.
// Never forward request headers, credential values, or response bodies to the AI.
internal static class ExplorationAuthenticationEvidence
{
    internal const string FileName = "authentication-request-evidence.json";
    internal sealed record Evidence(int RequestId, string Method, string Url, int Status,
        DateTimeOffset RequestAt, DateTimeOffset CheckedAt, string ActiveRole);

    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static bool Time(JsonElement value, string key, out DateTimeOffset at) => DateTimeOffset.TryParse(Text(value, key), out at);
    private static string Route(Uri uri) => uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;
    private static bool CredentialKey(string key) => Regex.IsMatch(key, "^(?:password|passwd|pass|credential|credentials)$", RegexOptions.IgnoreCase);

    private static bool CredentialBody(JsonElement request)
    {
        var encoded = Text(request, "postDataBase64");
        if (encoded.Length == 0) return false;
        try
        {
            var body = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            if (body.TrimStart().StartsWith('{'))
            {
                using var json = JsonDocument.Parse(body);
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Any(p => CredentialKey(p.Name));
            }
            return body.Split('&').Any(part => CredentialKey(Uri.UnescapeDataString(part.Split('=', 2)[0])));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return false; }
    }

    internal static bool IsLoginOperation(string operation)
    {
        var name = Regex.Replace(operation.Trim(), @"^[A-Za-z][A-Za-z0-9_-]*-\d+\s*[:：]?\s*", "");
        return Regex.IsMatch(name, @"^(?:ログイン(?:送信|操作|認証)?|サインイン(?:送信|操作)?|(?:submit\s+)?(?:log[ _-]?in|sign[ _-]?in)(?:\s+(?:request|submission|form))?)$", RegexOptions.IgnoreCase);
    }

    internal static async Task<Evidence?> CaptureAsync(string directory, CancellationToken token)
    {
        try
        {
            var navigation = Path.Combine(directory, "authentication-navigation.json");
            var journal = Path.Combine(directory, "http-transactions.jsonl");
            if (!File.Exists(navigation) || !File.Exists(journal)) return null;
            using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "engagement.json"), token));
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(navigation, token));
            var state = auth.RootElement;
            if (Text(state, "status") != "authenticated" || !state.TryGetProperty("loginFormVisible", out var visible) || visible.ValueKind != JsonValueKind.False ||
                !Time(contract.RootElement, "runStartedAt", out var started) || !Time(state, "checkedAt", out var checkedAt) || checkedAt < started) return null;
            var role = Text(state, "activeRole");
            if (string.IsNullOrWhiteSpace(role) || Regex.IsMatch(role, "未認証|未確認|^anonymous$|^guest$", RegexOptions.IgnoreCase)) return null;
            if (contract.RootElement.TryGetProperty("roles", out var roles) && !roles.EnumerateArray().Any(x => x.GetString() == role)) return null;
            var allowed = contract.RootElement.GetProperty("allowedOrigins").EnumerateArray().Select(x => new Uri(x.GetString()!).GetLeftPart(UriPartial.Authority)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Scoped(Uri uri) => uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && allowed.Contains(uri.GetLeftPart(UriPartial.Authority));
            if (!Uri.TryCreate(Text(state, "actualUrl"), UriKind.Absolute, out var landing) || !Scoped(landing)) return null;
            var forms = new HashSet<string>(StringComparer.Ordinal);
            var crawl = Path.Combine(directory, "fast-crawl-summary.json");
            if (File.Exists(crawl))
            {
                using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(crawl, token));
                if (summary.RootElement.TryGetProperty("pages", out var pages))
                    foreach (var page in pages.EnumerateArray())
                        if (page.TryGetProperty("forms", out var pageForms))
                            foreach (var form in pageForms.EnumerateArray())
                                if (form.TryGetProperty("hasPasswordField", out var password) && password.ValueKind == JsonValueKind.True && Uri.TryCreate(Text(form, "action"), UriKind.Absolute, out var action) && Scoped(action))
                                    forms.Add(Text(form, "method").ToUpperInvariant() + " " + Route(action));
            }
            var candidates = new List<(int Id, string Method, string Url, DateTimeOffset At)>();
            var responses = new Dictionary<int, (int Status, DateTimeOffset At)>();
            foreach (var line in File.ReadLines(journal))
            {
                token.ThrowIfCancellationRequested();
                JsonDocument row;
                try { row = JsonDocument.Parse(line); } catch (JsonException) { continue; } // A live final line may still be appending.
                using (row)
                {
                    var e = row.RootElement;
                    if (!e.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id)) continue;
                    var kind = Text(e, "kind");
                    if (kind is "response" or "wire-response")
                    {
                        if (e.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) && Time(e, "at", out var responseAt)) responses[id] = (code, responseAt);
                        continue;
                    }
                    if (kind != "request" || !Time(e, "at", out var at) || at < started || at > checkedAt || !Uri.TryCreate(Text(e, "url"), UriKind.Absolute, out var uri) || !Scoped(uri)) continue;
                    var method = Text(e, "method").ToUpperInvariant();
                    var credentialQuery = uri.Query.TrimStart('?').Split('&').Any(part => CredentialKey(Uri.UnescapeDataString(part.Split('=', 2)[0])));
                    var observedForm = forms.Contains(method + " " + Route(uri)) && (method == "POST" || method == "GET" && credentialQuery);
                    var credentialSubmission = method == "POST" && (Regex.IsMatch(uri.AbsolutePath, @"(?:^|/)(?:login|log[-_]?in|signin|sign[-_]?in|authenticate)(?:/|$)", RegexOptions.IgnoreCase) || CredentialBody(e));
                    if (observedForm || credentialSubmission) candidates.Add((id, method, Route(uri), at));
                }
            }
            var matched = candidates.Where(x => responses.TryGetValue(x.Id, out var response) && response.Status is >= 200 and < 400 && response.At >= x.At && response.At <= checkedAt).OrderByDescending(x => x.At).FirstOrDefault();
            if (matched == default) return null;
            var evidence = new Evidence(matched.Id, matched.Method, matched.Url, responses[matched.Id].Status, matched.At, checkedAt, role);
            await File.WriteAllTextAsync(Path.Combine(directory, FileName), JsonSerializer.Serialize(new { scope = "same-run authentication preparation", evidence.RequestId, evidence.Method, evidence.Url, evidence.Status, evidence.RequestAt, evidence.CheckedAt, evidence.ActiveRole }), token);
            return evidence;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException) { return null; }
    }

    internal static async Task<bool> ReconcileAsync(string directory, JsonObject report, CancellationToken token)
    {
        var evidence = await CaptureAsync(directory, token);
        if (evidence is null || report["operationCoverage"] is not JsonArray operations) return false;
        var changed = false;
        foreach (var item in operations.OfType<JsonObject>())
        {
            if (!IsLoginOperation(item["operation"]?.GetValue<string>() ?? "") || item["outcome"]?.GetValue<string>() == "observed") continue;
            item["outcome"] = "observed";
            item["page"] = evidence.Url;
            item["requests"] = new JsonArray(evidence.Method + " " + evidence.Url);
            item["reason"] = $"ScopePilotの同一runの認証準備で要求{evidence.RequestId}・HTTP{evidence.Status}を記録し、その後の認証済み到達を確認。AIパスの前でも今回の収集実績に含む。この照合情報には認証秘密値を含めない。";
            changed = true;
        }
        return changed;
    }
}
