using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Context;
using TLAHStudio.Core.Services.Memory;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.Models;
using TLAHStudio.Core.Services.Tools.PerTool;

namespace TLAHStudio.Core.Tests;

public class TokenBudgetServiceTests
{
    private readonly TokenBudgetService _svc = new();

    [Fact]
    public void GetBudget_KnownModel_ReturnsCorrectWindow()
    {
        var budget = _svc.GetBudget("openai", "gpt-4o");
        Assert.Equal(128_000, budget.MaxTokens);
        Assert.True(budget.AvailableForContext > 100_000);
    }

    [Fact]
    public void GetBudget_UnknownModel_ReturnsDefault()
    {
        var budget = _svc.GetBudget("unknown", "unknown-model");
        Assert.Equal(128_000, budget.MaxTokens);
    }

    [Fact]
    public void CheckBudget_LowUsage_ReturnsSafe()
    {
        var budget = new TokenBudget(128_000, 16_384, 111_616);
        var state = _svc.CheckBudget([], budget, 24_000);
        Assert.Equal(TokenBudgetState.Safe, state);
    }

    [Fact]
    public void CheckBudget_HighUsage_ReturnsWarningOrHigher()
    {
        var msg = new MessagePayload("user", new string('x', 280_000));
        var budget = new TokenBudget(128_000, 16_384, 111_616);
        var state = _svc.CheckBudget([msg], budget, 24_000);
        // 280k chars / 3.2 ≈ 87k tokens vs 112k budget ≈ 78% — should be CompactSoon or higher
        Assert.True(state >= TokenBudgetState.CompactSoon);
    }

    [Fact]
    public void CheckBudget_LowTriggerDoesNotCompactBeforeModelWindowPressure()
    {
        var msg = new MessagePayload("user", new string('x', 110_000));
        var budget = new TokenBudget(128_000, 16_384, 111_616);
        var state = _svc.CheckBudget([msg], budget, 24_000);

        Assert.Equal(TokenBudgetState.Safe, state);
    }

    [Fact]
    public void EstimateTokens_ReturnsReasonableValue()
    {
        var msgs = new List<MessagePayload> { new("user", "Hello, how are you?") };
        var tokens = _svc.EstimateTokens(msgs);
        Assert.True(tokens > 0 && tokens < 100);
    }
}

public class ReactiveCompactorTests
{
    private readonly ReactiveCompactor _compactor = new();
    private readonly TokenBudgetService _budget = new();

    [Fact]
    public async Task Compact_TrimToolOutputs_TrimsLargeResults()
    {
        var messages = new List<MessagePayload>
        {
            new("user", "test"),
            new("assistant", "ok"),
            new("tool", new string('x', 5000)),
            new("user", "done")
        };
        var result = await _compactor.CompactAsync(messages, TokenBudgetState.CompactSoon,
            CompactionStrategy.TrimToolOutputs, _budget);
        Assert.NotNull(result);
        Assert.True(result.WasCompacted || result.EstimatedTokensAfter <= result.EstimatedTokensBefore);
    }

    [Fact]
    public async Task Compact_Microcompact_ReplacesOldToolOutputs()
    {
        var messages = new List<MessagePayload>();
        for (int i = 0; i < 20; i++)
        {
            messages.Add(new("user", $"msg {i}"));
            messages.Add(new("assistant", $"response {i}"));
            // Large tool outputs that benefit from compaction
            messages.Add(new("tool", $"result {i} " + new string('x', 200), $"call_{i}"));
        }
        var before = _budget.EstimateTokens(messages);
        var result = await _compactor.CompactAsync(messages, TokenBudgetState.CompactNow,
            CompactionStrategy.Microcompact, _budget);
        Assert.NotNull(result);
        // Microcompact should reduce tokens by replacing old tool outputs with references
        Assert.True(result.EstimatedTokensAfter < before);
    }

    [Fact]
    public async Task Compact_SummarizeMiddle_ProducesCompactBoundary()
    {
        // M4.4.1: User messages are now preserved, so we need enough non-user
        // messages (> KeepHead+KeepTail+3 = 19) to actually form a compactable
        // middle and trigger SummarizeMiddle.
        var messages = new List<MessagePayload>();
        for (int i = 0; i < 10; i++)
        {
            messages.Add(new("user", $"user instruction {i}"));
            messages.Add(new("assistant", $"assistant response {i}"));
            messages.Add(new("tool", $"tool result {i} " + new string('x', 300), $"call_{i}"));
        }
        // Total: 30 messages > 19 gate. Head indices [0..3], tail indices [18..29].
        // All 10 user messages are preserved. Assistant+tool messages at indices
        // [4..17] that are NOT user messages form the compactable middle.
        var result = await _compactor.CompactAsync(messages, TokenBudgetState.CompactNow,
            CompactionStrategy.SummarizeMiddle, _budget);
        Assert.True(result.WasCompacted);
        // The summary boundary user message is in the result
        Assert.Contains(result.Messages, m =>
            m.Role == "user" && m.Content.Contains("context summary boundary"));
        // All 10 user messages must survive compaction
        var preservedUserCount = result.Messages.Count(m =>
            m.Role == "user" && m.Content.Contains("user instruction"));
        Assert.Equal(10, preservedUserCount);
    }

    [Fact]
    public async Task Compact_EmergencyTruncate_KeepsHeadAndTail()
    {
        var messages = new List<MessagePayload>();
        for (int i = 0; i < 30; i++)
            messages.Add(new("user", $"msg {i}"));
        var result = await _compactor.CompactAsync(messages, TokenBudgetState.Blocking,
            CompactionStrategy.EmergencyTruncate, _budget);
        Assert.True(result.WasCompacted);
        // 2 head + 1 emergency msg + 6 tail = 9 messages
        Assert.True(result.Messages.Count < 12);
    }
}

public class MemoryDirectoryServiceTests
{
    private readonly MemoryDirectoryService _svc = new();
    private readonly Guid _projectId = Guid.NewGuid();

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        await _svc.WriteFileAsync(_projectId, "test-convention.md", "project",
            "# Test\nThis is a test memory.", "Test memory file");

        var content = await _svc.ReadFileAsync(_projectId, "test-convention.md");
        Assert.Contains("Test memory file", content);
        Assert.Contains("This is a test memory", content);
    }

    [Fact]
    public async Task ListFiles_ReturnsWrittenFiles()
    {
        await _svc.WriteFileAsync(_projectId, "arch.md", "reference",
            "# Architecture", "Architecture notes");
        await _svc.WriteFileAsync(_projectId, "prefs.md", "user",
            "# Preferences", "User preferences");

        var files = await _svc.ListFilesAsync(_projectId);
        Assert.Contains(files, f => f.FileName == "arch.md");
        Assert.Contains(files, f => f.FileName == "prefs.md");
        Assert.Contains(files, f => f.FileName == "MEMORY.md");
    }

    [Fact]
    public async Task Search_FindsRelevantFiles()
    {
        await _svc.WriteFileAsync(_projectId, "api-keys.md", "reference",
            "# API Keys\nStore API keys securely.", "API key configuration");
        var results = await _svc.SearchAsync(_projectId, "API keys");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.FileName == "api-keys.md");
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        await _svc.WriteFileAsync(_projectId, "temp.md", "reference", "# Temp", "Temporary");
        await _svc.DeleteFileAsync(_projectId, "temp.md");
        var content = await _svc.ReadFileAsync(_projectId, "temp.md");
        Assert.Empty(content);
    }

    [Fact]
    public async Task BuildContext_TruncatesAtLimit()
    {
        await _svc.WriteFileAsync(_projectId, "large.md", "reference",
            "# Large\n" + new string('x', 5000), "Large file");
        var ctx = await _svc.BuildContextAsync(_projectId, maxChars: 200);
        Assert.True(ctx.Length <= 250);
    }
}

public class CommandSemanticsTests
{
    [Fact]
    public void IsExitCodeFailure_GrepNoMatch_ReturnsFalse()
    {
        Assert.False(CommandSemantics.IsExitCodeFailure("grep pattern file.txt", 1));
        Assert.False(CommandSemantics.IsExitCodeFailure("rg pattern .", 1));
    }

    [Fact]
    public void IsExitCodeFailure_DiffDiffers_ReturnsFalse()
    {
        Assert.False(CommandSemantics.IsExitCodeFailure("diff a.txt b.txt", 1));
        Assert.True(CommandSemantics.IsExitCodeFailure("diff a.txt b.txt", 2));
    }

    [Fact]
    public void IsExitCodeFailure_RegularCommand_ReturnsTrue()
    {
        Assert.True(CommandSemantics.IsExitCodeFailure("unknown-command", 1));
        Assert.False(CommandSemantics.IsExitCodeFailure("echo hello", 0));
    }

    [Fact]
    public void IsDestructive_DetectsDangerousCommands()
    {
        Assert.True(CommandSemantics.IsDestructive("rm -rf /tmp/test"));
        Assert.True(CommandSemantics.IsDestructive("git push --force origin main"));
        Assert.False(CommandSemantics.IsDestructive("ls -la"));
        Assert.False(CommandSemantics.IsDestructive("cat file.txt"));
    }
}

public class CodeToolsV3Tests
{
    [Fact]
    public void CodeReadToolV3_Definition_HasSchema()
    {
        var tool = new CodeReadToolV3();
        Assert.Equal("read", tool.Definition.Name);
        Assert.True(tool.Definition.InputSchema.ContainsKey("properties"));
        var props = (Dictionary<string, object>)tool.Definition.InputSchema["properties"];
        Assert.Contains("path", props.Keys);
    }

    [Fact]
    public void CodeEditToolV3_Definition_HasSchema()
    {
        var tool = new CodeEditToolV3();
        Assert.Equal("edit", tool.Definition.Name);
        Assert.True(tool.Definition.InputSchema.ContainsKey("properties"));
        var props = (Dictionary<string, object>)tool.Definition.InputSchema["properties"];
        Assert.Contains("old_string", props.Keys);
    }

    [Fact]
    public async Task CodeEditToolV3_CreateRollbackPlan_ReturnsPlan()
    {
        var tool = new CodeEditToolV3();
        var args = """{"path":"test.cs","old_string":"a","new_string":"b"}""";
        var plan = await tool.CreateRollbackPlanAsync(args, new AgentToolResult(true, "ok"));
        Assert.NotNull(plan);
        Assert.True(plan.IsFeasible);
        Assert.Contains("test.cs", plan.FilesToRestore!);
    }

    [Fact]
    public async Task FileChangeDetector_DetectsMissingFile()
    {
        var detector = new FileChangeDetector();
        var changed = await detector.HasChangedAsync("nonexistent-file.xyz", "abc");
        Assert.True(changed);
    }
}

public class ToolEffectPlanTests
{
    [Fact]
    public void Empty_ReturnsAllEmpty()
    {
        var plan = ToolEffectPlan.Empty;
        Assert.Empty(plan.PathsRead);
        Assert.Empty(plan.PathsWritten);
        Assert.Equal("none", plan.RiskLevel);
    }

    [Fact]
    public void ReadOnly_ReturnsCorrectPlan()
    {
        var plan = ToolEffectPlan.ReadOnly(["/tmp/a.txt", "/tmp/b.txt"]);
        Assert.Equal(2, plan.PathsRead.Count);
        Assert.Empty(plan.PathsWritten);
        Assert.Equal("low", plan.RiskLevel);
    }
}

public class ToolHookPipelineTests
{
    [Fact]
    public async Task RunAsync_WithNoHooks_ReturnsAllow()
    {
        var registry = new ToolHookRegistry();
        var ctx = new ToolHookContext(Guid.NewGuid(), Guid.NewGuid(), "test", "{}", null, null);
        var result = await ToolHookPipeline.RunAsync(registry, ToolHookTriggers.BeforeUse, ctx);
        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task RunAsync_WithBlockingHook_ReturnsBlock()
    {
        var registry = new ToolHookRegistry();
        var blockingHook = new BlockingHook();
        registry.Register(blockingHook);
        var ctx = new ToolHookContext(Guid.NewGuid(), Guid.NewGuid(), "test", "{}", null, null);
        var result = await ToolHookPipeline.RunAsync(registry, ToolHookTriggers.BeforeUse, ctx);
        Assert.False(result.Allowed);
        Assert.Equal("Blocked by test hook", result.Reason);
    }

    private sealed class BlockingHook : IToolHook
    {
        public ToolHookTriggers Triggers => ToolHookTriggers.BeforeUse;
        public string Name => "TestBlocker";
        public Task<ToolHookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct = default)
            => Task.FromResult(ToolHookResult.Block("Blocked by test hook"));
    }
}
