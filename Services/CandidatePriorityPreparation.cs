using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record PriorityPreparationResult(int HarFiles, int MatchedRequests, string[] Limitations);

public static class CandidatePriorityPreparation
{
    // Reuse existing capture evidence. Never send requests or invent requests from documents.
    public static async Task<PriorityPreparationResult> EnrichAsync(EngagementProject project, CancellationToken token = default, string? runDirectory = null)
    {
        var runs = runDirectory ?? Path.Combine(ProjectPaths.Root(project), "runs");
        ProjectPaths.EnsureChild(ProjectPaths.Root(project), runs);
        if (!Directory.Exists(runs)) return new(0, 0, ["保存済みHARがないため、既に取り込まれた通信情報の範囲で採点します。"]);
        var requests = project.Requests.GroupBy(Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var identities = project.Requests.GroupBy(ObservedRequestIdentity.Build).ToDictionary(x => x.Key, x => x.First());
        var operationBases = new HashSet<string>(StringComparer.Ordinal);
        var plainBases = new HashSet<string>(StringComparer.Ordinal);
        var limitations = new List<string>();
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(runs, "*.har", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            ProjectPaths.EnsureChild(ProjectPaths.Root(project), path);
            try
            {
                foreach (var incoming in await new RequestImporter().ImportAsync(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (!requests.TryGetValue(Key(incoming), out var existing)) continue;
                    var nearest = existing.MinBy(x => Math.Abs((x.ObservedAt - incoming.ObservedAt).TotalSeconds))!;
                    if (string.IsNullOrWhiteSpace(incoming.Role))
                    {
                        var nearby = existing.Where(x => Math.Abs((x.ObservedAt - incoming.ObservedAt).TotalSeconds) <= 5).ToArray();
                        var fingerprintMatches = nearby.Where(x => !string.IsNullOrEmpty(x.RequestFingerprint) && x.RequestFingerprint == incoming.RequestFingerprint).ToArray();
                        var roles = (fingerprintMatches.Length > 0 ? fingerprintMatches : nearby).Select(x => ObservedRequestIdentity.NormalizeRole(x.Role)).Distinct(StringComparer.Ordinal).ToArray();
                        incoming.Role = roles.Length == 1 ? roles[0] : "認証状態未確認";
                    }
                    incoming.PageInspected = nearest.PageInspected;
                    incoming.FormCount = nearest.FormCount;
                    incoming.FormFieldNames = nearest.FormFieldNames;
                    incoming.PageTitle = nearest.PageTitle;
                    var baseKey = Key(incoming) + "\n" + ObservedRequestIdentity.NormalizeRole(incoming.Role);
                    if (RequestOperationIdentity.BodyKey(incoming).Length > 0) operationBases.Add(baseKey);
                    else plainBases.Add(baseKey);
                    var identity = ObservedRequestIdentity.Build(incoming);
                    if (!identities.TryGetValue(identity, out var request))
                    {
                        project.Requests.Add(incoming);
                        identities[identity] = incoming;
                        request = incoming;
                    }
                    else RequestContentSummary.Merge(request, incoming);
                    if (incoming.RequestBodyCaptured) matched.Add(request.Id);
                }
                files++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            { limitations.Add($"{Path.GetFileName(path)}の要求内容を読み取れず、当該部分は未評価です。"); }
            await Task.Yield();
        }
        foreach (var request in project.Requests.Where(x => RequestOperationIdentity.BodyKey(x).Length == 0).ToArray())
        {
            var key = Key(request) + "\n" + ObservedRequestIdentity.NormalizeRole(request.Role);
            if (operationBases.Contains(key) && !plainBases.Contains(key)) project.Requests.Remove(request);
        }
        return new(files, matched.Count, limitations.ToArray());
    }

    private static string Key(ObservedRequest request) => request.Method.ToUpperInvariant() + " " + request.Url;
}
