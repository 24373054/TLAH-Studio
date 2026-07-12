using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Workspace;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Manages the current chat's messages, sending state, and input.
/// Maps from ChatContext.tsx (the core state: currentChat, messages, sending, sendMessage).
/// </summary>
public partial class ChatPageViewModel : ObservableObject
{
    private readonly IChatService _chatService;
    private readonly ILlmService _llmService;
    private readonly ISettingsService _settingsService;
    private readonly IAppStateService _appState;
    private readonly IWorkspaceRootService _workspaceRootService;
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _chatLoadCts;
    private long _chatLoadVersion;
    private IProgress<AgentProgressUpdate>? _activeAgentProgress;
    private AssistantStreamState? _activeStream;
    private string? _activeAgentRequest;
    // M4.9.6: Prevent infinite recursion when slash commands rewrite InputText
    // and re-invoke SendMessageAsync. Guards the recursive dispatch path.
    private bool _isHandlingSlashCommand;
    private const int StreamDrainDelayMs = 32;
    private const int InitialMessagePageSize = 80;
    private const string AgentActivityPanelStorageKey = "tlah-agent-activity-panel-open";
    private const string AgentPermissionModeStorageKey = "tlah-agent-permission-mode";
    private long _thinkingDepthUpdateVersion;
    private readonly SemaphoreSlim _thinkingDepthWriteGate = new(1, 1);

    public ObservableCollection<Message> Messages { get; } = new();
    public ObservableCollection<AgentProgressLine> AgentProgressLines { get; } = new();
    public ObservableCollection<AgentActivityRun> AgentActivityRuns { get; } = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasOlderMessages;

    [ObservableProperty]
    private Chat? _currentChat;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _selectedRole = "user";

    [ObservableProperty]
    private bool _isAgentModeEnabled = true;

    [ObservableProperty]
    private string _selectedAgentPermissionMode =
        AgentPermissionModes.Normalize(LocalStore.Get(AgentPermissionModeStorageKey));

    [ObservableProperty]
    private string _selectedThinkingDepth = ReasoningDepths.Auto;

    [ObservableProperty]
    private string _contextUsageText = "Context --";

    [ObservableProperty]
    private string _contextUsageToolTip = "Context usage is calculated after a chat is selected.";

    /// <summary>M4.7.0: Last fetched context usage snapshot for the gauge bar.</summary>
    public ContextUsageSnapshot? LastContextUsage { get; private set; }

    /// <summary>M4.7.0: Public token formatter used by the header gauge tooltips.</summary>
    public string FormatTokens(int value) => FormatTokenCount(value);

    [ObservableProperty]
    private bool _isAgentStatusVisible;

    [ObservableProperty]
    private string _agentStatusText = string.Empty;

    [ObservableProperty]
    private bool _isAgentLiveVisible;

    [ObservableProperty]
    private string _agentLiveSummary = string.Empty;

    [ObservableProperty]
    private bool _isAgentActivityPanelOpen =
        !string.Equals(LocalStore.Get(AgentActivityPanelStorageKey), "false", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _hasAgentActivity;

    [ObservableProperty]
    private string _agentActivitySummary = "No agent activity yet.";

    [ObservableProperty]
    private Guid? _currentAgentRunId;

    [ObservableProperty]
    private string? _currentAgentRunStatus;

    [ObservableProperty]
    private string _workspaceDisplayName = "Sandbox";

    [ObservableProperty]
    private string _workspacePath = string.Empty;

    [ObservableProperty]
    private bool _isWorkspaceConfigured;

    [ObservableProperty]
    private string _workspaceToolTip = "Using this chat's private sandbox.";

    public List<string> AvailableRoles { get; } = new() { "user", "system" };

    public ChatPageViewModel(
        IChatService chatService,
        ILlmService llmService,
        ISettingsService settingsService,
        IAppStateService appState,
        IWorkspaceRootService workspaceRootService,
        IServiceProvider services)
    {
        _chatService = chatService;
        _llmService = llmService;
        _settingsService = settingsService;
        _appState = appState;
        _workspaceRootService = workspaceRootService;
        _services = services;

        // React to chat selection changes from AppStateService
        _appState.ChatSelected += OnChatSelected;
        _appState.ChatDeselected += OnChatDeselected;
    }

    private readonly IServiceProvider _services;

    /// <summary>
    /// M4.9.5 Phase E2: aggregate slash commands for the input-box completion UI.
    /// Resolves ISlashCommandProvider lazily (scoped) to avoid captive-dependency
    /// on the singleton VM.
    /// </summary>
    public async Task<IReadOnlyList<SlashCommand>> GetSlashCommandsAsync(System.Threading.CancellationToken ct = default)
    {
        var chatId = _appState.CurrentChatId ?? Guid.Empty;
        try
        {
            var provider = _services.GetRequiredService<ISlashCommandProvider>();
            return await provider.GetCommandsAsync(chatId, ct);
        }
        catch
        {
            return Array.Empty<SlashCommand>();
        }
    }

    partial void OnIsAgentActivityPanelOpenChanged(bool value)
    {
        LocalStore.Set(AgentActivityPanelStorageKey, value ? "true" : "false");
    }

    partial void OnSelectedAgentPermissionModeChanged(string value)
    {
        SelectedAgentPermissionMode = AgentPermissionModes.Normalize(value);
        LocalStore.Set(AgentPermissionModeStorageKey, SelectedAgentPermissionMode);
    }

    /// <summary>
    /// Refreshes the effective reasoning depth shown by the composer. A chat
    /// uses its merged chat/profile/global settings; the empty workspace uses
    /// the global default.
    /// </summary>
    public async Task RefreshThinkingDepthAsync(CancellationToken ct = default)
    {
        var chatId = _appState.CurrentChatId;
        var version = Volatile.Read(ref _thinkingDepthUpdateVersion);
        await _thinkingDepthWriteGate.WaitAsync(ct);
        try
        {
            if (chatId != _appState.CurrentChatId ||
                version != Volatile.Read(ref _thinkingDepthUpdateVersion))
                return;

            var depth = chatId.HasValue
                ? (await _settingsService.GetEffectiveSettingsAsync(chatId.Value, ct)).ThinkingDepth
                : (await _settingsService.GetGlobalSettingsRawAsync(ct)).ThinkingDepth;

            if (chatId == _appState.CurrentChatId &&
                version == Volatile.Read(ref _thinkingDepthUpdateVersion))
                SelectedThinkingDepth = ReasoningDepths.Normalize(depth);
        }
        finally
        {
            _thinkingDepthWriteGate.Release();
        }
    }

    /// <summary>
    /// Applies a real reasoning setting. The current chat receives an
    /// explicit override; with no selected chat the global default is updated.
    /// </summary>
    public async Task SetThinkingDepthAsync(string depth, CancellationToken ct = default)
    {
        var normalized = ReasoningDepths.Normalize(depth);
        var previous = SelectedThinkingDepth;
        var chatId = _appState.CurrentChatId;
        var version = Interlocked.Increment(ref _thinkingDepthUpdateVersion);
        SelectedThinkingDepth = normalized;
        var enteredGate = false;

        try
        {
            await _thinkingDepthWriteGate.WaitAsync(ct);
            enteredGate = true;

            // Slider changes can arrive faster than a settings write. Drop any
            // queued stale value so the latest visible detent always wins.
            if (version != Volatile.Read(ref _thinkingDepthUpdateVersion) ||
                chatId != _appState.CurrentChatId)
                return;

            if (chatId.HasValue)
            {
                await _settingsService.UpdateChatSettingsAsync(
                    chatId.Value,
                    new ChatSettingsUpdateDto(ThinkingDepth: normalized),
                    ct);
            }
            else
            {
                await _settingsService.UpdateGlobalSettingsAsync(
                    new GlobalSettingsUpdateDto(ThinkingDepth: normalized),
                    ct);
            }
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _thinkingDepthUpdateVersion) &&
                chatId == _appState.CurrentChatId)
            {
                SelectedThinkingDepth = ReasoningDepths.Normalize(previous);
                ErrorMessage = ex.Message;
            }
            throw;
        }
        finally
        {
            if (enteredGate)
                _thinkingDepthWriteGate.Release();
        }
    }

    private async void OnChatSelected(object? sender, Guid chatId)
    {
        await LoadChatAsync(chatId);
    }

    private async void OnChatDeselected(object? sender, EventArgs e)
    {
        CancelPendingChatLoad();
        Interlocked.Increment(ref _chatLoadVersion);
        Interlocked.Increment(ref _thinkingDepthUpdateVersion);
        CurrentChat = null;
        Messages.Clear();
        HasOlderMessages = false;
        ClearAgentProgress();
        ClearAgentActivity();
        ContextUsageText = "Context --";
        ContextUsageToolTip = "Context usage is calculated after a chat is selected.";
        WorkspaceDisplayName = "Sandbox";
        WorkspacePath = string.Empty;
        IsWorkspaceConfigured = false;
        WorkspaceToolTip = "Using this chat's private sandbox.";
        SelectedThinkingDepth = ReasoningDepths.Auto;
        UpdateAgentStatus(null);
        try
        {
            await RefreshThinkingDepthAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task LoadChatAsync(Guid chatId)
    {
        CancelPendingChatLoad();
        var cancellation = new CancellationTokenSource();
        _chatLoadCts = cancellation;
        var version = Interlocked.Increment(ref _chatLoadVersion);
        IsLoading = true;
        ErrorMessage = null;
        // Do not leave a previous chat visible while the new selection is in
        // flight: an error or late response must never look like it belongs to
        // the conversation the user just selected.
        CurrentChat = null;
        Messages.Clear();
        HasOlderMessages = false;
        ClearAgentProgress();
        ClearAgentActivity();
        try
        {
            var chatTask = _chatService.GetChatAsync(chatId, cancellation.Token);
            var messagesTask = _chatService.GetChatMessagePageAsync(
                chatId,
                pageSize: InitialMessagePageSize,
                ct: cancellation.Token);
            await Task.WhenAll(chatTask, messagesTask);
            if (!IsCurrentChatLoad(chatId, version, cancellation))
                return;

            var chat = await chatTask;
            var page = await messagesTask;
            CurrentChat = chat;
            await RefreshThinkingDepthAsync(cancellation.Token);
            if (!IsCurrentChatLoad(chatId, version, cancellation))
                return;
            await RefreshWorkspaceAsync(chatId, version);
            if (!IsCurrentChatLoad(chatId, version, cancellation))
                return;

            foreach (var msg in page.Messages)
                Messages.Add(msg);
            HasOlderMessages = page.HasMore;

            await LoadAgentActivityAsync(chatId, version);
            await UpdateContextUsageAsync(chatId, version);
            var latestRun = await _llmService.GetLatestAgentRunAsync(chatId);
            if (IsCurrentChatLoad(chatId, version, cancellation))
                UpdateAgentStatus(latestRun);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Selection changed; a newer load owns the visible state.
        }
        catch (Exception e)
        {
            if (IsCurrentChatLoad(chatId, version, cancellation))
                ErrorMessage = e.Message;
        }
        finally
        {
            if (ReferenceEquals(_chatLoadCts, cancellation))
            {
                _chatLoadCts = null;
                IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Loads one older chronological page without replacing the active transcript.</summary>
    public async Task LoadOlderMessagesAsync()
    {
        if (CurrentChat == null || !HasOlderMessages || Messages.Count == 0)
            return;

        var chatId = CurrentChat.Id;
        var version = Volatile.Read(ref _chatLoadVersion);
        var beforeSequence = Messages[0].SequenceNum;
        try
        {
            var page = await _chatService.GetChatMessagePageAsync(chatId, beforeSequence, InitialMessagePageSize);
            if (_appState.CurrentChatId != chatId || version != Volatile.Read(ref _chatLoadVersion))
                return;

            for (var i = page.Messages.Count - 1; i >= 0; i--)
                Messages.Insert(0, page.Messages[i]);
            HasOlderMessages = page.HasMore;
        }
        catch (OperationCanceledException)
        {
            // A newer chat was selected while this page was in flight.
        }
    }

    private bool IsCurrentChatLoad(Guid chatId, long version, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested &&
        version == Volatile.Read(ref _chatLoadVersion) &&
        _appState.CurrentChatId == chatId;

    private void CancelPendingChatLoad()
    {
        _chatLoadCts?.Cancel();
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending)
            return;

        if (_appState.CurrentChatId == null)
            return;

        // M4.9.5 Phase E2: intercept slash commands before sending to the LLM.
        // Built-in commands execute locally; skill/tool/mcp commands dispatch
        // as agent invocations. Returns true if consumed (no LLM send).
        // M4.9.6: skip slash parsing on re-entry (a slash command already
        // rewrote InputText into a plain instruction and recursed).
        if (!_isHandlingSlashCommand && TryHandleSlashCommand(InputText.TrimStart()))
        {
            InputText = string.Empty;
            return;
        }

        var content = InputText;
        var role = SelectedRole == "system" ? "system" : null;
        var agentMode = IsAgentModeEnabled;
        if (agentMode)
            _activeAgentRequest = content;

        InputText = string.Empty;
        var optimisticMessage = AddOptimisticUserMessage(content, role ?? "user");
        var stream = BeginAssistantStream();
        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();

        try
        {
            var agentProgress = agentMode ? BeginAgentProgress(reset: true) : null;
            var result = agentMode
                ? await _llmService.RunAgentTaskAsync(
                    _appState.CurrentChatId.Value,
                    content,
                    role,
                    CreateAgentOptions(stream.Progress, agentProgress),
                    _sendCts.Token)
                : await _llmService.SendMessageAsync(
                    _appState.CurrentChatId.Value,
                    content,
                    role,
                    _sendCts.Token,
                    stream.Progress);

            if (agentMode)
                result = await CompleteApprovalFlowAsync(result, _sendCts.Token);

            var drainToken = result.AgentRun?.Status == AgentRunStatuses.Cancelled
                ? CancellationToken.None
                : _sendCts.Token;
            await stream.WaitForFinalDrainAsync(drainToken);
            ApplySendResultToLiveMessages(result, optimisticMessage, stream);
            if (agentMode)
                await ReloadCurrentChatAsync();

            // Notify debug panel of the new turn
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            if (agentMode)
            {
                RemoveStreamMessage(stream.Message);
                ErrorMessage = "Agent run stopped.";
                await ReloadCurrentChatAsync();
            }
            else
            {
                RemoveOptimisticMessage(optimisticMessage);
                RemoveStreamMessage(stream.Message);
                ErrorMessage = "Request stopped.";
            }
        }
        catch (Exception e)
        {
            RemoveOptimisticMessage(optimisticMessage);
            RemoveStreamMessage(stream.Message);
            ErrorMessage = e.Message;
        }
        finally
        {
            IsAgentLiveVisible = false;
            _activeAgentRequest = null;
            _activeStream = null;
            IsSending = false;
            // M4.7.0: Refresh context usage after each send so the gauge stays current.
            if (_appState.CurrentChatId is { } cid)
                _ = UpdateContextUsageAsync(cid);
        }
    }

    [RelayCommand]
    public void StopSending()
    {
        if (!IsSending)
            return;

        _sendCts?.Cancel();
        if (CurrentAgentRunId is { } runId)
            _ = _llmService.CancelAgentRunAsync(runId);
    }

    /// <summary>
    /// M4.9.5 Phase E2: raised when a slash command requests UI navigation that
    /// the VM can't perform itself (e.g. /new, /settings). MainWindow subscribes.
    /// </summary>
    public event EventHandler<string>? SlashCommandNavigationRequested;

    /// <summary>
    /// M4.9.5 Phase E2: dispatch a slash command. Returns true if the input was
    /// consumed (no LLM send). Built-in commands run locally; skill/tool/mcp
    /// commands are rewritten to a natural-language agent instruction and sent
    /// through the normal agent path (so the model invokes the skill/tool).
    /// </summary>
    private bool TryHandleSlashCommand(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed[0] != '/')
            return false;

        // Split "/name args..." → name + rest.
        var space = trimmed.IndexOf(' ');
        var rawName = (space < 0 ? trimmed[1..] : trimmed[1..space]).TrimEnd(':');
        var rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        if (rawName.Length == 0)
            return false;
        var name = rawName.ToLowerInvariant();

        switch (name)
        {
            case "clear":
                // M4.9.6: stop any in-flight stream first — otherwise the drain
                // loop keeps appending to Messages after we clear it, throwing
                // on the now-empty collection.
                StopSending();
                _activeStream = null;
                _activeAgentRequest = null;
                Messages.Clear();
                ErrorMessage = null;
                return true;
            case "new":
                // M4.9.6: stop streaming before navigating away, same reason
                // as /clear — the drain loop references Messages by index.
                StopSending();
                _activeStream = null;
                _activeAgentRequest = null;
                SlashCommandNavigationRequested?.Invoke(this, "new");
                return true;
            case "settings":
                SlashCommandNavigationRequested?.Invoke(this, "settings");
                return true;
            case "stop":
                StopSending();
                return true;
            case "regenerate":
                var lastAssistant = Messages.LastOrDefault(m =>
                    string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
                if (lastAssistant != null)
                    _ = RegenerateMessageAsync(lastAssistant);
                return true;
            case "agent":
                if (string.IsNullOrWhiteSpace(rest))
                    IsAgentModeEnabled = !IsAgentModeEnabled;
                else
                    IsAgentModeEnabled = rest.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                                         rest.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "help":
                AddSystemNotice("Slash commands: /clear /new /agent [on|off] /stop /regenerate /settings /help — plus any skill name (e.g. /code-review) or tool name (e.g. /file_read).");
                return true;
        }

        // Non-built-in: resolve against the slash command provider. If it's a
        // known skill/tool/mcp, rewrite to an agent instruction and send.
        var cmd = ResolveSlashCommandAsync(name).GetAwaiter().GetResult();
        if (cmd == null)
            return false;

        // Force agent mode on (skills/tools require the agent runtime).
        if (!IsAgentModeEnabled)
            IsAgentModeEnabled = true;

        var instruction = cmd.Kind switch
        {
            SlashCommandKind.Skill => $"Use the `{cmd.Name}` skill{(string.IsNullOrWhiteSpace(rest) ? "" : $" with: {rest}")}.",
            SlashCommandKind.Tool => $"Call the `{cmd.Name}` tool{(string.IsNullOrWhiteSpace(rest) ? "" : $" with: {rest}")}.",
            SlashCommandKind.Mcp => $"Call the MCP tool `{cmd.Name}`{(string.IsNullOrWhiteSpace(rest) ? "" : $" with: {rest}")}.",
            _ => null
        };
        if (instruction == null)
            return false;

        // Send the rewritten instruction through the normal path by setting
        // InputText and recursing once (without the slash prefix). Guard with
        // _isHandlingSlashCommand so the re-entry doesn't try to re-parse the
        // rewritten instruction as a slash command (infinite recursion).
        InputText = instruction;
        _isHandlingSlashCommand = true;
        try
        {
            _ = SendMessageAsync();
        }
        finally
        {
            _isHandlingSlashCommand = false;
        }
        return true;
    }

    private async Task<SlashCommand?> ResolveSlashCommandAsync(string name)
    {
        try
        {
            var chatId = _appState.CurrentChatId ?? Guid.Empty;
            var provider = _services.GetRequiredService<ISlashCommandProvider>();
            var cmds = await provider.GetCommandsAsync(chatId);
            return cmds.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private void AddSystemNotice(string text)
    {
        Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            Role = "system",
            Content = text,
            CreatedAt = DateTime.UtcNow
        });
    }

    [RelayCommand]
    public async Task ResumeAgentRunAsync()
    {
        if (CurrentAgentRunId == null || IsSending)
            return;

        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            var agentProgress = BeginAgentProgress(reset: true);
            var stream = BeginAssistantStream();
            AgentStatusText = "Resuming saved agent run...";
            var result = await _llmService.ResumeAgentTaskAsync(
                CurrentAgentRunId.Value,
                CreateAgentOptions(stream.Progress, agentProgress),
                _sendCts.Token);
            result = await CompleteApprovalFlowAsync(result, _sendCts.Token);
            var drainToken = result.AgentRun?.Status == AgentRunStatuses.Cancelled
                ? CancellationToken.None
                : _sendCts.Token;
            await stream.WaitForFinalDrainAsync(drainToken);
            ApplySendResultToLiveMessages(result, userMessage: null, stream);
            await ReloadCurrentChatAsync();
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Agent run stopped.";
            await ReloadCurrentChatAsync();
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsAgentLiveVisible = false;
            _activeAgentRequest = null;
            _activeStream = null;
            IsSending = false;
        }
    }

    public async Task RegenerateMessageAsync(Message message)
    {
        if (IsSending)
            return;

        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await _llmService.RegenerateAssistantAsync(message.Id, _sendCts.Token);
            await ReloadCurrentChatAsync();
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request stopped.";
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    public async Task EditAndResendMessageAsync(Message message, string content)
    {
        if (IsSending || string.IsNullOrWhiteSpace(content))
            return;

        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await _llmService.EditAndResendAsync(message.Id, content.Trim(), _sendCts.Token);
            await ReloadCurrentChatAsync();
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request stopped.";
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    public async Task ContinueFromMessageAsync(Message message)
    {
        if (IsSending)
            return;

        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await _llmService.ContinueFromMessageAsync(message.Id, _sendCts.Token);
            await ReloadCurrentChatAsync();
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Request stopped.";
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    public async Task UpdateSystemPromptAsync(string prompt)
    {
        if (_appState.CurrentChatId == null) return;
        try
        {
            await _chatService.UpdateChatAsync(_appState.CurrentChatId.Value, systemPrompt: prompt);
            if (CurrentChat != null)
                CurrentChat.SystemPrompt = prompt;
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    public async Task UpdateTitleAsync(string title)
    {
        if (_appState.CurrentChatId == null || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            var chat = await _chatService.UpdateChatAsync(_appState.CurrentChatId.Value, title: title.Trim());
            if (CurrentChat != null)
            {
                CurrentChat.Title = chat.Title;
                OnPropertyChanged(nameof(CurrentChat));
            }
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    private async Task ReloadCurrentChatAsync()
    {
        if (_appState.CurrentChatId != null)
            await LoadChatAsync(_appState.CurrentChatId.Value);
    }

    public async Task SetWorkspaceRootAsync(string rootPath)
    {
        if (_appState.CurrentChatId == null)
            return;
        if (string.IsNullOrWhiteSpace(rootPath))
            return;

        await _workspaceRootService.SetRootAsync(_appState.CurrentChatId.Value, rootPath);
        await RefreshWorkspaceAsync(_appState.CurrentChatId.Value);
    }

    public async Task<string?> CreateWorkspaceRootAsync()
    {
        if (_appState.CurrentChatId == null)
            return null;

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TLAH Studio Workspaces");
        Directory.CreateDirectory(baseDir);

        var title = CurrentChat?.Title ?? "Workspace";
        var safeTitle = SafeWorkspaceFolderName(title);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = "Workspace";

        var path = Path.Combine(baseDir, safeTitle);
        if (Directory.Exists(path))
            path = Path.Combine(baseDir, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}");

        Directory.CreateDirectory(path);
        await SetWorkspaceRootAsync(path);
        return path;
    }

    public async Task ClearWorkspaceRootAsync()
    {
        if (_appState.CurrentChatId == null)
            return;

        await _workspaceRootService.ClearRootAsync(_appState.CurrentChatId.Value);
        await RefreshWorkspaceAsync(_appState.CurrentChatId.Value);
    }

    private async Task RefreshWorkspaceAsync(Guid chatId, long? loadVersion = null)
    {
        var root = await _workspaceRootService.GetRootAsync(chatId);
        if (loadVersion.HasValue && !IsCurrentChatVersion(chatId, loadVersion.Value))
            return;
        WorkspacePath = root.RootPath;
        IsWorkspaceConfigured = root.IsConfigured;
        WorkspaceDisplayName = root.IsConfigured
            ? ShortWorkspaceName(root.RootPath)
            : "Sandbox";
        WorkspaceToolTip = root.IsConfigured
            ? $"Workspace: {root.RootPath}"
            : $"Sandbox: {root.RootPath}";
    }

    private static string ShortWorkspaceName(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private static string SafeWorkspaceFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
            builder.Append(invalid.Contains(ch) ? '-' : ch);
        var result = builder.ToString().Trim(' ', '.', '-');
        return result.Length <= 48 ? result : result[..48].Trim(' ', '.', '-');
    }

    private void ApplySendResultToLiveMessages(
        SendMessageResult result,
        Message? userMessage,
        AssistantStreamState? stream)
    {
        if (userMessage != null)
            CopyPersistedMessage(result.UserMessage, userMessage);

        if (stream?.Message != null)
        {
            CopyPersistedMessage(result.AssistantMessage, stream.Message);
            lock (stream.Gate)
            {
                stream.TextBuilder.Clear();
                stream.ThinkingBuilder.Clear();
                stream.PendingTextChars.Clear();
                stream.PendingThinkingChars.Clear();
                stream.FinalSnapshot = result.AssistantMessage.Content;
                stream.FinalReceived = true;
                stream.TryCompleteFinalDrainLocked();
            }
        }

        UpdateAgentStatus(result.AgentRun);
        StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void CopyPersistedMessage(Message source, Message target)
    {
        // M4.4.6: Evict old cache entry before overwriting the Id. Without this,
        // the per-message element cache accumulates orphaned entries every time
        // a streaming draft is finalized and gets its persisted Id assigned.
        if (target.Id != source.Id)
            MessageIdMutated?.Invoke(target.Id);

        target.Id = source.Id;
        target.ChatId = source.ChatId;
        target.Role = source.Role;
        target.Content = source.Content;
        target.TurnId = source.TurnId;
        target.SequenceNum = source.SequenceNum;
        target.CreatedAt = source.CreatedAt;
    }

    private async Task<SendMessageResult> CompleteApprovalFlowAsync(
        SendMessageResult result,
        CancellationToken ct)
    {
        UpdateAgentStatus(result.AgentRun);
        while (result.AgentRun is
               {
                   Status: AgentRunStatuses.AwaitingApproval,
                   PendingApproval: not null
               } run)
        {
            var request = new AgentApprovalRequest(
                run.Id,
                run.PendingApproval.Id,
                run.PendingApproval.ToolName,
                run.PendingApproval.ArgumentsJson,
                run.PendingApproval.SafetyLevel,
                run.PendingApproval.SafetySummary,
                run.PendingApproval.SafetyJson);
            AgentApprovalRequested?.Invoke(this, request);
            AgentApprovalChoice choice;
            using (ct.Register(() => request.Completion.TrySetCanceled(ct)))
                choice = await request.Completion.Task;

            await _llmService.SetAgentToolApprovalAsync(
                request.InvocationId,
                choice is AgentApprovalChoice.AllowOnce or AgentApprovalChoice.AllowForProject or AgentApprovalChoice.AllowGlobally,
                choice switch
                {
                    AgentApprovalChoice.AllowForProject => ToolPolicyScopes.Project,
                    AgentApprovalChoice.AllowGlobally => ToolPolicyScopes.Global,
                    AgentApprovalChoice.AlwaysDeny => ToolPolicyScopes.Global,
                    _ => ToolPolicyScopes.Once
                },
                ct,
                request.UpdatedArgumentsJson);  // M4.9.0
            AgentStatusText = choice is AgentApprovalChoice.DenyOnce or AgentApprovalChoice.AlwaysDeny
                ? "Tool denied. Asking the agent for a safer next step..."
                : "Tool approved. Continuing the agent run...";
            result = await _llmService.ResumeAgentTaskAsync(
                request.AgentRunId,
                CreateAgentOptions(_activeStream?.Progress, _activeAgentProgress),
                ct);
            UpdateAgentStatus(result.AgentRun);
        }
        return result;
    }

    private Message? AddOptimisticUserMessage(string content, string role)
    {
        if (_appState.CurrentChatId == null)
            return null;

        var sequence = Messages.Count == 0
            ? 0
            : Messages.Max(m => m.SequenceNum) + 1;
        var message = new Message
        {
            ChatId = _appState.CurrentChatId.Value,
            Role = role,
            Content = content,
            SequenceNum = sequence,
            CreatedAt = DateTime.UtcNow
        };
        Messages.Add(message);
        return message;
    }

    private void RemoveOptimisticMessage(Message? message)
    {
        if (message != null && message.TurnId == null)
            Messages.Remove(message);
    }

    private AssistantStreamState BeginAssistantStream()
    {
        var message = new Message
        {
            ChatId = _appState.CurrentChatId ?? Guid.Empty,
            Role = "assistant",
            Content = string.Empty,
            SequenceNum = Messages.Count == 0 ? 0 : Messages.Max(m => m.SequenceNum) + 1,
            CreatedAt = DateTime.UtcNow
        };
        Messages.Add(message);

        _activeStream = new AssistantStreamState(
            message,
            new InlineLlmStreamProgress(OnAssistantStream));
        return _activeStream;
    }

    private void RemoveStreamMessage(Message? message)
    {
        if (message != null && message.TurnId == null)
            Messages.Remove(message);
    }

    private void OnAssistantStream(LlmStreamUpdate update)
    {
        var stream = _activeStream;
        if (stream == null)
            return;

        var shouldStartDrain = false;
        lock (stream.Gate)
        {
            if (update.EventType == LlmStreamEventTypes.TextStarted)
                stream.IsThinkingCollapsed = true;

            if (!string.IsNullOrEmpty(update.Delta))
            {
                var target = update.EventType == LlmStreamEventTypes.ThinkingDelta
                    ? stream.PendingThinkingChars
                    : stream.PendingTextChars;
                foreach (var ch in update.Delta)
                    target.Enqueue(ch);

                if (update.EventType == LlmStreamEventTypes.TextDelta &&
                    (stream.ThinkingBuilder.Length > 0 ||
                     stream.PendingThinkingChars.Count > 0))
                    stream.IsThinkingCollapsed = true;
            }

            if (update.IsFinal)
            {
                stream.FinalReceived = true;
                stream.FinalSnapshot = update.Snapshot;
                QueueMissingFinalText(stream, update.Snapshot);
            }

            if (!stream.IsDraining)
            {
                stream.IsDraining = true;
                shouldStartDrain = true;
            }
        }

        if (shouldStartDrain)
            _ = DrainAssistantStreamAsync(stream);
    }

    private async Task DrainAssistantStreamAsync(AssistantStreamState stream)
    {
        try
        {
            while (ReferenceEquals(_activeStream, stream))
            {
                bool changed;
                lock (stream.Gate)
                {
                    var pending = stream.PendingThinkingChars.Count + stream.PendingTextChars.Count;
                    if (pending <= 0)
                    {
                        if (!string.IsNullOrEmpty(stream.FinalSnapshot))
                            QueueMissingFinalText(stream, stream.FinalSnapshot);

                        pending = stream.PendingThinkingChars.Count + stream.PendingTextChars.Count;
                    }

                    if (pending <= 0)
                        break;

                    var batchSize = pending switch
                    {
                        > 800 => 48,
                        > 300 => 24,
                        > 120 => 12,
                        > 30 => 6,
                        > 8 => 3,
                        _ => 1
                    };
                    for (var i = 0; i < batchSize; i++)
                    {
                        if (stream.PendingThinkingChars.Count > 0)
                        {
                            stream.ThinkingBuilder.Append(stream.PendingThinkingChars.Dequeue());
                            continue;
                        }

                        if (stream.PendingTextChars.Count > 0)
                            stream.TextBuilder.Append(stream.PendingTextChars.Dequeue());
                    }

                    stream.Message.Content = stream.ComposeContent();
                    changed = true;

                }

                if (!changed)
                    break;
                StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
                await Task.Delay(StreamDrainDelayMs);
            }
        }
        finally
        {
            var shouldRestart = false;
            lock (stream.Gate)
            {
                stream.IsDraining = false;
                if (ReferenceEquals(_activeStream, stream) &&
                    (stream.PendingThinkingChars.Count > 0 || stream.PendingTextChars.Count > 0))
                {
                    stream.IsDraining = true;
                    shouldRestart = true;
                }
                else
                {
                    stream.TryCompleteFinalDrainLocked();
                }
            }

            if (shouldRestart)
                _ = DrainAssistantStreamAsync(stream);
        }
    }

    private static void QueueMissingFinalText(AssistantStreamState stream, string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
            return;

        var knownLength = stream.TextBuilder.Length + stream.PendingTextChars.Count;
        if (snapshot.Length <= knownLength)
            return;

        var currentText = stream.TextBuilder.ToString();
        if (!snapshot.StartsWith(currentText, StringComparison.Ordinal))
            return;

        foreach (var ch in snapshot[knownLength..])
            stream.PendingTextChars.Enqueue(ch);
    }

    private IProgress<AgentProgressUpdate> BeginAgentProgress(bool reset)
    {
        if (reset)
            AgentProgressLines.Clear();

        AgentLiveSummary = "Preparing the agent run...";
        IsAgentLiveVisible = true;
        _activeAgentProgress = new Progress<AgentProgressUpdate>(OnAgentProgress);
        return _activeAgentProgress;
    }

    private void ClearAgentProgress()
    {
        AgentProgressLines.Clear();
        AgentLiveSummary = string.Empty;
        IsAgentLiveVisible = false;
        _activeAgentProgress = null;
    }

    private void OnAgentProgress(AgentProgressUpdate update)
    {
        UpdateAgentStatus(update.Run);
        var line = FormatAgentProgress(update);
        if (line == null)
            return;

        if (AgentProgressLines.LastOrDefault() is { } last &&
            last.SequenceNumber == line.SequenceNumber)
        {
            AgentProgressLines[^1] = line;
            UpsertAgentActivity(update, line);
            AgentLiveSummary = line.Text;
            IsAgentLiveVisible = true;
            return;
        }

        AgentProgressLines.Add(line);
        while (AgentProgressLines.Count > 200)
            AgentProgressLines.RemoveAt(0);

        UpsertAgentActivity(update, line);
        AgentLiveSummary = line.Text;
        IsAgentLiveVisible = true;
    }

    private async Task LoadAgentActivityAsync(Guid chatId, long? loadVersion = null)
    {
        var snapshots = await _llmService.GetAgentActivityAsync(chatId);
        if (loadVersion.HasValue && !IsCurrentChatVersion(chatId, loadVersion.Value))
            return;
        AgentActivityRuns.Clear();
        foreach (var snapshot in snapshots)
        {
            var run = AgentActivityRun.FromSnapshot(snapshot);
            foreach (var evt in snapshot.Events)
            {
                var line = FormatAgentProgress(snapshot, evt);
                if (line != null)
                    run.Lines.Add(line);
            }
            AgentActivityRuns.Add(run);
        }
        RefreshAgentActivityState();
    }

    private AgentRunOptions CreateAgentOptions(
        IProgress<LlmStreamUpdate>? outputStream,
        IProgress<AgentProgressUpdate>? progress) =>
        new(
            OutputStream: outputStream,
            Progress: progress,
            PermissionMode: AgentPermissionModes.Normalize(SelectedAgentPermissionMode));

    private async Task UpdateContextUsageAsync(Guid chatId, long? loadVersion = null)
    {
        try
        {
            var usage = await _llmService.GetContextUsageAsync(chatId);
            if (loadVersion.HasValue && !IsCurrentChatVersion(chatId, loadVersion.Value))
                return;
            LastContextUsage = usage;
            ContextUsageText =
                $"Context {FormatTokenCount(usage.TotalTokens)} / {FormatTokenCount(usage.AvailableTokens)} ({usage.PercentUsed:F1}%)";
            ContextUsageToolTip =
                $"Provider: {usage.Provider} / {usage.Model}\n" +
                $"Conversation: {FormatTokenCount(usage.ConversationTokens)}\n" +
                $"Tools: {FormatTokenCount(usage.ToolsTokens)}\n" +
                $"MCP: {FormatTokenCount(usage.McpTokens)}\n" +
                $"Execution results: {FormatTokenCount(usage.ExecutionResultTokens)}\n" +
                $"Files and memory: {FormatTokenCount(usage.FilesTokens)}\n" +
                $"Total: {FormatTokenCount(usage.TotalTokens)} of {FormatTokenCount(usage.AvailableTokens)} tokens";
        }
        catch
        {
            if (loadVersion.HasValue && !IsCurrentChatVersion(chatId, loadVersion.Value))
                return;
            ContextUsageText = "Context unavailable";
            ContextUsageToolTip = "Context usage could not be calculated for this chat.";
        }
    }

    private bool IsCurrentChatVersion(Guid chatId, long version) =>
        version == Volatile.Read(ref _chatLoadVersion) && _appState.CurrentChatId == chatId;

    private static string FormatTokenCount(int value)
    {
        var abs = Math.Abs(value);
        return abs >= 1_000_000
            ? $"{value / 1_000_000d:F1}M"
            : abs >= 1_000
                ? $"{value / 1_000d:F1}k"
                : value.ToString();
    }

    private void ClearAgentActivity()
    {
        AgentActivityRuns.Clear();
        HasAgentActivity = false;
        AgentActivitySummary = "No agent activity yet.";
        AgentActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpsertAgentActivity(AgentProgressUpdate update, AgentProgressLine line)
    {
        var run = AgentActivityRuns.FirstOrDefault(r => r.Id == update.AgentRunId);
        if (run == null)
        {
            run = AgentActivityRun.FromLive(update.Run, _activeAgentRequest);
            AgentActivityRuns.Insert(0, run);
        }
        else
        {
            run.UpdateFromLive(update.Run);
            var index = AgentActivityRuns.IndexOf(run);
            if (index > 0)
            {
                AgentActivityRuns.RemoveAt(index);
                AgentActivityRuns.Insert(0, run);
            }
        }

        var lineIndex = run.Lines.FindIndex(l => l.SequenceNumber == line.SequenceNumber);
        if (lineIndex >= 0)
        {
            run.Lines[lineIndex] = line;
        }
        else
        {
            run.Lines.Add(line);
            run.Lines.Sort(static (a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        }

        if (update.EventType is AgentEventTypes.TaskUpdated or AgentEventTypes.BackgroundTaskUpdated)
            run.UpdateTasksFromEvent(update.DataJson);

        RefreshAgentActivityState();
    }

    private void RefreshAgentActivityState()
    {
        HasAgentActivity = AgentActivityRuns.Count > 0;
        AgentActivitySummary = AgentActivityRuns.FirstOrDefault() is { } latest
            ? $"{latest.StatusText} · {latest.Lines.Count} event{(latest.Lines.Count == 1 ? string.Empty : "s")}"
            : "No agent activity yet.";
        AgentActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static AgentProgressLine? FormatAgentProgress(
        AgentActivityRunSnapshot run,
        AgentActivityEventSnapshot evt)
    {
        var snapshot = new AgentRunSnapshot(
            run.Id,
            run.ChatId,
            run.TurnId,
            run.Status,
            run.CurrentStep,
            run.MaxSteps,
            run.ErrorMessage,
            run.ArtifactCount,
            null);
        return FormatAgentProgress(new AgentProgressUpdate(
            run.Id,
            evt.SequenceNumber,
            evt.EventType,
            evt.Severity,
            evt.Summary,
            evt.CreatedAt,
            snapshot,
            evt.AgentStepId,
            evt.ToolInvocationId,
            evt.DataJson));
    }

    private static AgentProgressLine? FormatAgentProgress(AgentProgressUpdate update)
    {
        var label = update.EventType switch
        {
            AgentEventTypes.RunStarted => "Start",
            AgentEventTypes.Resume => "Resume",
            AgentEventTypes.ModelRequest => "Plan",
            AgentEventTypes.ModelResponse => "Model",
            AgentEventTypes.ToolRequest => "Tool",
            AgentEventTypes.ApprovalRequested => "Approval",
            AgentEventTypes.ApprovalGranted => "Approved",
            AgentEventTypes.ApprovalDenied => "Denied",
            AgentEventTypes.ToolStarted => "Run",
            AgentEventTypes.ToolProgress => "Progress",
            AgentEventTypes.ToolHookBlocked => "Hook",
            AgentEventTypes.ToolRollbackPlan => "Rollback",
            AgentEventTypes.ToolResult => "Result",
            AgentEventTypes.TaskUpdated => "Tasks",
            AgentEventTypes.BackgroundTaskUpdated => "Task",
            AgentEventTypes.ProtocolRepair => "Repair",
            AgentEventTypes.RuntimeMetrics => "Metrics",
            AgentEventTypes.RunCompleted => "Done",
            AgentEventTypes.RunPaused => "Paused",
            AgentEventTypes.RunCancelled => "Stopped",
            AgentEventTypes.Error => "Error",
            _ => "Event"
        };

        var render = TryReadRender(update.DataJson);
        var stepNumber = TryReadStepNumber(update.DataJson) ?? update.Run.CurrentStep;
        var text = update.EventType switch
        {
            AgentEventTypes.ModelRequest => $"Planning step {Math.Max(stepNumber, 1)}...",
            AgentEventTypes.ModelResponse => TryReadToolCount(update.DataJson) > 0
                ? "Model selected a tool call."
                : "Model returned a response.",
            AgentEventTypes.ToolRequest => render?.Activity ?? CleanSummary(update.Summary),
            AgentEventTypes.ToolStarted => render?.Activity ?? CleanSummary(update.Summary),
            AgentEventTypes.ToolProgress => TryReadToolProgress(update.DataJson) ?? CleanSummary(update.Summary),
            AgentEventTypes.ToolHookBlocked => CleanSummary(update.Summary),
            AgentEventTypes.ToolRollbackPlan => "Rollback plan ready.",
            AgentEventTypes.ToolResult => render?.Activity ?? CleanSummary(update.Summary),
            AgentEventTypes.TaskUpdated => "Task list updated.",
            AgentEventTypes.BackgroundTaskUpdated => "Background task updated.",
            AgentEventTypes.ApprovalRequested => render?.Activity ?? CleanSummary(update.Summary),
            AgentEventTypes.RuntimeMetrics => TryReadRuntimeMetrics(update.DataJson) ?? "Runtime metrics captured.",
            AgentEventTypes.RunCompleted => "Agent completed the task.",
            AgentEventTypes.RunPaused => "Agent paused at the step limit.",
            AgentEventTypes.RunCancelled => "Agent run stopped.",
            AgentEventTypes.Error => CleanSummary(update.Summary),
            AgentEventTypes.ProtocolRepair => "Adjusted provider message format before continuing.",
            _ => CleanSummary(update.Summary)
        };

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // M4.9.5 Phase D: derive the tool-call lifecycle status from the event
        // type + severity so the tool card can render a colored status dot.
        var status = update.EventType switch
        {
            AgentEventTypes.ToolRequest => ToolCallStatuses.Pending,
            AgentEventTypes.ToolStarted or AgentEventTypes.ToolProgress => ToolCallStatuses.Running,
            AgentEventTypes.ToolResult => update.Severity == AgentEventSeverities.Error
                ? ToolCallStatuses.Error : ToolCallStatuses.Done,
            AgentEventTypes.ApprovalDenied or AgentEventTypes.ToolRollbackPlan => ToolCallStatuses.Cancelled,
            AgentEventTypes.ApprovalRequested => ToolCallStatuses.Pending,
            AgentEventTypes.Error => ToolCallStatuses.Error,
            _ => ToolCallStatuses.Info
        };

        return new AgentProgressLine(
            update.SequenceNumber,
            label,
            text,
            update.Severity,
            update.CreatedAt,
            render?.Title,
            render?.Preview,
            render?.RenderHint,
            render?.IsTruncated ?? false,
            render?.PrimaryPath,
            DepthForEvent(update.EventType),
            status);
    }

    /// <summary>
    /// M4.9.3: Tree depth for the activity timeline. Root events (Start/Resume)
    /// are depth 0; model/plan and tool/approval top-level events are depth 1;
    /// their child events (tool progress/result, approval granted/denied) are
    /// depth 2 so the tree connector indents under the parent.
    /// </summary>
    private static int DepthForEvent(string eventType) => eventType switch
    {
        AgentEventTypes.RunStarted or AgentEventTypes.Resume => 0,
        AgentEventTypes.ModelRequest or AgentEventTypes.ModelResponse => 1,
        AgentEventTypes.ToolRequest or AgentEventTypes.ApprovalRequested => 1,
        AgentEventTypes.ToolStarted or AgentEventTypes.ToolProgress or
        AgentEventTypes.ToolHookBlocked or AgentEventTypes.ToolRollbackPlan or
        AgentEventTypes.ToolResult => 2,
        AgentEventTypes.ApprovalGranted or AgentEventTypes.ApprovalDenied => 2,
        AgentEventTypes.RunCompleted or AgentEventTypes.RunPaused or
        AgentEventTypes.RunCancelled or AgentEventTypes.Error => 1,
        _ => 1
    };

    private static string CleanSummary(string summary) =>
        summary.Replace("Agent run", "Agent", StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static int TryReadToolCount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("toolCallCount", out var count) &&
                   count.ValueKind == JsonValueKind.Number
                ? count.GetInt32()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int? TryReadStepNumber(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadInt(doc.RootElement, "stepNumber");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadRuntimeMetrics(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var eventCount = TryReadInt(root, "EventCount");
            var dbWriteMs = TryReadDouble(root, "dbWriteMs");
            var elapsedMs = TryReadDouble(root, "elapsedMs");
            if (eventCount == null && dbWriteMs == null && elapsedMs == null)
                return null;
            return $"Events {eventCount ?? 0} · DB {dbWriteMs ?? 0:0}ms · elapsed {elapsedMs ?? 0:0}ms";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadToolProgress(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var message = TryReadString(root, "Message") ?? TryReadString(root, "message");
            var percent = TryReadInt(root, "Percent") ?? TryReadInt(root, "percent");
            if (string.IsNullOrWhiteSpace(message))
                return null;
            return percent is null ? message : $"{message} ({percent}%)";
        }
        catch
        {
            return null;
        }
    }

    private static AgentProgressRender? TryReadRender(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("render", out var render) ||
                render.ValueKind != JsonValueKind.Object)
                return null;

            var title = TryReadString(render, "Title") ?? TryReadString(render, "title");
            var activity = TryReadString(render, "Subtitle") ?? TryReadString(render, "subtitle");
            var body = TryReadString(render, "Body") ?? TryReadString(render, "body");
            var hint = TryReadString(render, "RenderHint") ?? TryReadString(render, "renderHint");
            var path = TryReadString(render, "PrimaryPath") ?? TryReadString(render, "primaryPath");
            var truncated = TryReadBool(render, "IsTruncated") ?? TryReadBool(render, "isTruncated") ?? false;
            var preview = PreviewText(body);

            if (string.IsNullOrWhiteSpace(title) &&
                string.IsNullOrWhiteSpace(activity) &&
                string.IsNullOrWhiteSpace(preview))
                return null;

            return new AgentProgressRender(
                title ?? string.Empty,
                activity ?? string.Empty,
                preview ?? string.Empty,
                hint ?? string.Empty,
                truncated,
                path);
        }
        catch
        {
            return null;
        }
    }

    private static string? PreviewText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var compact = string.Join(
            " ",
            text.Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length <= 180)
            return compact;
        return compact[..180] + "...";
    }

    private static string? TryReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? TryReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool? TryReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private void UpdateAgentStatus(AgentRunSnapshot? run)
    {
        if (run == null)
        {
            IsAgentStatusVisible = false;
            AgentStatusText = string.Empty;
            CurrentAgentRunId = null;
            CurrentAgentRunStatus = null;
            return;
        }

        CurrentAgentRunId = run.Id;
        CurrentAgentRunStatus = run.Status;
        IsAgentStatusVisible = true;
        var artifacts = run.ArtifactCount == 0
            ? string.Empty
            : $" • {run.ArtifactCount} artifact{(run.ArtifactCount == 1 ? string.Empty : "s")}";
        AgentStatusText = run.Status switch
        {
            AgentRunStatuses.Running => $"Agent running • step {run.CurrentStep}/{run.MaxSteps}{artifacts}",
            AgentRunStatuses.AwaitingApproval => $"Agent waiting for tool approval • step {run.CurrentStep}/{run.MaxSteps}",
            AgentRunStatuses.Paused => $"Agent paused • step {run.CurrentStep}/{run.MaxSteps}{artifacts}",
            AgentRunStatuses.Completed => $"Agent completed • {run.CurrentStep} steps{artifacts}",
            AgentRunStatuses.Cancelled => $"Agent stopped • progress saved at step {run.CurrentStep}{artifacts}",
            AgentRunStatuses.Failed => $"Agent failed at step {run.CurrentStep}: {run.ErrorMessage}",
            _ => $"Agent {run.Status} • step {run.CurrentStep}/{run.MaxSteps}{artifacts}"
        };
    }

    /// <summary>Fired when a new Turn is created, for the DebugPanel to pick up.</summary>
    public event EventHandler<Guid>? TurnCreated;
    public event EventHandler<AgentApprovalRequest>? AgentApprovalRequested;
    public event EventHandler? StreamingMessageUpdated;
    public event Action<Guid>? MessageIdMutated; // M4.4.6: old Id before CopyPersistedMessage overwrites it
    public event EventHandler? AgentActivityChanged;
    protected virtual void OnTurnCreated(Guid turnId) =>
        TurnCreated?.Invoke(this, turnId);
}

internal sealed class AssistantStreamState(
    Message message,
    IProgress<LlmStreamUpdate> progress)
{
    public Message Message { get; } = message;
    public IProgress<LlmStreamUpdate> Progress { get; } = progress;
    public object Gate { get; } = new();
    public Queue<char> PendingTextChars { get; } = new();
    public Queue<char> PendingThinkingChars { get; } = new();
    public StringBuilder TextBuilder { get; } = new();
    public StringBuilder ThinkingBuilder { get; } = new();
    public bool IsThinkingCollapsed { get; set; }
    public bool IsDraining { get; set; }
    public string? FinalSnapshot { get; set; }

    public string ComposeContent()
    {
        if (ThinkingBuilder.Length == 0)
            return TextBuilder.ToString();

        return AssistantContentFormatter.Compose(
            TextBuilder.ToString(),
            ThinkingBuilder.ToString(),
            isThinkingExpanded: !IsThinkingCollapsed);
    }

    public bool FinalReceived { get; set; }
    public TaskCompletionSource FinalDrainCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void TryCompleteFinalDrainLocked()
    {
        if (FinalReceived &&
            PendingThinkingChars.Count == 0 &&
            PendingTextChars.Count == 0)
        {
            FinalDrainCompletion.TrySetResult();
        }
    }

    public async Task WaitForFinalDrainAsync(CancellationToken ct)
    {
        Task task;
        lock (Gate)
        {
            if (!FinalReceived)
                return;

            TryCompleteFinalDrainLocked();
            task = FinalDrainCompletion.Task;
        }

        using var registration = ct.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, FinalDrainCompletion);
        await task;
    }
}

internal sealed class InlineLlmStreamProgress(Action<LlmStreamUpdate> onUpdate) : IProgress<LlmStreamUpdate>
{
    public void Report(LlmStreamUpdate value) => onUpdate(value);
}

public sealed record AgentProgressLine(
    int SequenceNumber,
    string Label,
    string Text,
    string Severity,
    DateTime CreatedAt,
    string? ToolTitle = null,
    string? Preview = null,
    string? RenderHint = null,
    bool IsTruncated = false,
    string? PrimaryPath = null,
    int Depth = 1,
    // M4.9.5 Phase D: tool-call lifecycle status for the status dot.
    //   pending  — tool requested, not yet started (grey)
    //   running  — tool started / in progress (animated blue)
    //   done     — tool result, success (green)
    //   error    — tool result, error severity (red)
    //   cancelled— denied / rollback (amber)
    string Status = "info");

public static class ToolCallStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string Info = "info";
}

public sealed class AgentActivityRun
{
    public Guid Id { get; private set; }
    public Guid ChatId { get; private set; }
    public Guid TurnId { get; private set; }
    public string Status { get; private set; } = AgentRunStatuses.Running;
    public string UserRequest { get; private set; } = string.Empty;
    public int CurrentStep { get; private set; }
    public int MaxSteps { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int ArtifactCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public List<AgentProgressLine> Lines { get; } = new();
    public List<AgentTaskSnapshot> Tasks { get; } = new();

    public bool IsActive =>
        Status is AgentRunStatuses.Running or AgentRunStatuses.AwaitingApproval;

    public string DisplayTitle
    {
        get
        {
            var title = Compact(UserRequest);
            if (string.IsNullOrWhiteSpace(title))
                title = "Agent run";
            return title.Length <= 88 ? title : title[..88] + "...";
        }
    }

    public string StatusText
    {
        get
        {
            var artifacts = ArtifactCount == 0
                ? string.Empty
                : $" · {ArtifactCount} artifact{(ArtifactCount == 1 ? string.Empty : "s")}";
            return Status switch
            {
                AgentRunStatuses.Running => $"running · step {CurrentStep}/{MaxSteps}{artifacts}",
                AgentRunStatuses.AwaitingApproval => $"waiting · step {CurrentStep}/{MaxSteps}{artifacts}",
                AgentRunStatuses.Paused => $"paused · step {CurrentStep}/{MaxSteps}{artifacts}",
                AgentRunStatuses.Completed => $"completed · {CurrentStep} steps{artifacts}",
                AgentRunStatuses.Cancelled => $"stopped · step {CurrentStep}{artifacts}",
                AgentRunStatuses.Failed => $"failed · step {CurrentStep}{artifacts}",
                _ => $"{Status} · step {CurrentStep}/{MaxSteps}{artifacts}"
            };
        }
    }

    public string TimeText
    {
        get
        {
            var start = CreatedAt.ToLocalTime().ToString("MM/dd HH:mm");
            if (CompletedAt == null)
                return start;
            return $"{start} - {CompletedAt.Value.ToLocalTime():HH:mm}";
        }
    }

    public static AgentActivityRun FromSnapshot(AgentActivityRunSnapshot snapshot)
    {
        var run = new AgentActivityRun();
        run.Apply(
            snapshot.Id,
            snapshot.ChatId,
            snapshot.TurnId,
            snapshot.Status,
            snapshot.UserRequest,
            snapshot.CurrentStep,
            snapshot.MaxSteps,
            snapshot.ErrorMessage,
            snapshot.ArtifactCount,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            snapshot.CompletedAt);
        run.Tasks.Clear();
        if (snapshot.Tasks is { Count: > 0 })
            run.Tasks.AddRange(snapshot.Tasks);
        return run;
    }

    public static AgentActivityRun FromLive(AgentRunSnapshot snapshot, string? userRequest)
    {
        var run = new AgentActivityRun();
        run.Apply(
            snapshot.Id,
            snapshot.ChatId,
            snapshot.TurnId,
            snapshot.Status,
            userRequest ?? string.Empty,
            snapshot.CurrentStep,
            snapshot.MaxSteps,
            snapshot.ErrorMessage,
            snapshot.ArtifactCount,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null);
        return run;
    }

    public void UpdateFromLive(AgentRunSnapshot snapshot)
    {
        Apply(
            snapshot.Id,
            snapshot.ChatId,
            snapshot.TurnId,
            snapshot.Status,
            UserRequest,
            snapshot.CurrentStep,
            snapshot.MaxSteps,
            snapshot.ErrorMessage,
            snapshot.ArtifactCount,
            CreatedAt == default ? DateTime.UtcNow : CreatedAt,
            DateTime.UtcNow,
            snapshot.Status is AgentRunStatuses.Completed or AgentRunStatuses.Cancelled or AgentRunStatuses.Failed
                ? DateTime.UtcNow
                : CompletedAt);
    }

    private void Apply(
        Guid id,
        Guid chatId,
        Guid turnId,
        string status,
        string userRequest,
        int currentStep,
        int maxSteps,
        string? errorMessage,
        int artifactCount,
        DateTime createdAt,
        DateTime updatedAt,
        DateTime? completedAt)
    {
        Id = id;
        ChatId = chatId;
        TurnId = turnId;
        Status = status;
        UserRequest = userRequest;
        CurrentStep = currentStep;
        MaxSteps = maxSteps;
        ErrorMessage = errorMessage;
        ArtifactCount = artifactCount;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        CompletedAt = completedAt;
    }

    public void UpdateTasksFromEvent(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (!TryGetProperty(doc.RootElement, "tasks", out var tasksElement) ||
                tasksElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var parsed = new List<AgentTaskSnapshot>();
            foreach (var item in tasksElement.EnumerateArray())
            {
                if (!TryReadTask(item, out var task))
                    continue;
                parsed.Add(task);
            }

            if (parsed.Count > 0)
            {
                Tasks.Clear();
                Tasks.AddRange(parsed);
            }
        }
        catch
        {
            // Ignore malformed live task metadata; persisted snapshots will catch up on reload.
        }
    }

    private static bool TryReadTask(JsonElement item, out AgentTaskSnapshot task)
    {
        task = default!;
        if (!TryGetGuid(item, "id", out var id) || !TryGetGuid(item, "chatId", out var chatId))
            return false;
        TryGetGuid(item, "agentRunId", out var runId);
        task = new AgentTaskSnapshot(
            id,
            chatId,
            runId == Guid.Empty ? null : runId,
            GetString(item, "title"),
            GetString(item, "description"),
            GetString(item, "status", AgentTaskStatuses.Pending),
            GetString(item, "priority", "medium"),
            GetString(item, "source"),
            GetDate(item, "createdAt"),
            GetDate(item, "updatedAt"),
            TryGetDate(item, "completedAt", out var completedAt) ? completedAt : null,
            GetString(item, "metadataJson", "{}"));
        return true;
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value)
    {
        if (item.TryGetProperty(name, out value))
            return true;
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetGuid(JsonElement item, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryGetProperty(item, name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               Guid.TryParse(element.GetString(), out value);
    }

    private static string GetString(JsonElement item, string name, string fallback = "") =>
        TryGetProperty(item, name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static DateTime GetDate(JsonElement item, string name) =>
        TryGetDate(item, name, out var value) ? value : DateTime.UtcNow;

    private static bool TryGetDate(JsonElement item, string name, out DateTime value)
    {
        value = default;
        return TryGetProperty(item, name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(element.GetString(), out value);
    }

    private static string Compact(string value) =>
        string.Join(
            " ",
            value.Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

internal sealed record AgentProgressRender(
    string Title,
    string Activity,
    string Preview,
    string RenderHint,
    bool IsTruncated,
    string? PrimaryPath);

public enum AgentApprovalChoice
{
    DenyOnce,
    AlwaysDeny,
    AllowOnce,
    AllowForProject,
    AllowGlobally
}

public sealed class AgentApprovalRequest : EventArgs
{
    public AgentApprovalRequest(
        Guid agentRunId,
        Guid invocationId,
        string toolName,
        string argumentsJson,
        string safetyLevel = "unknown",
        string safetySummary = "",
        string safetyJson = "{}")
    {
        AgentRunId = agentRunId;
        InvocationId = invocationId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        SafetyLevel = safetyLevel;
        SafetySummary = safetySummary;
        SafetyJson = safetyJson;
    }

    public Guid AgentRunId { get; }
    public Guid InvocationId { get; }
    public string ToolName { get; }
    public string ArgumentsJson { get; }
    public string SafetyLevel { get; }
    public string SafetySummary { get; }
    public string SafetyJson { get; }
    /// <summary>M4.9.0: Updated tool arguments (e.g. AskUserQuestion answers).</summary>
    public string? UpdatedArgumentsJson { get; set; }
    public TaskCompletionSource<AgentApprovalChoice> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
