using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

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
    private bool _isDrainingStream;

    public ObservableCollection<Message> Messages { get; } = new();
    public ObservableCollection<AgentProgressLine> AgentProgressLines { get; } = new();

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

    private async void OnChatSelected(object? sender, Guid chatId)
    {
        await LoadChatAsync(chatId);
    }

    private void OnChatDeselected(object? sender, EventArgs e)
    {
        CurrentChat = null;
        Messages.Clear();
        ClearAgentProgress();
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
            ClearAgentProgress();
            if (chat?.Messages != null)
            {
                foreach (var msg in chat.Messages)
                    Messages.Add(msg);
            }
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

        InputText = string.Empty;
        var optimisticMessage = AddOptimisticUserMessage(content, role ?? "user");
        var stream = BeginAssistantStream();
        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();

        try
        {
            var agentProgress = IsAgentModeEnabled ? BeginAgentProgress(reset: true) : null;
            var result = IsAgentModeEnabled
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

            if (IsAgentModeEnabled)
                result = await CompleteApprovalFlowAsync(result, _sendCts.Token);

            // Reload so agent tool-request and sandbox-result messages appear in order.
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
                run.PendingApproval.ArgumentsJson);
            AgentApprovalRequested?.Invoke(this, request);
            AgentApprovalChoice choice;
            using (ct.Register(() => request.Completion.TrySetCanceled(ct)))
                choice = await request.Completion.Task;

            await _llmService.SetAgentToolApprovalAsync(
                request.InvocationId,
                choice != AgentApprovalChoice.AlwaysDeny,
                choice switch
                {
                    AgentApprovalChoice.AllowForProject => ToolPolicyScopes.Project,
                    AgentApprovalChoice.AlwaysDeny => ToolPolicyScopes.Global,
                    _ => "once"
                },
                ct);
            AgentStatusText = choice == AgentApprovalChoice.AlwaysDeny
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
        _activeStream = new AssistantStreamState(
            message,
            new Progress<LlmStreamUpdate>(OnAssistantStream));
        return _activeStream;
    }

    private void RemoveStreamMessage(Message? message)
    {
        if (message != null && message.TurnId == null)
            Messages.Remove(message);
    }

    private void OnAssistantStream(LlmStreamUpdate update)
    {
        if (_activeStream == null)
            return;

        if (!string.IsNullOrEmpty(update.Delta))
        {
            foreach (var ch in update.Delta)
                _activeStream.PendingChars.Enqueue(ch);
        }

        if (update.IsFinal)
            _activeStream.FinalSnapshot = update.Snapshot;

        if (!_isDrainingStream)
            _ = DrainAssistantStreamAsync();
    }

    private async Task DrainAssistantStreamAsync()
    {
        if (_activeStream == null)
            return;

        _isDrainingStream = true;
        try
        {
            while (_activeStream != null && _activeStream.PendingChars.Count > 0)
            {
                var batchSize = _activeStream.PendingChars.Count > 80 ? 3 : 1;
                for (var i = 0; i < batchSize && _activeStream.PendingChars.Count > 0; i++)
                    _activeStream.Builder.Append(_activeStream.PendingChars.Dequeue());
                _activeStream.Message.Content = _activeStream.Builder.ToString();
                StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
                await Task.Delay(10);
            }

            if (_activeStream is { FinalSnapshot: { } final } &&
                !string.Equals(_activeStream.Message.Content, final, StringComparison.Ordinal))
            {
                _activeStream.Builder.Clear();
                _activeStream.Builder.Append(final);
                _activeStream.Message.Content = final;
                StreamingMessageUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _isDrainingStream = false;
        }
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
        while (AgentProgressLines.Count > 18)
            AgentProgressLines.RemoveAt(0);

        AgentLiveSummary = line.Text;
        IsAgentLiveVisible = true;
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
            AgentEventTypes.ToolResult => "Result",
            AgentEventTypes.ProtocolRepair => "Repair",
            AgentEventTypes.RunCompleted => "Done",
            AgentEventTypes.RunPaused => "Paused",
            AgentEventTypes.RunCancelled => "Stopped",
            AgentEventTypes.Error => "Error",
            _ => "Event"
        };

        var text = update.EventType switch
        {
            AgentEventTypes.ModelRequest => $"Planning step {Math.Max(update.Run.CurrentStep + 1, 1)}...",
            AgentEventTypes.ModelResponse => TryReadToolCount(update.DataJson) > 0
                ? "Model selected a tool call."
                : "Model returned a response.",
            AgentEventTypes.ToolRequest => CleanSummary(update.Summary),
            AgentEventTypes.ToolStarted => CleanSummary(update.Summary),
            AgentEventTypes.ToolResult => CleanSummary(update.Summary),
            AgentEventTypes.ApprovalRequested => CleanSummary(update.Summary),
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
            update.CreatedAt);
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
    protected virtual void OnTurnCreated(Guid turnId) =>
        TurnCreated?.Invoke(this, turnId);
}

internal sealed class AssistantStreamState(
    Message message,
    IProgress<LlmStreamUpdate> progress)
{
    public Message Message { get; } = message;
    public IProgress<LlmStreamUpdate> Progress { get; } = progress;
    public Queue<char> PendingChars { get; } = new();
    public StringBuilder Builder { get; } = new();
    public string? FinalSnapshot { get; set; }
}

public sealed record AgentProgressLine(
    int SequenceNumber,
    string Label,
    string Text,
    string Severity,
    DateTime CreatedAt);

public enum AgentApprovalChoice
{
    AlwaysDeny,
    AllowOnce,
    AllowForProject
}

public sealed class AgentApprovalRequest : EventArgs
{
    public AgentApprovalRequest(
        Guid agentRunId,
        Guid invocationId,
        string toolName,
        string argumentsJson)
    {
        AgentRunId = agentRunId;
        InvocationId = invocationId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
    }

    public Guid AgentRunId { get; }
    public Guid InvocationId { get; }
    public string ToolName { get; }
    public string ArgumentsJson { get; }
    public TaskCompletionSource<AgentApprovalChoice> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
