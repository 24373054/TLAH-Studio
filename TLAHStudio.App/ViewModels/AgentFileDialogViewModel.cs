using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// AGENT.md file upload/delete management.
/// Maps from AgentFileManager.tsx.
/// </summary>
public partial class AgentFileDialogViewModel : ObservableObject
{
    private readonly DbContext _db;
    private readonly IAppStateService _appState;

    [ObservableProperty] private string? _filename;
    [ObservableProperty] private string? _content;
    [ObservableProperty] private int _sizeBytes;
    [ObservableProperty] private bool _hasAgentFile;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    public bool HasCurrentChat => _appState.CurrentChatId != null;

    public AgentFileDialogViewModel(DbContext db, IAppStateService appState)
    {
        _db = db;
        _appState = appState;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        OnPropertyChanged(nameof(HasCurrentChat));
        ErrorMessage = null;

        if (_appState.CurrentChatId == null)
        {
            Filename = null;
            Content = null;
            SizeBytes = 0;
            HasAgentFile = false;
            return;
        }

        IsLoading = true;
        try
        {
            var af = await _db.Set<AgentFile>()
                .FirstOrDefaultAsync(a => a.ChatId == _appState.CurrentChatId.Value);
            if (af != null)
            {
                Filename = af.Filename;
                Content = af.Content;
                SizeBytes = af.SizeBytes;
                HasAgentFile = true;
            }
            else
            {
                Filename = null;
                Content = null;
                SizeBytes = 0;
                HasAgentFile = false;
            }
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task UploadAsync()
    {
        if (_appState.CurrentChatId == null) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".md");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var content = await File.ReadAllTextAsync(file.Path);

        // Upsert: replace existing agent file for this chat
        var existing = await _db.Set<AgentFile>()
            .FirstOrDefaultAsync(a => a.ChatId == _appState.CurrentChatId.Value);
        if (existing != null)
        {
            _db.Set<AgentFile>().Remove(existing);
            await _db.SaveChangesAsync();
        }

        var af = new AgentFile
        {
            ChatId = _appState.CurrentChatId.Value,
            Filename = file.Name,
            Content = content,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(content)
        };
        _db.Set<AgentFile>().Add(af);
        await _db.SaveChangesAsync();

        await LoadAsync();
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (_appState.CurrentChatId == null) return;

        var af = await _db.Set<AgentFile>()
            .FirstOrDefaultAsync(a => a.ChatId == _appState.CurrentChatId.Value);
        if (af != null)
        {
            _db.Set<AgentFile>().Remove(af);
            await _db.SaveChangesAsync();
        }

        Filename = null;
        Content = null;
        SizeBytes = 0;
        HasAgentFile = false;
    }
}
