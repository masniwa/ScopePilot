using Microsoft.Win32;
using ScopePilot.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScopePilot;

public partial class LogWindow : Window
{
    private readonly Func<string> _readLog;
    private readonly Func<string, Task<string>> _saveLog;
    private readonly Func<string, Task<ClipboardCopyResult>> _copy;

    internal LogWindow(Func<string> readLog, Func<string, Task<string>> saveLog,
        Func<string, Task<ClipboardCopyResult>>? copy = null)
    {
        _readLog = readLog;
        _saveLog = saveLog;
        _copy = copy ?? ClipboardService.CopyTextAsync;
        InitializeComponent();
        RefreshLog();
        SourceInitialized += (_, _) => ThemeService.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ThemeService.Apply(this));

    internal void RefreshLog()
    {
        LogTextBox.Text = _readLog();
        ResultText.Text = "表示中のログを更新しました。";
        CopyAllButton.IsEnabled = SaveLogButton.IsEnabled = LogTextBox.Text.Length > 0;
        UpdateSelectionButton();
    }

    private void UpdateSelectionButton()
    {
        if (CopySelectionButton is not null) CopySelectionButton.IsEnabled = LogTextBox.SelectionLength > 0;
    }
    private void Log_SelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionButton();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLog();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { LogTextBox.Focus(); LogTextBox.SelectAll(); }
    private async void CopyAll_Click(object sender, RoutedEventArgs e) => await CopyAsync(LogTextBox.Text);
    private async void CopySelection_Click(object sender, RoutedEventArgs e) => await CopyAsync(LogTextBox.SelectedText);
    private void Copy_CanExecute(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = LogTextBox.SelectionLength > 0; e.Handled = true; }
    private async void Copy_Executed(object sender, ExecutedRoutedEventArgs e) { e.Handled = true; await CopyAsync(LogTextBox.SelectedText); }

    internal async Task CopyAsync(string text) => ResultText.Text = (await _copy(text)).Message;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { ResultText.Text = $"保存しました: {await _saveLog(LogTextBox.Text)}"; }
        catch (Exception ex) { ResultText.Text = $"ログを保存できませんでした: {ex.Message}"; }
    }
}
