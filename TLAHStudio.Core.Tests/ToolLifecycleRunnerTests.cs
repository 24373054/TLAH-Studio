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
        public List<string> PlannedValues { get; } = [];

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
            var level = SafetyBlocked ? ToolSafetyLevels.Blocked : ToolSafetyLevels.Low;
            return Task.FromResult(new ToolSafetyClassification(
                level,
                "test",
                !SafetyBlocked,
                SafetyBlocked,
                false,
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
            var value = ReadValue(argumentsJson);
            PlannedValues.Add(value);
            return Task.FromResult(ToolEffectPlan.Write([], [value], hasRollback: CreatesRollback));
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct) =>
            Task.FromResult(new AgentToolResult(ExecuteSuccess, $"executed:{ReadValue(argumentsJson)}"));

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
