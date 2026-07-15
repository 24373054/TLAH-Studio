using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Core.Services.Sdk;

namespace TLAHStudio.Core.Tests;

public sealed class LocalSdkHostTests
{
    [Fact]
    public async Task HandleRequest_ProductionHostCreatesAndDisposesScopePerRequest()
    {
        var scopeFactory = new TrackingScopeFactory();
        var host = new LocalSdkHost(scopeFactory);
        var request = JsonSerializer.Serialize(new
        {
            method = "send_message",
            chat_id = Guid.NewGuid(),
            content = "hello"
        });

        _ = await host.HandleRequestAsync(request, CancellationToken.None);
        _ = await host.HandleRequestAsync(request, CancellationToken.None);

        Assert.Equal(2, scopeFactory.CreatedCount);
        Assert.Equal(2, scopeFactory.DisposedCount);
    }

    [Fact]
    public async Task ApproveTool_WithRunId_PersistsDecisionAndResumesInOneRequest()
    {
        var llm = new RecordingLlmService();
        var host = new LocalSdkHost(llm, new UnusedChatService());
        var invocationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var response = await host.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            method = "approve_tool",
            invocation_id = invocationId,
            approved = true,
            scope = ToolPolicyScopes.Project,
            run_id = runId,
            permission_mode = AgentPermissionModes.BypassPermissions,
            max_steps = 72,
            updated_arguments_json = "{\"path\":\"C:\\\\Users\\\\Public\\\\note.txt\"}"
        }), CancellationToken.None);

        Assert.Equal(invocationId, llm.ApprovedInvocationId);
        Assert.True(llm.Approved);
        Assert.Equal(ToolPolicyScopes.Project, llm.ApprovalScope);
        Assert.Equal("{\"path\":\"C:\\\\Users\\\\Public\\\\note.txt\"}", llm.UpdatedArgumentsJson);
        Assert.Equal(runId, llm.ResumedRunId);
        Assert.Equal(72, llm.ResumeOptions?.MaxSteps);
        Assert.Equal(AgentPermissionModes.BypassPermissions, llm.ResumeOptions?.PermissionMode);

        using var parsed = JsonDocument.Parse(response);
        Assert.True(parsed.RootElement.GetProperty("resumed").GetBoolean());
        Assert.Equal(runId.ToString(), parsed.RootElement.GetProperty("run_id").GetString());
    }

    [Fact]
    public async Task ApproveTool_WithoutRunId_ReportsRequiredResume()
    {
        var llm = new RecordingLlmService();
        var host = new LocalSdkHost(llm, new UnusedChatService());

        var response = await host.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            method = "approve_tool",
            invocation_id = Guid.NewGuid(),
            approved = false,
            scope = ToolPolicyScopes.Once
        }), CancellationToken.None);

        Assert.Null(llm.ResumedRunId);
        using var parsed = JsonDocument.Parse(response);
        Assert.False(parsed.RootElement.GetProperty("resumed").GetBoolean());
        Assert.True(parsed.RootElement.GetProperty("requires_resume").GetBoolean());
    }

    [Fact]
    public async Task ResumeRun_UsesRequestedPermissionAndTimeoutOptions()
    {
        var llm = new RecordingLlmService();
        var host = new LocalSdkHost(llm, new UnusedChatService());
        var runId = Guid.NewGuid();

        _ = await host.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            method = "resume_run",
            run_id = runId,
            max_steps = 80,
            command_timeout_seconds = 180,
            permission_mode = "auto",
            auto_approve_tools = true
        }), CancellationToken.None);

        Assert.Equal(runId, llm.ResumedRunId);
        Assert.Equal(80, llm.ResumeOptions?.MaxSteps);
        Assert.Equal(180, llm.ResumeOptions?.CommandTimeoutSeconds);
        Assert.Equal(AgentPermissionModes.AutoApprove, llm.ResumeOptions?.PermissionMode);
        Assert.True(llm.ResumeOptions?.AutoApproveTools);
    }

    [Fact]
    public async Task ResumeRun_WithoutPermissionModeRequestsCheckpointPreservation()
    {
        var llm = new RecordingLlmService();
        var host = new LocalSdkHost(llm, new UnusedChatService());

        _ = await host.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            method = "resume_run",
            run_id = Guid.NewGuid()
        }), CancellationToken.None);

        Assert.Equal(string.Empty, llm.ResumeOptions?.PermissionMode);
        Assert.False(llm.ResumeOptions?.AutoApproveTools);
    }

    private sealed class RecordingLlmService : ILlmService
    {
        public Guid? ApprovedInvocationId { get; private set; }
        public bool? Approved { get; private set; }
        public string? ApprovalScope { get; private set; }
        public string? UpdatedArgumentsJson { get; private set; }
        public Guid? ResumedRunId { get; private set; }
        public AgentRunOptions? ResumeOptions { get; private set; }

        public Task SetAgentToolApprovalAsync(
            Guid invocationId,
            bool approved,
            string policyScope = "once",
            CancellationToken ct = default,
            string? updatedArgumentsJson = null)
        {
            ApprovedInvocationId = invocationId;
            Approved = approved;
            ApprovalScope = policyScope;
            UpdatedArgumentsJson = updatedArgumentsJson;
            return Task.CompletedTask;
        }

        public Task<SendMessageResult> ResumeAgentTaskAsync(
            Guid agentRunId,
            AgentRunOptions? options = null,
            CancellationToken ct = default)
        {
            ResumedRunId = agentRunId;
            ResumeOptions = options;
            return Task.FromResult(Result("resumed"));
        }

        public Task<SendMessageResult> SendMessageAsync(Guid chatId, string userContent, string? role = null, CancellationToken ct = default, IProgress<LlmStreamUpdate>? stream = null) =>
            Task.FromResult(Result("sent"));
        public Task<SendMessageResult> RunAgentTaskAsync(Guid chatId, string userContent, string? role = null, AgentRunOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(Result("started"));
        public Task<AgentRunSnapshot?> GetLatestAgentRunAsync(Guid chatId, CancellationToken ct = default) => Task.FromResult<AgentRunSnapshot?>(null);
        public Task<IReadOnlyList<AgentActivityRunSnapshot>> GetAgentActivityAsync(Guid chatId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentActivityRunSnapshot>>([]);
        public Task<ContextUsageSnapshot> GetContextUsageAsync(Guid chatId, CancellationToken ct = default) =>
            Task.FromResult(new ContextUsageSnapshot(0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty));
        public Task CancelAgentRunAsync(Guid agentRunId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SendMessageResult> RegenerateAssistantAsync(Guid assistantMessageId, CancellationToken ct = default) => Task.FromResult(Result("regenerated"));
        public Task<SendMessageResult> EditAndResendAsync(Guid messageId, string content, CancellationToken ct = default) => Task.FromResult(Result("edited"));
        public Task<SendMessageResult> ContinueFromMessageAsync(Guid messageId, CancellationToken ct = default) => Task.FromResult(Result("continued"));
        public Task<SendMessageResult> ReplayTurnAsync(Guid turnId, CancellationToken ct = default) => Task.FromResult(Result("replayed"));
        public Task<ConnectionTestResult> TestConnectionAsync(string provider, string apiKey, string baseUrl, string model, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true, "ok"));
        public Task<IReadOnlyList<string>> ListModelsAsync(string provider, string apiKey, string baseUrl, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        private static SendMessageResult Result(string content)
        {
            var turn = new Turn();
            return new SendMessageResult(
                turn,
                new Message { TurnId = turn.Id, Role = "user", Content = "request" },
                new Message { TurnId = turn.Id, Role = "assistant", Content = content },
                new RawRequest { TurnId = turn.Id },
                new RawResponse { TurnId = turn.Id });
        }
    }

    private sealed class UnusedChatService : IChatService
    {
        public Task<Chat> CreateChatAsync(string title = "New Chat", CancellationToken ct = default) => throw Unused();
        public Task<Chat?> GetChatAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task<Chat> GetChatOrThrowAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task<List<ChatSummaryDto>> ListChatsAsync(string? search = null, bool includeArchived = false, CancellationToken ct = default) => throw Unused();
        public Task<Chat> UpdateChatAsync(Guid chatId, string? title = null, string? systemPrompt = null, CancellationToken ct = default) => throw Unused();
        public Task<Chat> SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken ct = default) => throw Unused();
        public Task<Chat> SetArchivedAsync(Guid chatId, bool isArchived, CancellationToken ct = default) => throw Unused();
        public Task DeleteChatAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task RestoreChatAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task<string> ExportChatJsonAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task<List<Message>> GetChatMessagesAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task<ChatMessagePage> GetChatMessagePageAsync(Guid chatId, int? beforeSequenceNum = null, int pageSize = 80, CancellationToken ct = default) => throw Unused();
        public Task<int> GetNextSequenceAsync(Guid chatId, CancellationToken ct = default) => throw Unused();
        public Task DeleteMessagesAfterAsync(Guid messageId, bool includeSelected = false, CancellationToken ct = default) => throw Unused();
        public Task<Message> UpdateMessageContentAsync(Guid messageId, string content, CancellationToken ct = default) => throw Unused();
        private static InvalidOperationException Unused() => new("Not used by this SDK test.");
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        private int _createdCount;
        private int _disposedCount;

        public int CreatedCount => Volatile.Read(ref _createdCount);
        public int DisposedCount => Volatile.Read(ref _disposedCount);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _createdCount);
            return new TrackingScope(
                new RecordingLlmService(),
                new UnusedChatService(),
                () => Interlocked.Increment(ref _disposedCount));
        }
    }

    private sealed class TrackingScope(
        ILlmService llmService,
        IChatService chatService,
        Action onDispose) : IServiceScope
    {
        private int _disposed;

        public IServiceProvider ServiceProvider { get; } =
            new TrackingServiceProvider(llmService, chatService);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }

    private sealed class TrackingServiceProvider(
        ILlmService llmService,
        IChatService chatService) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILlmService)
            ? llmService
            : serviceType == typeof(IChatService)
                ? chatService
                : null;
    }

}
