using System.Globalization;
using ScopePilot.Infrastructure;

namespace ScopePilot.ViewModels;

public sealed partial class MainViewModel
{
    private string _priorityTopCount = "10";
    private string _priorityMinimum = "80";
    private const string InitialPriorityBulkStatus = "条件に合う候補だけを採用し、ほかの候補のチェックを外します。対象は全候補です。";
    private string _priorityBulkStatus = InitialPriorityBulkStatus;
    private void ResetPriorityBulkStatus() => PriorityBulkStatus = InitialPriorityBulkStatus;
    public string PriorityTopCount { get => _priorityTopCount; set => SetProperty(ref _priorityTopCount, value); }
    public string PriorityMinimum { get => _priorityMinimum; set => SetProperty(ref _priorityMinimum, value); }
    public string PriorityBulkStatus { get => _priorityBulkStatus; private set => SetProperty(ref _priorityBulkStatus, value); }
    private AsyncRelayCommand? _adoptTopPriorityCommand;
    private AsyncRelayCommand? _adoptMinimumPriorityCommand;
    public AsyncRelayCommand AdoptTopPriorityCommand => _adoptTopPriorityCommand ??= new(AdoptTopPriorityAsync, () => !IsRunning && Project.Candidates.Count > 0);
    public AsyncRelayCommand AdoptMinimumPriorityCommand => _adoptMinimumPriorityCommand ??= new(AdoptMinimumPriorityAsync, () => !IsRunning && Project.Candidates.Count > 0);

    public async Task AdoptTopPriorityAsync()
    {
        if (IsRunning) return;
        if (!int.TryParse(PriorityTopCount?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
        { PriorityBulkStatus = "上位件数は1以上の整数を入力してください。採用状態は変更していません。"; return; }
        var selected = Project.Candidates.OrderByDescending(x => x.Priority).ThenBy(x => x.Pattern, StringComparer.Ordinal).Take(count).ToHashSet();
        await ApplyPrioritySelectionAsync(x => selected.Contains(x));
        PriorityBulkStatus = $"全候補の上位{selected.Count:N0}件だけを採用しました（指定{count:N0}件）。［保存］で案件へ反映します。";
    }

    public async Task AdoptMinimumPriorityAsync()
    {
        if (IsRunning) return;
        if (!int.TryParse(PriorityMinimum?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var score) || score is < 0 or > 100)
        { PriorityBulkStatus = "優先度の下限は0〜100の整数を入力してください。採用状態は変更していません。"; return; }
        await ApplyPrioritySelectionAsync(x => x.Priority >= score);
        PriorityBulkStatus = $"全候補のうち{score}点以上の{Project.Candidates.Count(x => x.Selected):N0}件だけを採用しました。［保存］で案件へ反映します。";
    }

    private async Task ApplyPrioritySelectionAsync(Func<ScopePilot.Domain.DiagnosticCandidate, bool> selected)
    {
        foreach (var candidate in Project.Candidates)
        {
            candidate.Selected = selected(candidate);
            UpdateCandidateDecisionCache(candidate);
        }
        await RecordProjectChangeAsync();
        RefreshFilteredCandidates();
        RefreshSummary();
        RefreshBurpAiPrompt();
    }
}
