using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// App-level navigation state and dialog triggers.
/// Maps from App.tsx + top-level state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isBackgroundSettingsOpen;

    [ObservableProperty]
    private bool _isAgentFileOpen;

    [ObservableProperty]
    private bool _isBetaGateOpen;

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void OpenBackgroundSettings() => IsBackgroundSettingsOpen = true;

    [RelayCommand]
    private void CloseBackgroundSettings() => IsBackgroundSettingsOpen = false;

    [RelayCommand]
    private void OpenAgentFile() => IsAgentFileOpen = true;

    [RelayCommand]
    private void CloseAgentFile() => IsAgentFileOpen = false;
}
