using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Tests;

public class ToolLifecycleRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_V3Tool_RunsLifecycleInOrder()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order) { CreatesRollback = true };
        var hooks = Hooks(
            new RecordingHook(ToolHookTriggers.BeforeUse, "before", order),
            new RecordingHook(ToolHookTriggers.AfterUse, "after", order));
        var runner = Runner(tool, hooks);

        var outcome = await runner.ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.True(outcome.Result.Success);
        Assert.Equal(
            ["ClassifySafetyAsync", "PlanEffectsAsync", "BeforeUse", "ExecuteWithProgressAsync", "AfterUse", "CreateRollbackPlanAsync"],
            order);
        Assert.NotNull(outcome.EffectPlan);
        Assert.NotNull(outcome.RollbackPlan);
    }

    [Fact]
    public async Task ExecuteAsync_BeforeHookBlocks_DoesNotExecuteTool()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order);
        var hooks = Hooks(new RecordingHook(
            ToolHookTriggers.BeforeUse,
            "before",
            order,
            blockReason: "blocked by policy"));
        var runner = Runner(tool, hooks);

        var outcome = await runner.ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.False(outcome.Result.Success);
        Assert.Contains("blocked by policy", outcome.Result.Error);
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
        Assert.Contains(outcome.ProgressEvents, p => p.Phase == "hook_blocked");
    }

    [Fact]
    public async Task ExecuteAsync_BeforeHookModifiesArguments_RevalidatesAndReplans()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order);
        var hooks = Hooks(new RecordingHook(
            ToolHookTriggers.BeforeUse,
            "before",
            order,
            modifiedArgumentsJson: """{"value":"modified"}"""));
        var runner = Runner(tool, hooks);
        var request = Request("""{"value":"original"}""");

        var outcome = await runner.ExecuteAsync(request);

        Assert.True(outcome.Result.Success);
        Assert.True(JsonEquivalent("""{"value":"modified"}""", request.Invocation.ArgumentsJson));
        Assert.Equal(["original", "modified"], tool.PlannedValues);
        Assert.Equal("executed:modified", outcome.Result.Output);
        Assert.Equal(["modified"], outcome.EffectPlan!.PathsWritten);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedInvocationRejectsHookArgumentMutation()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order);
        var hooks = Hooks(new RecordingHook(
            ToolHookTriggers.BeforeUse,
            "before",
            order,
            modifiedArgumentsJson: """{"value":"changed-after-approval"}"""));
        var runner = Runner(tool, hooks);
        var request = Request("""{"value":"approved"}""") with
        {
            ExplicitUserApproval = true
        };

        var outcome = await runner.ExecuteAsync(request);

        Assert.False(outcome.Result.Success);
        Assert.Contains("changed", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
    }

    [Fact]
    public async Task ExecuteAsync_HookMutationToHighRiskRequiresFreshApproval()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order) { HighWhenValue = "high-risk" };
        var hooks = Hooks(new RecordingHook(
            ToolHookTriggers.BeforeUse,
            "before",
            order,
            modifiedArgumentsJson: """{"value":"high-risk"}"""));

        var outcome = await Runner(tool, hooks).ExecuteAsync(
            Request("""{"value":"safe"}"""));

        Assert.False(outcome.Result.Success);
        Assert.Contains("fresh approval", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutionArguments_UsesUnredactedRuntimeValue()
    {
        var tool = new RecordingV3Tool([]);
        var runner = Runner(tool, Hooks());
        var request = Request("""{"value":"[REDACTED]"}""") with
        {
            ExecutionArgumentsJson = """{"value":"runtime-secret"}"""
        };

        var outcome = await runner.ExecuteAsync(request);

        Assert.True(outcome.Result.Success);
        Assert.Equal("executed:runtime-secret", outcome.Result.Output);
        Assert.Equal(["runtime-secret"], tool.PlannedValues);
        Assert.True(JsonEquivalent("""{"value":"[REDACTED]"}""", request.Invocation.ArgumentsJson));
    }

    [Fact]
    public async Task ExecuteAsync_StoredPolicyAuthorizationReachesInnerToolWithoutForgingExactApproval()
    {
        var tool = new RecordingV3Tool([]);
        var request = Request("""{"value":"policy-authorized"}""") with
        {
            PermissionMode = AgentPermissionModes.RequestApproval,
            PolicyAuthorization = true,
            ExplicitUserApproval = false
        };

        var outcome = await Runner(tool, Hooks()).ExecuteAsync(request);

        Assert.True(outcome.Result.Success);
        Assert.NotNull(tool.LastContext);
        Assert.True(tool.LastContext!.HasPolicyAuthorization);
        Assert.False(tool.LastContext.HasInvocationAuthorization);
        Assert.Equal(AgentPermissionModes.BypassPermissions, tool.LastContext.EffectivePermissionMode);
    }

    [Fact]
    public async Task ExecuteAsync_HookArgumentChangeDoesNotInheritStoredPolicyAuthorization()
    {
        var tool = new RecordingV3Tool([]);
        var hooks = Hooks(new RecordingHook(
            ToolHookTriggers.BeforeUse,
            "before",
            [],
            modifiedArgumentsJson: """{"value":"changed"}"""));
        var request = Request("""{"value":"original"}""") with
        {
            PermissionMode = AgentPermissionModes.RequestApproval,
            PolicyAuthorization = true
        };

        var outcome = await Runner(tool, hooks).ExecuteAsync(request);

        Assert.True(outcome.Result.Success);
        Assert.NotNull(tool.LastContext);
        Assert.False(tool.LastContext!.HasPolicyAuthorization);
        Assert.Equal(AgentPermissionModes.RequestApproval, tool.LastContext.EffectivePermissionMode);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyTool_UsesCompatibilityPath()
    {
        var legacy = new LegacyRecordingTool();
        var runner = Runner(legacy, Hooks());

        var outcome = await runner.ExecuteAsync(Request("""{"value":"legacy"}""", legacy.Definition.Name));

        Assert.True(outcome.Result.Success);
        Assert.Equal("legacy:legacy", outcome.Result.Output);
        Assert.NotNull(outcome.EffectPlan);
        Assert.Empty(outcome.ProgressEvents);
    }

    [Fact]
    public async Task ExecuteAsync_SafetyBlocked_DoesNotExecuteOrRunAfterHook()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order) { SafetyBlocked = true };
        var hooks = Hooks(new RecordingHook(ToolHookTriggers.AfterFailedUse, "after_failed", order));
        var runner = Runner(tool, hooks);

        var outcome = await runner.ExecuteAsync(Request("""{"value":"blocked"}"""));

        Assert.False(outcome.Result.Success);
        Assert.True(outcome.Safety.IsBlocked);
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
        Assert.DoesNotContain("AfterFailedUse", order);
    }

    [Fact]
    public async Task ExecuteAsync_SafetyBlocked_InBypassMode_DoesNotExecute()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order) { SafetyBlocked = true };
        var runner = Runner(tool, Hooks());

        var outcome = await runner.ExecuteAsync(Request("""{"value":"blocked"}""") with
        {
            PermissionMode = AgentPermissionModes.BypassPermissions
        });

        Assert.False(outcome.Result.Success);
        Assert.True(outcome.Safety.IsBlocked);
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
    }

    [Fact]
    public async Task ExecuteAsync_CollectsProgressEvents()
    {
        var runner = Runner(new RecordingV3Tool([]), Hooks());

        var outcome = await runner.ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.Contains(outcome.ProgressEvents, p => p.Phase == "running" && p.Percent == 50);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackPlanOnlyGeneratedAfterSuccess()
    {
        var successTool = new RecordingV3Tool([]) { CreatesRollback = true };
        var success = await Runner(successTool, Hooks()).ExecuteAsync(Request("""{"value":"alpha"}"""));
        Assert.NotNull(success.RollbackPlan);

        var failingTool = new RecordingV3Tool([]) { CreatesRollback = true, ExecuteSuccess = false };
        var failed = await Runner(failingTool, Hooks()).ExecuteAsync(Request("""{"value":"alpha"}"""));
        Assert.Null(failed.RollbackPlan);
    }

    [Fact]
    public async Task ExecuteAsync_ToolExceptionBecomesRecoverableFailureResult()
    {
        var tool = new RecordingV3Tool([])
        {
            ThrowOnExecute = true,
            IsReadOnlyClassification = false
        };

        var outcome = await Runner(tool, Hooks()).ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.False(outcome.Result.Success);
        Assert.Contains("Tool execution failed", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic tool failure", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(outcome.Result.OutcomeUncertain);
        Assert.True(outcome.Result.MayHaveCommitted);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyToolExceptionRemainsOrdinaryFailure()
    {
        var tool = new RecordingV3Tool([])
        {
            ThrowOnExecute = true,
            IsReadOnlyClassification = true
        };

        var outcome = await Runner(tool, Hooks()).ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.False(outcome.Result.Success);
        Assert.False(outcome.Result.OutcomeUncertain);
        Assert.False(outcome.Result.MayHaveCommitted);
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("effect plan")]
    public async Task PreviewAsync_PreviewStageExceptionBecomesObservableFailure(string stage)
    {
        var tool = new RecordingV3Tool([])
        {
            ThrowOnClassify = stage == "classification",
            ThrowOnPlan = stage == "effect plan"
        };
        var runner = Runner(tool, Hooks());

        var preview = await runner.PreviewAsync(
            Guid.NewGuid(),
            tool.Definition.Name,
            """{"value":"alpha"}""");

        Assert.NotNull(preview.ValidationFailure);
        Assert.False(preview.ValidationFailure!.Success);
        Assert.Contains("Tool preview failed", preview.ValidationFailure.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(stage, preview.ValidationFailure.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(preview.Safety.IsBlocked);
        Assert.Equal("lifecycle", preview.Safety.Category);
    }

    [Fact]
    public async Task ExecuteAsync_BeforeHookExceptionBecomesObservableFailure()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order);
        var runner = Runner(
            tool,
            Hooks(new ThrowingHook(
                ToolHookTriggers.BeforeUse,
                "synthetic before hook failure")));

        var outcome = await runner.ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.False(outcome.Result.Success);
        Assert.Contains("Before-use hook failed", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic before hook failure", outcome.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.ProgressEvents, p => p.Phase == "hook_failed");
        Assert.DoesNotContain("ExecuteWithProgressAsync", order);
    }

    [Fact]
    public async Task ExecuteAsync_AfterHookExceptionBecomesObservableFailure()
    {
        var order = new List<string>();
        var tool = new RecordingV3Tool(order);
        var runner = Runner(
            tool,
            Hooks(new ThrowingHook(
                ToolHookTriggers.AfterUse,
                "synthetic after hook failure")));

        var outcome = await runner.ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.True(outcome.Result.Success);
        Assert.Contains("executed:alpha", outcome.Result.Output);
        Assert.Contains("After-use hook failed", outcome.Result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic after hook failure", outcome.Result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.ProgressEvents, p => p.Phase == "hook_failed");
        Assert.Contains("ExecuteWithProgressAsync", order);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackPlanExceptionBecomesObservableFailure()
    {
        var tool = new RecordingV3Tool([])
        {
            CreatesRollback = true,
            ThrowOnRollback = true
        };

        var outcome = await Runner(tool, Hooks()).ExecuteAsync(Request("""{"value":"alpha"}"""));

        Assert.True(outcome.Result.Success);
        Assert.Contains("executed:alpha", outcome.Result.Output);
        Assert.Contains("rollback planning failed", outcome.Result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic rollback failure", outcome.Result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.RollbackPlan);
        Assert.Contains(outcome.ProgressEvents, p => p.Phase == "rollback_failed");
    }

    [Fact]
    public async Task ExecuteAsync_UserCancellationFromHookStillPropagates()
    {
        var tool = new RecordingV3Tool([]);
        var runner = Runner(
            tool,
            Hooks(new ThrowingHook(
                ToolHookTriggers.BeforeUse,
                "cancelled",
                throwCancellation: true)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ExecuteAsync(Request("""{"value":"alpha"}"""), cts.Token));
    }

    private static IToolLifecycleRunner Runner(IAgentTool tool, IToolHookRegistry hooks)
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Lifecycle.Tests", Guid.NewGuid().ToString("N")));
        return new DefaultToolLifecycleRunner(new AgentToolRegistry([tool]), sandbox, hooks);
    }

    private static ToolHookRegistry Hooks(params IToolHook[] hooks)
    {
        var registry = new ToolHookRegistry();
        foreach (var hook in hooks)
            registry.Register(hook);
        return registry;
    }

    private static ToolExecutionRequest Request(string argumentsJson, string toolName = "fake_v3")
    {
        var chatId = Guid.NewGuid();
        return new ToolExecutionRequest(
            new AgentRun { Id = Guid.NewGuid(), ChatId = chatId },
            new ToolInvocation
            {
                Id = Guid.NewGuid(),
                AgentRunId = Guid.NewGuid(),
                AgentStepId = Guid.NewGuid(),
                ToolName = toolName,
                ArgumentsJson = argumentsJson
            },
            TimeoutSeconds: 5,
            MaxOutputChars: 12000);
    }

    private sealed class RecordingV3Tool(List<string> order) : AgentToolV3Base
    {
        public bool SafetyBlocked { get; init; }
        public bool ExecuteSuccess { get; init; } = true;
        public bool CreatesRollback { get; init; }
        public bool ThrowOnExecute { get; init; }
        public bool ThrowOnClassify { get; init; }
        public bool ThrowOnPlan { get; init; }
        public bool ThrowOnRollback { get; init; }
        public bool IsReadOnlyClassification { get; init; } = true;
        public string? HighWhenValue { get; init; }
        public List<string> PlannedValues { get; } = [];
        public AgentToolExecutionContext? LastContext { get; private set; }

        public override LlmToolDefinition Definition { get; } = new(
            "fake_v3",
            "Fake V3 tool.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["value"] = new Dictionary<string, object> { ["type"] = "string" }
                }
            });

        public override bool RequiresApproval => false;

        public override Task<ToolSafetyClassification> ClassifySafetyAsync(
            string argumentsJson,
            Guid chatId,
            ISandboxCommandService sandbox,
            CancellationToken ct = default)
        {
            order.Add("ClassifySafetyAsync");
            if (ThrowOnClassify)
                throw new InvalidOperationException("synthetic classification failure");
            var highRisk = string.Equals(
                ReadValue(argumentsJson),
                HighWhenValue,
                StringComparison.Ordinal);
            var level = SafetyBlocked
                ? ToolSafetyLevels.Blocked
                : highRisk ? ToolSafetyLevels.High : ToolSafetyLevels.Low;
            return Task.FromResult(new ToolSafetyClassification(
                level,
                "test",
                IsReadOnlyClassification && !SafetyBlocked && !highRisk,
                SafetyBlocked || highRisk,
                highRisk,
                SafetyBlocked,
                SafetyBlocked ? "blocked" : "ok",
                SafetyBlocked ? "blocked" : null,
                null));
        }

        public override Task<ToolEffectPlan> PlanEffectsAsync(
            string argumentsJson,
            Guid chatId,
            ISandboxCommandService sandbox,
            CancellationToken ct = default)
        {
            order.Add("PlanEffectsAsync");
            if (ThrowOnPlan)
                throw new InvalidOperationException("synthetic effect plan failure");
            var value = ReadValue(argumentsJson);
            PlannedValues.Add(value);
            return Task.FromResult(ToolEffectPlan.Write([], [value], hasRollback: CreatesRollback));
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct)
        {
            LastContext = context;
            if (ThrowOnExecute)
                throw new InvalidOperationException("synthetic tool failure");
            return Task.FromResult(new AgentToolResult(ExecuteSuccess, $"executed:{ReadValue(argumentsJson)}"));
        }

        public override Task<AgentToolResult> ExecuteWithProgressAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            IProgress<AgentToolProgress>? progress = null,
            CancellationToken ct = default)
        {
            order.Add("ExecuteWithProgressAsync");
            progress?.Report(new AgentToolProgress("running", 50, "Halfway"));
            return ExecuteAsync(context, argumentsJson, ct);
        }

        public override Task<ToolRollbackPlan?> CreateRollbackPlanAsync(
            string argumentsJson,
            AgentToolResult result,
            CancellationToken ct = default)
        {
            order.Add("CreateRollbackPlanAsync");
            if (ThrowOnRollback)
                throw new InvalidOperationException("synthetic rollback failure");
            return Task.FromResult<ToolRollbackPlan?>(
                CreatesRollback
                    ? new ToolRollbackPlan(true, "restore fake file", null, [ReadValue(argumentsJson)])
                    : null);
        }
    }

    private sealed class LegacyRecordingTool : IAgentTool
    {
        public LlmToolDefinition Definition { get; } = new(
            "legacy_fake",
            "Legacy fake tool.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["value"] = new Dictionary<string, object> { ["type"] = "string" }
                }
            });

        public bool RequiresApproval => false;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(true, $"legacy:{ReadValue(argumentsJson)}"));
    }

    private sealed class RecordingHook(
        ToolHookTriggers trigger,
        string name,
        List<string> order,
        string? blockReason = null,
        string? modifiedArgumentsJson = null) : IToolHook
    {
        public ToolHookTriggers Triggers => trigger;
        public string Name => name;

        public Task<ToolHookResult> ExecuteAsync(
            ToolHookContext context,
            CancellationToken ct = default)
        {
            order.Add(trigger.ToString());
            return Task.FromResult(blockReason == null
                ? new ToolHookResult(true, ModifiedArgumentsJson: modifiedArgumentsJson)
                : ToolHookResult.Block(blockReason));
        }
    }

    private sealed class ThrowingHook(
        ToolHookTriggers trigger,
        string message,
        bool throwCancellation = false) : IToolHook
    {
        public ToolHookTriggers Triggers => trigger;
        public string Name => "throwing";

        public Task<ToolHookResult> ExecuteAsync(
            ToolHookContext context,
            CancellationToken ct = default)
        {
            if (throwCancellation)
                ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private static string ReadValue(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        return doc.RootElement.TryGetProperty("value", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool JsonEquivalent(string expected, string actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        using var actualDoc = JsonDocument.Parse(actual);
        return JsonSerializer.Serialize(expectedDoc.RootElement) ==
               JsonSerializer.Serialize(actualDoc.RootElement);
    }
}
