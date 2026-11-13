using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class RunObservationCollector
{
    public async Task<int> CollectAsync(string directory, string allowedOrigins, string role)
    {
        var importer = new RequestImporter();
        var roleChanges = new List<(DateTimeOffset At, string Role)>();
        var changesPath = Path.Combine(directory, "authentication-changes.jsonl");
        if (File.Exists(changesPath))
            foreach (var line in await File.ReadAllLinesAsync(changesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var change = JsonDocument.Parse(line);
                var root = change.RootElement;
                var changedRole = root.TryGetProperty("role", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                roleChanges.Add((root.GetProperty("observedAt").GetDateTimeOffset(), string.IsNullOrWhiteSpace(changedRole) ? "認証状態未確認" : changedRole));
            }
        roleChanges.Sort((a, b) => a.At.CompareTo(b.At));
        string RoleAt(DateTimeOffset time, string fallback)
        {
            var change = roleChanges.LastOrDefault(x => x.At <= time);
            return change.Role ?? fallback;
        }
        var result = new Dictionary<string, ObservedRequest>(StringComparer.Ordinal);
        var operationBases = new HashSet<string>(StringComparer.Ordinal);
        var plainBases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "fast-observed-requests.jsonl", "ai-observed-requests.jsonl", "observed-requests.jsonl" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            foreach (var r in await importer.ImportAsync(path))
            {
                if (!Allowed(r, allowedOrigins)) continue;
                r.Role = RoleAt(r.ObservedAt, r.Role);
                var key = ObservedRequestIdentity.Build(r);
                if (!result.ContainsKey(key)) result[key] = r;
            }
        }
        DateTimeOffset? authenticationTime = null;
        string verifiedRole = "認証状態未確認";
        var navigationPath = Path.Combine(directory, "authentication-navigation.json");
        if (File.Exists(navigationPath))
        {
            using var navigation = JsonDocument.Parse(await File.ReadAllTextAsync(navigationPath));
            var root = navigation.RootElement;
            if (root.GetProperty("status").GetString() == "authenticated" && root.GetProperty("loginFormVisible").ValueKind == JsonValueKind.False && root.GetProperty("authenticatedIndicatorVisible").ValueKind == JsonValueKind.True)
            {
                var landing = root.GetProperty("actualUrl").GetString()!;
                AuthenticationFlow.EnsureNavigationAllowed(landing, allowedOrigins);
                verifiedRole = root.GetProperty("activeRole").GetString() ?? role;
                if (AuthenticationFlow.IsAnonymous(verifiedRole)) verifiedRole = "認証状態未確認";
                authenticationTime = result.Values.Where(x => AuthenticationFlow.DisplayUrl(x.Url) == landing && ObservedRequestIdentity.IsAuthenticatedRole(x.Role))
                    .Select(x => (DateTimeOffset?)x.ObservedAt).Min() ?? root.GetProperty("checkedAt").GetDateTimeOffset();
            }
        }
        var captureNames = new List<string> { "fast-capture.har", "zap-fast-capture.har", "zap-ai-capture.har" };
        // Keep the valid browser original useful when final wire serialization
        // failed. This is partial evidence, not a successful finalized capture.
        if (!File.Exists(Path.Combine(directory, "fast-capture.har"))) captureNames.Add("browser-capture.har");
        foreach (var name in captureNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            foreach (var r in await importer.ImportAsync(path))
            {
                if (!Allowed(r, allowedOrigins)) continue;
                var match = result.Values.Where(x => x.Url == r.Url && x.Method == r.Method && Math.Abs((x.ObservedAt - r.ObservedAt).TotalSeconds) < 5)
                    .MinBy(x => Math.Abs((x.ObservedAt - r.ObservedAt).TotalSeconds));
                var recordedRole = string.IsNullOrWhiteSpace(r.Role) ? null : r.Role;
                r.Role = RoleAt(r.ObservedAt, recordedRole ?? match?.Role ??
                    (authenticationTime.HasValue && r.ObservedAt >= authenticationTime ? verifiedRole : "認証状態未確認"));
                r.Source = name;
                var baseKey = r.Method.ToUpperInvariant() + "\n" + r.Url + "\n" + ObservedRequestIdentity.NormalizeRole(r.Role);
                if (RequestOperationIdentity.BodyKey(r).Length > 0) operationBases.Add(baseKey); else plainBases.Add(baseKey);
                var key = ObservedRequestIdentity.Build(r);
                if (!result.TryGetValue(key, out var old)) result[key] = r;
                else
                {
                    RequestContentSummary.Merge(old, r);
                    if (!old.PageInspected && r.ObservedAt > old.ObservedAt) { old.StatusCode = r.StatusCode; old.ContentType = r.ContentType; old.ObservedAt = r.ObservedAt; }
                }
            }
        }
        foreach (var pair in result.Where(x => RequestOperationIdentity.BodyKey(x.Value).Length == 0).ToArray())
        {
            var r = pair.Value;
            var key = r.Method.ToUpperInvariant() + "\n" + r.Url + "\n" + ObservedRequestIdentity.NormalizeRole(r.Role);
            if (operationBases.Contains(key) && !plainBases.Contains(key)) result.Remove(pair.Key);
        }
        var output = Path.Combine(directory, "observed-requests.jsonl");
        var temp = output + ".tmp";
        await File.WriteAllLinesAsync(temp, result.Values.OrderBy(x => x.ObservedAt).Select(x => JsonSerializer.Serialize(x)));
        File.Move(temp, output, true);
        return result.Count;
    }
    private static bool Allowed(ObservedRequest request, string origins)
    {
        if (request.StatusCode is <= 0) return false;
        try { ApiRequestFormatter.EnsureAllowed(request.Url, origins); return true; }
        catch (InvalidDataException) { return false; }
    }
}
