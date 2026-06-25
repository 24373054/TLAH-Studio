using CommunityToolkit.Mvvm.ComponentModel;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

public partial class PrivacyDataViewModel : ObservableObject
{
    private readonly IPrivacyService _privacyService;
    private readonly IAppReleaseService _releaseService;

    [ObservableProperty]
    private PrivacySummary? _summary;

    [ObservableProperty]
    private string _diagnosticsPreview = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public PrivacyDataViewModel(IPrivacyService privacyService, IAppReleaseService releaseService)
    {
        _privacyService = privacyService;
        _releaseService = releaseService;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        ErrorMessage = null;
        Summary = await _privacyService.GetSummaryAsync(ct);
        DiagnosticsPreview = await _releaseService.GetDiagnosticsPreviewAsync(ct);
    }

    public async Task ExportAllDataAsync(string path, CancellationToken ct = default)
    {
        ErrorMessage = null;
        var exported = await _privacyService.ExportAllDataAsync(path, ct);
        StatusText = $"Exported data to {exported}";
        Summary = await _privacyService.GetSummaryAsync(ct);
    }

    public async Task ImportAllDataAsync(string path, CancellationToken ct = default)
    {
        ErrorMessage = null;
        await _privacyService.ImportAllDataAsync(path, ct);
        StatusText = "Imported data. The chat list has been refreshed.";
        Summary = await _privacyService.GetSummaryAsync(ct);
    }

    public async Task<string> ExportDiagnosticsAsync(CancellationToken ct = default)
    {
        ErrorMessage = null;
        var path = await _releaseService.ExportDiagnosticsAsync(ct);
        StatusText = $"Exported diagnostics to {path}";
        return path;
    }

    public async Task ClearAllDataAsync(CancellationToken ct = default)
    {
        ErrorMessage = null;
        await _privacyService.ClearAllDataAsync(ct);
        LocalStore.ClearAll();
        StatusText = "All local chats, debug records, API keys, and local preferences were cleared.";
        await LoadAsync(ct);
    }
}
