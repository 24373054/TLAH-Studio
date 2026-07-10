using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Manages the chat list sidebar, chat creation, selection, and deletion.
/// Maps from Sidebar.tsx + ChatList.tsx + ChatListItem.tsx.
/// </summary>
public partial class SidebarViewModel : ObservableObject
{
    private readonly IChatService _chatService;
    private readonly IAppStateService _appState;
    private CancellationTokenSource? _loadChatsCts;
    private long _loadChatsVersion;
    private const int SearchDebounceMs = 220;

    public ObservableCollection<ChatSummaryDto> Chats { get; } = new();
    public ObservableCollection<ChatSummaryDto> PinnedChats { get; } = new();
    public ObservableCollection<ChatSummaryDto> RegularChats { get; } = new();

    // M4.7.0: Date-grouped regular chats for sidebar sections.
    public ObservableCollection<ChatSummaryDto> TodayChats { get; } = new();
    public ObservableCollection<ChatSummaryDto> YesterdayChats { get; } = new();
    public ObservableCollection<ChatSummaryDto> ThisWeekChats { get; } = new();
    public ObservableCollection<ChatSummaryDto> OlderChats { get; } = new();

    [ObservableProperty]
    private ChatSummaryDto? _selectedChat;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showArchived;

    [ObservableProperty]
    private ChatSummaryDto? _lastDeletedChat;

    [ObservableProperty]
    private bool _hasPinnedChats;

    [ObservableProperty]
    private bool _hasRegularChats;

    [ObservableProperty]
    private bool _hasVisibleChats;

    public SidebarViewModel(IChatService chatService, IAppStateService appState)
    {
        _chatService = chatService;
        _appState = appState;
    }

    [RelayCommand]
    public Task LoadChatsAsync() => LoadChatsCoreAsync(debounce: false);

    private async Task LoadChatsCoreAsync(bool debounce)
    {
        _loadChatsCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadChatsCts = cancellation;
        var version = Interlocked.Increment(ref _loadChatsVersion);
        IsLoading = true;
        try
        {
            if (debounce)
                await Task.Delay(SearchDebounceMs, cancellation.Token);

            var currentId = _appState.CurrentChatId;
            var searchText = SearchText;
            var showArchived = ShowArchived;
            var chats = await _chatService.ListChatsAsync(searchText, showArchived, cancellation.Token);
            if (!IsCurrentChatListLoad(version, cancellation))
                return;

            Chats.Clear();
            PinnedChats.Clear();
            RegularChats.Clear();
            TodayChats.Clear();
            YesterdayChats.Clear();
            ThisWeekChats.Clear();
            OlderChats.Clear();
            var now = DateTime.Now.Date;
            foreach (var chat in chats)
            {
                Chats.Add(chat);
                if (chat.IsPinned)
                    PinnedChats.Add(chat);
                else
                    RegularChats.Add(chat);
            }
            PopulateDateGroups();
            UpdateGroupState();

            if (currentId != null)
                SelectedChat = Chats.FirstOrDefault(c => c.Id == currentId.Value);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer keystroke, archive toggle, or mutation owns the sidebar.
        }
        finally
        {
            if (ReferenceEquals(_loadChatsCts, cancellation))
            {
                _loadChatsCts = null;
                IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentChatListLoad(long version, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested && version == Volatile.Read(ref _loadChatsVersion);

    [RelayCommand]
    public async Task CreateChatAsync()
    {
        var chat = await _chatService.CreateChatAsync();
        await LoadChatsAsync();
        await _appState.SelectChatAsync(chat.Id);
        SelectedChat = Chats.FirstOrDefault(c => c.Id == chat.Id);
    }

    [RelayCommand]
    public async Task DeleteChatAsync(Guid chatId)
    {
        var wasSelected = SelectedChat?.Id == chatId || _appState.CurrentChatId == chatId;
        await _chatService.DeleteChatAsync(chatId);
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        LastDeletedChat = chat;
        if (chat != null) Chats.Remove(chat);
        if (chat != null) PinnedChats.Remove(chat);
        if (chat != null) RegularChats.Remove(chat);
        if (chat != null) { TodayChats.Remove(chat); YesterdayChats.Remove(chat); ThisWeekChats.Remove(chat); OlderChats.Remove(chat); }
        UpdateGroupState();

        if (!wasSelected)
            return;

        var nextChat = Chats.FirstOrDefault();
        if (nextChat != null)
        {
            SelectedChat = nextChat;
        }
        else
        {
            SelectedChat = null;
            _appState.ClearSelection();
        }
    }

    [RelayCommand]
    public async Task RestoreLastDeletedAsync()
    {
        if (LastDeletedChat == null)
            return;

        await _chatService.RestoreChatAsync(LastDeletedChat.Id);
        LastDeletedChat = null;
        await LoadChatsAsync();
    }

    [RelayCommand]
    public async Task RenameChatAsync(ChatSummaryDto chat)
    {
        if (string.IsNullOrWhiteSpace(chat.Title))
            return;

        await _chatService.UpdateChatAsync(chat.Id, title: chat.Title.Trim());
        await LoadChatsAsync();
    }

    public async Task RenameChatAsync(Guid chatId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;

        await _chatService.UpdateChatAsync(chatId, title: title.Trim());
        await LoadChatsAsync();
    }

    public async Task TogglePinnedAsync(Guid chatId)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null) return;
        await _chatService.SetPinnedAsync(chatId, !chat.IsPinned);
        await LoadChatsAsync();
    }

    public async Task ToggleArchivedAsync(Guid chatId)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null) return;
        await _chatService.SetArchivedAsync(chatId, !chat.IsArchived);
        await LoadChatsAsync();
    }

    public Task<string> ExportChatJsonAsync(Guid chatId) =>
        _chatService.ExportChatJsonAsync(chatId);

    // M4.7.0: Populate date-grouped collections from RegularChats.
    private void PopulateDateGroups()
    {
        TodayChats.Clear();
        YesterdayChats.Clear();
        ThisWeekChats.Clear();
        OlderChats.Clear();
        var now = DateTime.Now.Date;
        var yesterday = now.AddDays(-1);
        var weekStart = now.AddDays(-(int)now.DayOfWeek);
        foreach (var chat in RegularChats)
        {
            var date = chat.UpdatedAt.Date;
            if (date == now) TodayChats.Add(chat);
            else if (date == yesterday) YesterdayChats.Add(chat);
            else if (date >= weekStart) ThisWeekChats.Add(chat);
            else OlderChats.Add(chat);
        }
    }

    private void UpdateGroupState()
    {
        HasPinnedChats = PinnedChats.Count > 0;
        HasRegularChats = RegularChats.Count > 0;
        HasVisibleChats = Chats.Count > 0;
    }

    partial void OnSelectedChatChanged(ChatSummaryDto? value)
    {
        if (value != null)
            _ = _appState.SelectChatAsync(value.Id);
    }

    partial void OnSearchTextChanged(string value) =>
        _ = LoadChatsCoreAsync(debounce: true);

    partial void OnShowArchivedChanged(bool value) =>
        _ = LoadChatsAsync();
}
