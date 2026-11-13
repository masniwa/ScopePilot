using Microsoft.Win32;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using System.Windows;

namespace ScopePilot;

public partial class ManualOperationsWindow : Window
{
    public ManualOperationsWindow(string runDescription, ManualOperationReadResult result)
    {
        InitializeComponent();
        DataContext = new ManualOperationsWindowViewModel(runDescription, result);
        SourceInitialized += (_, _) => ThemeService.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => ThemeService.Apply(this));
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
