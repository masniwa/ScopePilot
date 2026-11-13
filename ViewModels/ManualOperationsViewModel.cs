using ScopePilot.Infrastructure;
using ScopePilot.Services;

namespace ScopePilot.ViewModels;

public sealed partial class MainViewModel
{
    private bool _readingManualOperations;
    public bool CanViewManualOperations => SelectedRun is not null && !IsRunning && !_readingManualOperations;

    public async Task<ManualOperationReadResult> ReadSelectedRunManualOperationsAsync(CancellationToken token = default)
    {
        if (!CanViewManualOperations || SelectedRun is not { } run)
            return new("missing", "実行履歴で探索を選択してください。", [], "");
        _readingManualOperations = true;
        RaisePropertyChanged(nameof(CanViewManualOperations));
        try
        {
            ProjectPaths.EnsureChild(ProjectPaths.Root(Project), run.Directory);
            return await new ManualOperationReader().ReadAsync(run.Directory, token);
        }
        catch (InvalidDataException)
        { return new("error", "手動項目を読み取れません。選択した実行の保存先を確認してください。", [], ""); }
        finally { _readingManualOperations = false; RaisePropertyChanged(nameof(CanViewManualOperations)); }
    }
}

public sealed class ManualOperationsWindowViewModel : ObservableObject
{
    private ManualOperationItem? _selectedItem;
    public ManualOperationsWindowViewModel(string runDescription, ManualOperationReadResult result)
    {
        RunDescription = runDescription;
        Status = result.Status;
        Message = result.Message;
        Items = result.Items;
        _selectedItem = Items.FirstOrDefault();
    }
    public string RunDescription { get; }
    public string Status { get; }
    public string Message { get; }
    public IReadOnlyList<ManualOperationItem> Items { get; }
    public string Heading => Status is "loaded" or "empty" ? $"手動項目（{Items.Count:N0}件）" : "手動項目";
    public ManualOperationItem? SelectedItem
    {
        get => _selectedItem;
        set { if (SetProperty(ref _selectedItem, value)) RaisePropertyChanged(nameof(SelectedDetails)); }
    }
    public string SelectedDetails => SelectedItem is { } item
        ? $"操作: {item.Operation}\n対象画面: {item.Page}\n\n理由・確認内容\n{item.Reason}" +
            (item.Requests.Count > 0 ? "\n\n報告された通信\n" + string.Join('\n', item.Requests) : "")
        : "一覧の項目を選ぶと、手動での確認が必要な理由・確認内容を表示します。";
}
