using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.App.Models;

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
    private CancellationTokenSource? _sendCts;
    private IProgress<AgentProgressUpdate>? _activeAgentProgress;
    private AssistantStreamState? _activeStream;
    private string? _activeAgentRequest;
    private const string AgentActivityPanelStorageKey = "tlah-agent-activity-panel-open";

    /// <summary>
    /// M2.8.0: Typed content blocks for virtualized rendering.
    /// Replaces monolithic message rendering with independently renderable blocks.
    /// </summary>
    public ObservableCollection<ChatMessageBlock> ChatMessageBlocks { get; } = new();

    private readonly ChatRenderer _chatRenderer = new();

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
    private Chat? _currentChat;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _selectedRole = "user";

    [ObservableProperty]
    private bool _isAgentModeEnabled;

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

    public List<string> AvailableRoles { get; } = new() { "user", "system" };

    public ChatPageViewModel(
        IChatService chatService,
        ILlmService llmService,
        ISettingsService settingsService,
        IAppStateService appState)
    {
        _chatService = chatService;
        _llmService = llmService;
        _settingsService = settingsService;
        _appState = appState;

        // React to chat selection changes from AppStateService
        _appState.ChatSelected += OnChatSelected;
        _appState.ChatDeselected += OnChatDeselected;
    }

    partial void OnIsAgentActivityPanelOpenChanged(bool value)
    {
        LocalStore.Set(AgentActivityPanelStorageKey, value ? "true" : "false");
    }

    private async void OnChatSelected(object? sender, Guid chatId)
    {
        await LoadChatAsync(chatId);
    }

    private void OnChatDeselected(object? sender, EventArgs e)
    {
        CurrentChat = null;
        Messages.Clear();
        ChatMessageBlocks.Clear();
        ClearAgentProgress();
        ClearAgentActivity();
        UpdateAgentStatus(null);
    }

    [RelayCommand]
    public async Task LoadChatAsync(Guid chatId)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var chat = await _chatService.GetChatAsync(chatId);
            CurrentChat = chat;
            Messages.Clear();
            ChatMessageBlocks.Clear();
            ClearAgentProgress();
            ClearAgentActivity();
            if (chat?.Messages != null)
            {
                var msgList = chat.Messages.ToList();
                foreach (var msg in msgList)
                    Messages.Add(msg);
                // M2.8.0: Populate typed blocks
                ChatMessageBlocks.Clear();
                foreach (var block in _chatRenderer.RenderAll(msgList))
                    ChatMessageBlocks.Add(block);
            }
            await LoadAgentActivityAsync(chatId);
            UpdateAgentStatus(await _llmService.GetLatestAgentRunAsync(chatId));
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
    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending)
            return;

        if (_appState.CurrentChatId == null)
            return;

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
                    new AgentRunOptions(OutputStream: stream.Progress, Progress: agentProgress),
                    _sendCts.Token)
                : await _llmService.SendMessageAsync(
                    _appState.CurrentChatId.Value,
                    content,
                    role,
                    _sendCts.Token,
                    stream.Progress);

            if (agentMode)
                result = await CompleteApprovalFlowAsync(result, _sendCts.Token);

            await stream.WaitForFinalDrainAsync(_sendCts.Token);
            ApplySendResultToLiveMessages(result, optimisticMessage, stream);
            if (agentMode)
                await ReloadCurrentChatAsync();

            // Notify debug panel of the new turn
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            RemoveOptimisticMessage(optimisticMessage);
            RemoveStreamMessage(stream.Message);
            ErrorMessage = "Request stopped.";
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
                new AgentRunOptions(OutputStream: stream.Progress, Progress: agentProgress),
                _sendCts.Token);
            result = await CompleteApprovalFlowAsync(result, _sendCts.Token);
            await stream.WaitForFinalDrainAsync(_sendCts.Token);
            ApplySendResultToLiveMessages(result, userMessage: null, stream);
            await ReloadCurrentChatAsync();
            TurnCreated?.Invoke(this, result.Turn.Id);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Agent run stopped.";
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

        // M2.8.0: Finalize streaming blocks
        if (stream?.Message != null)
            _chatRenderer.FinalizeStreaming(ChatMessageBlocks, stream.Message);

        UpdateAgentStatus(result.AgentRun);
        StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static void CopyPersistedMessage(Message source, Message target)
    {
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
                ct);
            AgentStatusText = choice is AgentApprovalChoice.DenyOnce or AgentApprovalChoice.AlwaysDeny
                ? "Tool denied. Asking the agent for a safer next step..."
                : "Tool approved. Continuing the agent run...";
            result = await _llmService.ResumeAgentTaskAsync(
                request.AgentRunId,
                new AgentRunOptions(OutputStream: _activeStream?.Progress, Progress: _activeAgentProgress),
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

        // M2.8.0: Create initial streaming block
        var streamBlock = ChatMessageBlock.TextBlock(message.Id, "assistant", string.Empty, ChatMessageBlocks.Count);
        streamBlock.IsStreaming = true;
        ChatMessageBlocks.Add(streamBlock);

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
                        > 400 => 24,
                        > 160 => 12,
                        > 60 => 6,
                        > 12 => 3,
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

                    // M2.8.0: Incremental block update — no full tree rebuild
                    _chatRenderer.UpdateStreamingBlock(ChatMessageBlocks, stream.Message);
                }

                if (!changed)
                    break;
                StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
                await Task.Delay(16);
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
            return;
        }

        AgentProgressLines.Add(line);
        while (AgentProgressLines.Count > 200)
            AgentProgressLines.RemoveAt(0);

        UpsertAgentActivity(update, line);
        AgentLiveSummary = line.Text;
        IsAgentLiveVisible = true;
    }

    private async Task LoadAgentActivityAsync(Guid chatId)
    {
        var snapshots = await _llmService.GetAgentActivityAsync(chatId);
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

        if (!run.Lines.Any(l => l.SequenceNumber == line.SequenceNumber))
        {
            run.Lines.Add(line);
            run.Lines.Sort(static (a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        }

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
            render?.PrimaryPath);
    }

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
    string? PrimaryPath = null);

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
    public TaskCompletionSource<AgentApprovalChoice> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
