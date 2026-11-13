using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class CandidateSelector
{
    public IReadOnlyList<DiagnosticCandidate> Select(EngagementProject project) => Select(project.Requests, project.Guidelines, DocumentPriorityEvidence.ValidHints(project));

    public IReadOnlyList<DiagnosticCandidate> Select(IEnumerable<ObservedRequest> requests, GuidelineSelectionOptions? guidelines = null, DocumentHints? documentHints = null) => requests
        .Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _))
        .GroupBy(RequestOperationIdentity.Pattern, StringComparer.Ordinal)
        .Select(group => CreateCandidate(group, guidelines ?? new GuidelineSelectionOptions(), documentHints))
        .OrderByDescending(x => x.Priority)
        .ThenBy(x => x.Pattern, StringComparer.Ordinal)
        .ToArray();

    private static DiagnosticCandidate CreateCandidate(IGrouping<string, ObservedRequest> group, GuidelineSelectionOptions guidelines, DocumentHints? documentHints)
    {
        var assessed = group.Select(request =>
        {
            var assessment = GuidelineSelectionPolicy.Evaluate(request, guidelines);
            return (Request: request, Assessment: assessment, Priority: DiagnosticPriorityPolicy.Evaluate(request, assessment, guidelines, documentHints));
        }).ToArray();
        var chosen = assessed.OrderByDescending(x => x.Priority.Score)
            .ThenByDescending(x => x.Assessment.Selected)
            .ThenByDescending(x => x.Request.BurpProxyRequestId.HasValue)
            .ThenByDescending(x => x.Request.FormCount)
            .ThenByDescending(x => x.Request.PageInspected)
            .First();
        var evidence = assessed.Where(x => x.Assessment.Selected == chosen.Assessment.Selected)
            .SelectMany(x => x.Assessment.Signals).Distinct(StringComparer.Ordinal).ToArray();
        var roles = assessed.Select(x => ObservedRequestIdentity.NormalizeRole(x.Request.Role))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var priority = DiagnosticPriorityPolicy.WithRoles(chosen.Priority, roles, guidelines);
        var references = assessed.SelectMany(x => x.Assessment.References).Concat(priority.References).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var count = assessed.Length;
        var reason = chosen.Assessment.Reason;
        var selected = priority.Score > 0;
        if (selected && !chosen.Assessment.Selected) reason = "静的な形式ですが、入力ドキュメントに機能の記載があるため診断候補として残します。";
        if (count > 1) reason += $" 同一URL構造の{count:N0}件をまとめ、このURLを代表にしています。";
        var trace = string.Join(Environment.NewLine, new[]
        {
            $"1. URL構造: {group.Key}（同一構造 {count:N0}件）",
            $"2. 代表通信: {chosen.Request.Method} {chosen.Request.Url}",
            $"3. 応答: Status {chosen.Request.StatusCode?.ToString() ?? "不明"}, Content-Type {(string.IsNullOrWhiteSpace(chosen.Request.ContentType) ? "不明" : chosen.Request.ContentType)}",
            $"4. 画面確認: {(chosen.Request.PageInspected ? $"実施、フォーム{chosen.Request.FormCount}件" : "未実施または記録なし")}",
            $"5. 観測ロール: {string.Join("、", roles)}",
            $"6. 判定材料: {string.Join("／", evidence)}",
            $"7. 参照基準: {string.Join("、", references)}",
            $"8. 診断優先度: {priority.Reason}",
            $"自動選定: {(selected ? "採用候補" : "除外候補")}。優先度はScopePilot独自の診断順序の目安であり、脆弱性の確率・深刻度やガイドライン公式の点数ではありません。"
        });
        return new DiagnosticCandidate
        {
            Selected = selected,
            Priority = priority.Score,
            PriorityReason = priority.Reason,
            PriorityScoringVersion = DiagnosticPriorityPolicy.Version,
            Confidence = selected != chosen.Assessment.Selected ? "低" : chosen.Assessment.Confidence,
            Category = chosen.Assessment.Category,
            Decision = selected ? "候補" : "除外候補",
            Reason = reason,
            DecisionTrace = trace,
            DiagnosticCaution = chosen.Assessment.Caution,
            GuidelineBasis = string.Join("、", references),
            Pattern = group.Key,
            SimilarCount = count,
            Representative = chosen.Request
        };
    }

    public void RefreshPriorities(EngagementProject project)
    {
        // Recalculate legacy scores in memory, preserving user decisions and the file on disk.
        project.Requests ??= [];
        project.Candidates ??= [];
        project.Guidelines ??= new();
        var hints = DocumentPriorityEvidence.ValidHints(project);
        var groups = project.Requests.Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _))
            .GroupBy(Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        foreach (var candidate in project.Candidates)
        {
            var requests = groups.GetValueOrDefault(candidate.Pattern) ?? [candidate.Representative];
            var rescored = requests.GroupBy(_ => candidate.Pattern).Select(x => CreateCandidate(x, project.Guidelines, hints)).Single();
            candidate.Priority = rescored.Priority;
            candidate.PriorityReason = rescored.PriorityReason;
            candidate.PriorityScoringVersion = rescored.PriorityScoringVersion;
            candidate.Confidence = rescored.Confidence;
            candidate.GuidelineBasis = rescored.GuidelineBasis;
            candidate.DecisionTrace = rescored.DecisionTrace;
            candidate.Representative = rescored.Representative;
        }
    }

    private static string Key(ObservedRequest request) => RequestOperationIdentity.Pattern(request);
}
