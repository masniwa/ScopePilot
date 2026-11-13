using ScopePilot.Infrastructure;
using ScopePilot.Services;

namespace ScopePilot.ViewModels;

public sealed partial class MainViewModel
{
    private RelayCommand? _copyBurpAiPromptCommand;
    private AsyncRelayCommand? _linkBurpHistoryCommand;
    private const string InitialBurpHistoryStatus = "Burp Proxy historyのRequest IDを照合してください。接続先は現在の案件を記録したBurpプロジェクトにしてください。";
    private string _burpHistoryStatus = InitialBurpHistoryStatus;
    private void ResetBurpHistoryStatus() => BurpHistoryStatus = InitialBurpHistoryStatus;
    public string BurpAiPrompt => BurpAiPromptService.Build(Project.Candidates).Text;
    public string BurpAiPromptStatus => BurpAiPromptService.Build(Project.Candidates).Message;
    public string BurpHistoryStatus { get => _burpHistoryStatus; private set => SetProperty(ref _burpHistoryStatus, value); }
    public Action<string> CopyBurpPromptText { get; set; } = System.Windows.Clipboard.SetText;
    public RelayCommand CopyBurpAiPromptCommand => _copyBurpAiPromptCommand ??= new(() => CopyBurpPromptText(BurpAiPrompt), () => !IsRunning && BurpAiPromptService.Build(Project.Candidates).Ready);
    public AsyncRelayCommand LinkBurpHistoryCommand => _linkBurpHistoryCommand ??= new(LinkBurpHistoryAsync, () => !IsRunning);
    public bool IsBurpAiPromptTarget { get => Project.DiagnosticTools.Target == ScopePilot.Domain.HandoffTarget.BurpMcpAiPrompt; set { if (value) HandoffTargetIndex = 2; } }

    public void RefreshBurpAiPrompt()
    {
        RaisePropertyChanged(nameof(BurpAiPrompt));
        RaisePropertyChanged(nameof(BurpAiPromptStatus));
        CopyBurpAiPromptCommand.RaiseCanExecuteChanged();
        AdoptTopPriorityCommand.RaiseCanExecuteChanged();
        AdoptMinimumPriorityCommand.RaiseCanExecuteChanged();
    }

    public async Task LinkBurpHistoryAsync()
    {
        if (IsRunning || !await TryEnsureProjectAsync()) return;
        using var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation; IsRunning = true;
        try
        {
            await CandidatePriorityPreparation.EnrichAsync(Project, cancellation.Token);
            var linked = await new BurpHistoryLinkService().LinkAsync(Project, cancellation.Token);
            BurpHistoryStatus = linked.Message; Log(linked.Message);
            GenerateCandidates();
            await RecordProjectChangeAsync();
        }
        catch (OperationCanceledException) { BurpHistoryStatus = "Burp IDの照合を中止しました。未照合のIDは生成しません。"; }
        catch (Exception ex) { BurpHistoryStatus = "Burp履歴の照合に失敗しました。"; Log($"Burp履歴の照合: {ex.Message}"); }
        finally { _runCancellation = null; IsRunning = false; RefreshBurpAiPrompt(); }
    }
}
