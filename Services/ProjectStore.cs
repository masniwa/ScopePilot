using System.Text.Json;
using System.Text.Json.Serialization;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class ProjectStore
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string Snapshot(EngagementProject project) => JsonSerializer.Serialize(project, _options);

    public async Task SaveAsync(EngagementProject project)
    {
        var directory = ProjectPaths.Root(project);
        ProjectPaths.CheckProjectFile(project);
        Directory.CreateDirectory(directory);
        ProjectPaths.EnsureChild(directory, project.ProjectFilePath);
        project.UpdatedAt = DateTimeOffset.Now;
        var temporary = project.ProjectFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var snapshot = JsonSerializer.SerializeToNode(project, _options)!;
            foreach (var collection in new[] { "ApiRequests", "Findings" })
                foreach (var item in snapshot[collection]!.AsArray())
                    if (item?["SourceRunDirectory"]?.GetValue<string>() is { Length: > 0 } path)
                        item["SourceRunDirectory"] = ProjectPaths.Relative(project, path);
            await File.WriteAllTextAsync(temporary, snapshot.ToJsonString(_options));
            File.Move(temporary, project.ProjectFilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<EngagementProject> LoadAsync(string path)
    {
        path = Path.GetFullPath(path);
        ProjectPaths.EnsureChild(Path.GetDirectoryName(path)!, path);
        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer.DeserializeAsync<EngagementProject>(stream, _options)
            ?? throw new InvalidDataException("プロジェクトファイルが空です。");
        if (project.FormatVersion != 1) throw new InvalidDataException("未対応のプロジェクト形式です。");
        // Repair only the previous untouched default. Custom phrases and an
        // explicitly empty prohibition list keep their exact meaning.
        if (project.ForbiddenActions == EngagementProject.LegacyDefaultForbiddenActions)
            project.ForbiddenActions = EngagementProject.DefaultForbiddenActions;
        project.ProjectFilePath = path;
        ProjectPaths.CheckProjectFile(project);
        project.ApiRequests ??= [];
        project.Findings ??= [];
        project.Inputs ??= [];
        project.BrunoVerifications ??= [];
        project.ExplorationDocument ??= new();
        project.ExplorationAi ??= new();
        project.ExplorationProxy ??= new();
        foreach (var document in ExplorationDocumentService.Documents(project.ExplorationDocument))
            if (document.Source is { } source) _ = ProjectPaths.Resolve(project, source.LocalPath);
        foreach (var reference in project.Inputs) _ = ProjectPaths.Resolve(project, reference.LocalPath);
        foreach (var verification in project.BrunoVerifications) _ = ProjectPaths.Resolve(project, verification.Exchange.LocalPath);
        foreach (var item in project.ApiRequests)
            item.SourceRunDirectory = ResolveStoredPath(project, item.SourceRunDirectory);
        foreach (var item in project.Findings)
            item.SourceRunDirectory = ResolveStoredPath(project, item.SourceRunDirectory);
        UpgradeCaseSensitiveCandidatePatterns(project);
        new CandidateSelector().RefreshPriorities(project);
        return project;
    }

    private static void UpgradeCaseSensitiveCandidatePatterns(EngagementProject project)
    {
        project.Candidates ??= [];
        project.AdoptedCandidatePatterns ??= [];
        project.DeferredCandidatePatterns ??= [];
        project.ExplicitlyReviewedCandidatePatterns ??= [];
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in project.Candidates)
        {
            var oldPattern = RequestOperationIdentity.NormalizePattern(candidate.Pattern);
            var current = RequestOperationIdentity.Pattern(candidate.Representative);
            // Older versions folded URL paths to lower case. Only migrate a
            // stored candidate when its observed representative proves the case;
            // do not turn unrelated case-distinct URLs into the same candidate.
            if (oldPattern == current || !oldPattern.Equals(current, StringComparison.OrdinalIgnoreCase) ||
                PatternSuffix(oldPattern) != PatternSuffix(current)) continue;
            changes[oldPattern] = current;
            candidate.Pattern = current;
        }
        foreach (var cache in new[] { project.AdoptedCandidatePatterns, project.DeferredCandidatePatterns, project.ExplicitlyReviewedCandidatePatterns })
            for (var index = 0; index < cache.Count; index++)
                if (changes.TryGetValue(RequestOperationIdentity.NormalizePattern(cache[index]), out var current))
                    cache[index] = current;
    }

    private static string PatternSuffix(string pattern)
    {
        var index = pattern.IndexOfAny(['?', '#']);
        return index < 0 ? "" : pattern[index..];
    }

    public static string ResolveStoredPath(EngagementProject project, string path) => string.IsNullOrWhiteSpace(path)
        ? "" : ProjectPaths.Resolve(project, path);
}
