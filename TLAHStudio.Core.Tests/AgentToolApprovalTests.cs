using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Data;

namespace TLAHStudio.Core.Tests;

public class AgentToolApprovalTests
{
    private const string OriginalArguments = "{\"value\":\"before\"}";

    [Fact]
    public async Task SetAgentToolApprovalAsync_DefaultApprovalPreservesOriginalArguments()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var protectedBefore = fixture.Invocation.ProtectedArgumentsJson;
        var redactedBefore = fixture.Invocation.ArgumentsJson;

        await fixture.Service.SetAgentToolApprovalAsync(
            fixture.Invocation.Id,
            approved: true);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(redactedBefore, saved.ArgumentsJson);
        Assert.Equal(protectedBefore, saved.ProtectedArgumentsJson);
        Assert.Equal(OriginalArguments, ProtectedLocalData.Reveal(saved.ProtectedArgumentsJson));
        Assert.True(saved.Approved);
        Assert.True(saved.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.Approved, saved.Status);
    }

    [Theory]
    [InlineData("{", "valid JSON")]
    [InlineData("{}", "value must be a non-empty string")]
    public async Task SetAgentToolApprovalAsync_InvalidPersistedPayloadCannotBeApproved(
        string persistedArguments,
        string expectedError)
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        fixture.Invocation.ArgumentsJson = SecretRedactor.RedactJson(persistedArguments);
        fixture.Invocation.ProtectedArgumentsJson = ProtectedLocalData.Protect(persistedArguments);
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetAgentToolApprovalAsync(
                fixture.Invocation.Id,
                approved: true,
                policyScope: ToolPolicyScopes.Global));

        Assert.Contains(expectedError, error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>()
            .Include(i => i.AgentRun)
            .SingleAsync();
        Assert.Null(saved.Approved);
        Assert.False(saved.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, saved.Status);
        Assert.Equal(AgentRunStatuses.AwaitingApproval, saved.AgentRun.Status);
        Assert.Empty(await fixture.Db.Set<ToolPolicyRule>().ToListAsync());
        Assert.Empty(await fixture.Db.Set<AgentEvent>().ToListAsync());
    }

    [Theory]
    [InlineData("{", "valid JSON")]
    [InlineData("[]", "JSON object")]
    [InlineData("", "non-empty JSON object")]
    public async Task SetAgentToolApprovalAsync_InvalidJsonEditLeavesInvocationUntouched(
        string updatedArgumentsJson,
        string expectedError)
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var protectedBefore = fixture.Invocation.ProtectedArgumentsJson;
        var redactedBefore = fixture.Invocation.ArgumentsJson;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetAgentToolApprovalAsync(
                fixture.Invocation.Id,
                approved: true,
                policyScope: ToolPolicyScopes.Global,
                updatedArgumentsJson: updatedArgumentsJson));

        Assert.Contains(expectedError, error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>()
            .Include(i => i.AgentRun)
            .SingleAsync();
        Assert.Equal(redactedBefore, saved.ArgumentsJson);
        Assert.Equal(protectedBefore, saved.ProtectedArgumentsJson);
        Assert.Null(saved.Approved);
        Assert.Null(saved.ApprovedAt);
        Assert.False(saved.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, saved.Status);
        Assert.Equal(AgentRunStatuses.AwaitingApproval, saved.AgentRun.Status);
        Assert.Empty(await fixture.Db.Set<ToolPolicyRule>().ToListAsync());
        Assert.Empty(await fixture.Db.Set<AgentEvent>().ToListAsync());
    }

    [Fact]
    public async Task SetAgentToolApprovalAsync_ToolValidationFailureLeavesInvocationUntouched()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var protectedBefore = fixture.Invocation.ProtectedArgumentsJson;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SetAgentToolApprovalAsync(
                fixture.Invocation.Id,
                approved: true,
                updatedArgumentsJson: "{\"value\":42}"));

        Assert.Contains("value must be a non-empty string", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(protectedBefore, saved.ProtectedArgumentsJson);
        Assert.Null(saved.Approved);
        Assert.False(saved.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, saved.Status);
    }

    [Fact]
    public async Task SetAgentToolApprovalAsync_InvalidScopeDoesNotDirtyTrackedState()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.SetAgentToolApprovalAsync(
                fixture.Invocation.Id,
                approved: true,
                policyScope: "invalid-scope"));

        Assert.Null(fixture.Invocation.Approved);
        Assert.False(fixture.Invocation.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, fixture.Invocation.Status);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Empty(await fixture.Db.Set<ToolPolicyRule>().ToListAsync());
    }

    [Fact]
    public async Task SetAgentToolApprovalAsync_ConcurrentDecisionCannotOverwriteWinner()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var dataSource = fixture.Db.Database.GetDbConnection().DataSource;
        var options = new DbContextOptionsBuilder<TlahDbContext>()
            .UseSqlite($"Data Source={dataSource}")
            .Options;
        await using var secondDb = new TlahDbContext(options);
        _ = await secondDb.Set<ToolInvocation>()
            .Include(i => i.AgentRun)
            .SingleAsync(i => i.Id == fixture.Invocation.Id);
        var secondService = new LlmService(
            secondDb,
            new ChatService(secondDb),
            new SettingsService(secondDb),
            new StaticHttpClientFactory(fixture.HttpClient),
            agentTools: new AgentToolRegistry([new ValidatingApprovalTool()]));

        await fixture.Service.SetAgentToolApprovalAsync(
            fixture.Invocation.Id,
            approved: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            secondService.SetAgentToolApprovalAsync(
                fixture.Invocation.Id,
                approved: false));

        Assert.Contains("already received", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(secondDb.ChangeTracker.Entries());
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>().SingleAsync();
        Assert.True(saved.Approved);
        Assert.True(saved.ExplicitUserApproval);
        Assert.Equal(ToolInvocationStatuses.Approved, saved.Status);
    }

    [Theory]
    [InlineData(true, ToolInvocationStatuses.Approved)]
    [InlineData(false, ToolInvocationStatuses.Denied)]
    public async Task SetAgentToolApprovalAsync_ValidExplicitEditIsValidatedAndPersisted(
        bool approved,
        string expectedStatus)
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        const string updated = "{\"value\":\"after\"}";

        await fixture.Service.SetAgentToolApprovalAsync(
            fixture.Invocation.Id,
            approved,
            updatedArgumentsJson: updated);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(updated, ProtectedLocalData.Reveal(saved.ProtectedArgumentsJson));
        Assert.Equal(SecretRedactor.RedactJson(updated), saved.ArgumentsJson);
        Assert.Equal(approved, saved.Approved);
        Assert.Equal(approved, saved.ExplicitUserApproval);
        Assert.Equal(expectedStatus, saved.Status);
    }

    private sealed class ApprovalFixture : IAsyncDisposable
    {
        private ApprovalFixture(
            TlahDbContext db,
            HttpClient httpClient,
            LlmService service,
            ToolInvocation invocation)
        {
            Db = db;
            HttpClient = httpClient;
            Service = service;
            Invocation = invocation;
        }

        public TlahDbContext Db { get; }
        public HttpClient HttpClient { get; }
        public LlmService Service { get; }
        public ToolInvocation Invocation { get; }

        public static async Task<ApprovalFixture> CreateAsync()
        {
            var db = TestDb.Create();
            var chatService = new ChatService(db);
            var settingsService = new SettingsService(db);
            var chat = await chatService.CreateChatAsync("Approval validation");
            var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
            db.Set<Turn>().Add(turn);
            await db.SaveChangesAsync();

            var run = new AgentRun
            {
                ChatId = chat.Id,
                TurnId = turn.Id,
                Status = AgentRunStatuses.AwaitingApproval,
                UserRequest = "Validate an edited invocation."
            };
            var step = new AgentStep
            {
                AgentRun = run,
                StepNumber = 1,
                Status = AgentStepStatuses.AwaitingApproval
            };
            var invocation = new ToolInvocation
            {
                AgentRun = run,
                AgentStep = step,
                ToolName = ValidatingApprovalTool.ToolName,
                ProviderCallId = "approval-call",
                ArgumentsJson = SecretRedactor.RedactJson(OriginalArguments),
                ProtectedArgumentsJson = ProtectedLocalData.Protect(OriginalArguments),
                Status = ToolInvocationStatuses.AwaitingApproval,
                RequiresApproval = true
            };
            db.AddRange(run, step, invocation);
            await db.SaveChangesAsync();

            var registry = new AgentToolRegistry([new ValidatingApprovalTool()]);
            var httpClient = new HttpClient(new MapHttpMessageHandler(_ =>
                MapHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
            var service = new LlmService(
                db,
                chatService,
                settingsService,
                new StaticHttpClientFactory(httpClient),
                agentTools: registry);
            return new ApprovalFixture(db, httpClient, service, invocation);
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class ValidatingApprovalTool : IAgentTool
    {
        public const string ToolName = "approval_validation_test";

        public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
            ToolName,
            "Validate approval edits in tests.",
            new Dictionary<string, object>
            {
                ["value"] = AgentToolSupport.StringProperty("A required test value.")
            },
            ["value"]);

        public bool RequiresApproval => true;

        public AgentToolValidationResult ValidateInput(string argumentsJson)
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return AgentToolValidationResult.Fail(error ?? "Invalid JSON.");
            if (!root.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                return AgentToolValidationResult.Fail("The value must be a non-empty string.");
            }

            return AgentToolValidationResult.Ok;
        }

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(true, "validated"));
    }
}

public class AgentResumeBudgetTests
{
    [Theory]
    [InlineData(ToolInvocationStatuses.Approved)]
    [InlineData(ToolInvocationStatuses.Denied)]
    public async Task ResumeAgentTaskAsync_ApprovalContinuationDoesNotExtendStepBudget(
        string invocationStatus)
    {
        await using var fixture = await ResumeFixture.CreateAsync(invocationStatus);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 20));

        Assert.Equal(8, fixture.Engine.ResumedMaxSteps);
        Assert.Equal(8, result.AgentRun!.MaxSteps);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_UserResumeWithoutApprovalResultExtendsStepBudget()
    {
        await using var fixture = await ResumeFixture.CreateAsync(invocationStatus: null);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 20));

        Assert.Equal(24, fixture.Engine.ResumedMaxSteps);
        Assert.Equal(24, result.AgentRun!.MaxSteps);
        Assert.False(LlmService.HasActiveResumeGate(fixture.RunId));
    }

    [Theory]
    [InlineData(192, 192, 96, 288)]
    [InlineData(2147483637, 2147483637, 96, int.MaxValue)]
    public async Task ResumeAgentTaskAsync_UserResumeExtendsBeyondAutomaticSoftCapWithSaturation(
        int currentStep,
        int maxSteps,
        int requestedSteps,
        int expectedMaxSteps)
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            invocationStatus: null,
            currentStep: currentStep,
            maxSteps: maxSteps);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: requestedSteps));

        Assert.Equal(expectedMaxSteps, fixture.Engine.ResumedMaxSteps);
        Assert.Equal(expectedMaxSteps, result.AgentRun!.MaxSteps);
    }

    [Theory]
    [InlineData(ToolInvocationStatuses.Running)]
    [InlineData(ToolInvocationStatuses.UnknownOutcome)]
    public async Task ResumeAgentTaskAsync_AcknowledgesUnknownOutcomeWithoutReplaying(
        string invocationStatus)
    {
        await using var fixture = await ResumeFixture.CreateAsync(invocationStatus);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 20));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Null(fixture.Engine.ResumedPendingInvocationId);
        Assert.Null(fixture.Engine.ResumedUnknownOutcomeInvocationId);
        Assert.True(fixture.Engine.ResumedRecoveryDirectivePending);
        Assert.Contains(
            fixture.Engine.ResumedMessages,
            message => message.Role == "tool" && message.ToolCallId == "resume-approval-call");

        var invocation = await fixture.Db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(ToolInvocationStatuses.UnknownOutcome, invocation.Status);
        Assert.NotNull(invocation.CompletedAt);
        Assert.Contains("not replayed", invocation.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_ThrowingProgressObserverDoesNotBreakResume()
    {
        await using var fixture = await ResumeFixture.CreateAsync(invocationStatus: null);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 4, Progress: new ThrowingProgress()));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_HistoricalDeniedInvocationDoesNotSuppressBudgetExtension()
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            ToolInvocationStatuses.Denied,
            checkpointReferencesInvocation: false);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 20));

        Assert.Equal(24, fixture.Engine.ResumedMaxSteps);
        Assert.Equal(24, result.AgentRun!.MaxSteps);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_ReconstructsApprovedInvocationWhenCheckpointPointerIsMissing()
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            ToolInvocationStatuses.Approved,
            checkpointReferencesInvocation: false);

        var result = await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 20));

        Assert.Equal(8, fixture.Engine.ResumedMaxSteps);
        Assert.NotNull(fixture.Engine.ResumedPendingInvocationId);
        Assert.Equal(8, result.AgentRun!.MaxSteps);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_RejectsASecondResumeWhileRunIsRunning()
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            invocationStatus: null,
            runStatus: AgentRunStatuses.Running);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ResumeAgentTaskAsync(fixture.RunId));

        Assert.Contains("already running", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_DoesNotBypassAnUnansweredApproval()
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            ToolInvocationStatuses.AwaitingApproval,
            runStatus: AgentRunStatuses.AwaitingApproval);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ResumeAgentTaskAsync(fixture.RunId));

        Assert.Contains("waiting", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, AgentPermissionModes.BypassPermissions, true)]
    [InlineData(true, AgentPermissionModes.Plan, false)]
    public async Task ResumeAgentTaskAsync_UsesRequestedPermissionUnlessPlanIsActive(
        bool activePlan,
        string expectedMode,
        bool expectedAutoApprove)
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            invocationStatus: null,
            activePlan: activePlan);

        await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(
                MaxSteps: 1,
                PermissionMode: AgentPermissionModes.BypassPermissions));

        Assert.Equal(expectedMode, fixture.Engine.ResumedStatePermissionMode);
        Assert.Equal(expectedAutoApprove, fixture.Engine.ResumedStateAutoApprove);
        Assert.Equal(expectedMode, fixture.Engine.ResumedOptionPermissionMode);
        Assert.Equal(expectedAutoApprove, fixture.Engine.ResumedOptionAutoApprove);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, true)]
    [InlineData(AgentPermissionModes.AutoApprove, true)]
    [InlineData(AgentPermissionModes.RequestApproval, false)]
    public async Task ResumeAgentTaskAsync_OmittedPermissionOverridePreservesCheckpointMode(
        string checkpointMode,
        bool checkpointAutoApprove)
    {
        await using var fixture = await ResumeFixture.CreateAsync(
            invocationStatus: null,
            checkpointPermissionMode: checkpointMode,
            checkpointAutoApprove: checkpointAutoApprove);

        await fixture.Service.ResumeAgentTaskAsync(
            fixture.RunId,
            new AgentRunOptions(MaxSteps: 1, PermissionMode: string.Empty));

        Assert.Equal(checkpointMode, fixture.Engine.ResumedStatePermissionMode);
        Assert.Equal(checkpointAutoApprove, fixture.Engine.ResumedStateAutoApprove);
        Assert.Equal(checkpointMode, fixture.Engine.ResumedOptionPermissionMode);
        Assert.Equal(checkpointAutoApprove, fixture.Engine.ResumedOptionAutoApprove);
    }

    private sealed class ResumeFixture : IAsyncDisposable
    {
        private ResumeFixture(
            TlahDbContext db,
            HttpClient httpClient,
            LlmService service,
            CapturingRunEngine engine,
            Guid runId)
        {
            Db = db;
            HttpClient = httpClient;
            Service = service;
            Engine = engine;
            RunId = runId;
        }

        public TlahDbContext Db { get; }
        public HttpClient HttpClient { get; }
        public LlmService Service { get; }
        public CapturingRunEngine Engine { get; }
        public Guid RunId { get; }

        public static async Task<ResumeFixture> CreateAsync(
            string? invocationStatus,
            bool activePlan = false,
            bool checkpointReferencesInvocation = true,
            string runStatus = AgentRunStatuses.Paused,
            int currentStep = 4,
            int maxSteps = 8,
            string checkpointPermissionMode = AgentPermissionModes.RequestApproval,
            bool checkpointAutoApprove = false)
        {
            var db = TestDb.Create();
            var chatService = new ChatService(db);
            var settingsService = new SettingsService(db);
            await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
                Provider: "openai",
                ApiKey: "sk-resume-budget-test-1234567890",
                BaseUrl: "https://api.example.com",
                Model: "model-a"));
            var chat = await chatService.CreateChatAsync("Resume budget");
            var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
            db.Set<Turn>().Add(turn);
            await db.SaveChangesAsync();

            var userMessage = new Message
            {
                ChatId = chat.Id,
                TurnId = turn.Id,
                Role = "user",
                Content = "Continue the paused run.",
                SequenceNum = 0
            };
            var run = new AgentRun
            {
                ChatId = chat.Id,
                TurnId = turn.Id,
                Status = runStatus,
                UserRequest = userMessage.Content,
                CurrentStep = currentStep,
                MaxSteps = maxSteps
            };
            db.AddRange(userMessage, run);

            ToolInvocation? pendingInvocation = null;
            if (invocationStatus != null)
            {
                var step = new AgentStep
                {
                    AgentRun = run,
                    StepNumber = currentStep,
                    Status = invocationStatus switch
                    {
                        ToolInvocationStatuses.Denied => AgentStepStatuses.Denied,
                        ToolInvocationStatuses.Running or ToolInvocationStatuses.UnknownOutcome =>
                            AgentStepStatuses.Running,
                        _ => AgentStepStatuses.AwaitingApproval
                    }
                };
                pendingInvocation = new ToolInvocation
                {
                    AgentRun = run,
                    AgentStep = step,
                    ToolName = AgentToolNames.SandboxExec,
                    ProviderCallId = "resume-approval-call",
                    ArgumentsJson = "{\"command\":\"Write-Output ok\"}",
                    ProtectedArgumentsJson = ProtectedLocalData.Protect(
                        "{\"command\":\"Write-Output ok\"}"),
                    Status = invocationStatus,
                    RequiresApproval = true,
                    Approved = invocationStatus == ToolInvocationStatuses.Approved,
                    ExplicitUserApproval = invocationStatus == ToolInvocationStatuses.Approved,
                    ApprovedAt = DateTime.UtcNow
                };
                db.Add(pendingInvocation);
            }

            await db.SaveChangesAsync();
            var checkpointStore = new CheckpointStore(db);
            var checkpointState = new AgentRunState
            {
                RunId = run.Id,
                ChatId = chat.Id,
                TurnId = turn.Id,
                Status = runStatus,
                CurrentStep = run.CurrentStep,
                MaxSteps = run.MaxSteps,
                UserRequest = run.UserRequest,
                Messages = [new MessagePayload("user", userMessage.Content)],
                SequenceNum = 1,
                IsPlanMode = activePlan,
                PrePlanMode = activePlan ? AgentPermissionModes.RequestApproval : null,
                EffectivePermissionMode = activePlan
                    ? AgentPermissionModes.Plan
                    : checkpointPermissionMode,
                EffectiveAutoApproveTools = activePlan ? false : checkpointAutoApprove,
                PendingToolInvocationId = checkpointReferencesInvocation
                    ? pendingInvocation?.Id
                    : null
            };
            await checkpointStore.SaveAsync(
                run,
                run.CurrentStep,
                JsonSerializer.Serialize(checkpointState));

            var engine = new CapturingRunEngine();
            var httpClient = new HttpClient(new MapHttpMessageHandler(_ =>
                MapHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
            var service = new LlmService(
                db,
                chatService,
                settingsService,
                new StaticHttpClientFactory(httpClient),
                checkpointStore: checkpointStore,
                agentRunEngineV2: engine);
            return new ResumeFixture(db, httpClient, service, engine, run.Id);
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class CapturingRunEngine : IAgentRunEngineV2
    {
        public int? ResumedMaxSteps { get; private set; }
        public string? ResumedStatePermissionMode { get; private set; }
        public bool? ResumedStateAutoApprove { get; private set; }
        public string? ResumedOptionPermissionMode { get; private set; }
        public bool? ResumedOptionAutoApprove { get; private set; }
        public Guid? ResumedPendingInvocationId { get; private set; }
        public Guid? ResumedUnknownOutcomeInvocationId { get; private set; }
        public bool ResumedRecoveryDirectivePending { get; private set; }
        public IReadOnlyList<MessagePayload> ResumedMessages { get; private set; } = [];

        public Task<AgentRunResult> RunAsync(
            AgentRunState state,
            AgentEngineOptions options,
            IProgress<AgentRunFrame>? frameProgress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AgentRunResult> ResumeAsync(
            AgentRunState state,
            AgentEngineOptions options,
            IProgress<AgentRunFrame>? frameProgress = null,
            CancellationToken ct = default)
        {
            ResumedMaxSteps = state.MaxSteps;
            ResumedStatePermissionMode = state.EffectivePermissionMode;
            ResumedStateAutoApprove = state.EffectiveAutoApproveTools;
            ResumedOptionPermissionMode = options.PermissionMode;
            ResumedOptionAutoApprove = options.AutoApproveTools;
            ResumedPendingInvocationId = state.PendingToolInvocationId;
            ResumedUnknownOutcomeInvocationId = state.UnknownOutcomeInvocationId;
            ResumedRecoveryDirectivePending = state.RecoveryDirectivePending;
            ResumedMessages = state.Messages.Select(message => message with { }).ToArray();
            var finalState = state.DeepClone();
            finalState.Status = AgentRunStatuses.Completed;
            return Task.FromResult(new AgentRunResult(
                finalState,
                "Resumed.",
                LastResponse: null,
                Events: []));
        }
    }

    private sealed class ThrowingProgress : IProgress<AgentProgressUpdate>
    {
        public void Report(AgentProgressUpdate value) =>
            throw new InvalidOperationException("Synthetic progress sink failure.");
    }
}
