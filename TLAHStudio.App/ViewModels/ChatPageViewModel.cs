using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ObservableCollection<Message> Messages { get; } = new();

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
        IsSending = true;
        ErrorMessage = null;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();

        try
        {
            var result = IsAgentModeEnabled
                ? await _llmService.RunAgentTaskAsync(
                    _appState.CurrentChatId.Value,
                    content,
                    role,
                    new AgentRunOptions(),
                    _sendCts.Token)
                : await _llmService.SendMessageAsync(
                    _appState.CurrentChatId.Value,
                    content,
                    role,
                    _sendCts.Token);

            if (IsAgentModeEnabled)
                result = await CompleteApprovalFlowAsync(result, _sendCts.Token);

            // Reload so agent tool-request and sandbox-result messages appear in order.
            await ReloadCurrentChatAsync();

            // Notify debug panel of the new turn
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
            AgentStatusText = "Resuming saved agent run...";
            var result = await _llmService.ResumeAgentTaskAsync(
                CurrentAgentRunId.Value,
                new AgentRunOptions(),
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
                new AgentRunOptions(),
                ct);
            UpdateAgentStatus(result.AgentRun);
        }
        return result;
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
    protected virtual void OnTurnCreated(Guid turnId) =>
        TurnCreated?.Invoke(this, turnId);
}

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
