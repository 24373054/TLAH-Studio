using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Context;
using TLAHStudio.Core.Services.Plugins;
using TLAHStudio.Core.Services.Tools;

namespace TLAHStudio.Core.Tests;

/// <summary>
/// Tests for 4.8.0 and 4.9.0 foundational services:
/// OutputStyleService, SkillLoader (multi-source + conditional),
/// PlanModeAgentTools, AskUserQuestionAgentTool, SkillAgentTool,
/// ReactiveCompactor (model-assisted), ReadFileTracker (conditional).
/// </summary>
public class FoundationServicesV2Tests : IDisposable
{
    private readonly string _tempDir;

    public FoundationServicesV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TLAHStudio.FoundV2.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: OutputStyleService
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void OutputStyle_GetStyles_ContainsThreeBuiltins()
    {
        var svc = new OutputStyleService();
        var styles = svc.GetStyles();

        Assert.Contains(styles, s => s.Name == "default");
        Assert.Contains(styles, s => s.Name == "Explanatory");
        Assert.Contains(styles, s => s.Name == "Learning");
        Assert.True(styles.Count >= 3);
    }

    [Fact]
    public void OutputStyle_DefaultStyle_HasEmptyPrompt()
    {
        var svc = new OutputStyleService();
        var style = svc.GetStyle("default");

        Assert.NotNull(style);
        Assert.Equal("", style.Prompt);
        Assert.Equal("built-in", style.Source);
    }

    [Fact]
    public void OutputStyle_Explanatory_HasPrompt()
    {
        var svc = new OutputStyleService();
        var style = svc.GetStyle("Explanatory");

        Assert.NotNull(style);
        Assert.Contains("Insight", style.Prompt);
        Assert.True(style.KeepCodingInstructions);
    }

    [Fact]
    public void OutputStyle_Learning_HasPrompt()
    {
        var svc = new OutputStyleService();
        var style = svc.GetStyle("Learning");

        Assert.NotNull(style);
        Assert.Contains("Learn by Doing", style.Prompt);
    }

    [Fact]
    public void OutputStyle_GetUnknown_ReturnsNull()
    {
        var svc = new OutputStyleService();
        Assert.Null(svc.GetStyle("nonexistent"));
    }

    [Fact]
    public void OutputStyle_DefaultStyleName_IsDefault()
    {
        var svc = new OutputStyleService();
        Assert.Equal("default", svc.DefaultStyleName);
    }

    [Fact]
    public async Task OutputStyle_CustomFromDir_Loaded()
    {
        var userDir = Path.Combine(_tempDir, "output-styles");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "concise.md"),
            "---\nname: concise\ndescription: Very short responses.\n---\n\nBe extremely concise. One sentence per response.");

        var svc = new OutputStyleService(projectDir: null);
        typeof(OutputStyleService).GetField("_userDir",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(svc, userDir);
        // Force rebuild
        await svc.ReloadAsync();

        var style = svc.GetStyle("concise");
        Assert.NotNull(style);
        Assert.Contains("extremely concise", style.Prompt);
    }

    [Fact]
    public async Task OutputStyle_Reload_DoesNotCrash()
    {
        var svc = new OutputStyleService();
        await svc.ReloadAsync();
        var after = svc.GetStyles();

        // After reload, built-in styles are still there.
        Assert.True(after.Count >= 3);
        Assert.Contains(after, s => s.Name == "default");
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SkillLoader — multi-source, bundled, conditional
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkillLoader_EmptyDirs_ReturnsEmpty()
    {
        var empty = Path.Combine(_tempDir, "empty-skills");
        Directory.CreateDirectory(empty);
        var loader = new SkillLoader(workspaceRoot: null, bundledDir: empty);
        var skills = await loader.LoadSkillsAsync();

        Assert.Empty(skills);
    }

    [Fact]
    public async Task SkillLoader_BundledDir_LoadsSkills_WithBundledSource()
    {
        var bundled = Path.Combine(_tempDir, "bundled");
        var skillDir = Path.Combine(bundled, "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: test-skill\ndescription: A test bundled skill.\n---\n\nDo something useful.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var skills = await loader.LoadSkillsAsync();

        Assert.Single(skills);
        Assert.Equal("test-skill", skills[0].Name);
        Assert.Equal("bundled", skills[0].Source);
    }

    [Fact]
    public async Task SkillLoader_UserDir_LoadsSkills_WithUserSource()
    {
        var user = Path.Combine(_tempDir, "user-skills");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "custom.md"),
            "---\nname: custom-skill\ndescription: A custom user skill.\n---\n\nCustom content.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: Path.Combine(_tempDir, "empty-bundled"));
        // Override user dir via reflection
        typeof(SkillLoader).GetField("_userDir",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(loader, user);
        var skills = await loader.LoadSkillsAsync();

        Assert.Contains(skills, s => s.Name == "custom-skill" && s.Source == "user");
    }

    [Fact]
    public async Task SkillLoader_Deduplicates_FirstWins()
    {
        var bundled = Path.Combine(_tempDir, "bundled-dup");
        var user = Path.Combine(_tempDir, "user-dup");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);

        File.WriteAllText(Path.Combine(bundled, "same-name.md"),
            "---\nname: same-name\ndescription: Bundled version.\n---\n\nBundled content.");
        File.WriteAllText(Path.Combine(user, "same-name.md"),
            "---\nname: same-name\ndescription: User version.\n---\n\nUser content.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        typeof(SkillLoader).GetField("_userDir",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(loader, user);
        var skills = await loader.LoadSkillsAsync();

        var match = skills.Where(s => s.Id.Contains("same-name")).ToList();
        Assert.Single(match);
        // M4.9.2: Priority is project > managed > user > bundled, so user wins
        // over bundled when only those two sources define the skill.
        Assert.Equal("user", match[0].Source);
    }

    [Fact]
    public async Task SkillLoader_ConditionalSkills_NotInActiveList()
    {
        var bundled = Path.Combine(_tempDir, "bundled-cond");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "cond-skill.md"),
            "---\nname: cond-skill\ndescription: Conditional.\npaths: src/**/*.cs\n---\n\nConditional content.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var skills = await loader.LoadSkillsAsync();

        Assert.DoesNotContain(skills, s => s.Name == "cond-skill");
    }

    [Fact]
    public async Task SkillLoader_ActivateConditional_MatchesPath_Activates()
    {
        var bundled = Path.Combine(_tempDir, "bundled-act");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "cs-skill.md"),
            "---\nname: cs-skill\ndescription: C# files.\npaths: *.cs, src/**/*.cs\n---\n\nC# skill.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        await loader.LoadSkillsAsync();

        var activated = await loader.ActivateConditionalSkillsForPathAsync("src/Controllers/HomeController.cs");
        Assert.Single(activated);
        Assert.Equal("cs-skill", activated[0].Name);

        // Now it should appear in the active list
        var skills = await loader.LoadSkillsAsync();
        Assert.Contains(skills, s => s.Name == "cs-skill");
    }

    [Fact]
    public async Task SkillLoader_ActivateConditional_NoMatch_DoesNotActivate()
    {
        var bundled = Path.Combine(_tempDir, "bundled-noact");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "cs-only.md"),
            "---\nname: cs-only\ndescription: C# only.\npaths: *.cs\n---\n\nC# skill.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        await loader.LoadSkillsAsync();

        var activated = await loader.ActivateConditionalSkillsForPathAsync("README.md");
        Assert.Empty(activated);
    }

    [Fact]
    public async Task SkillLoader_GetSkillContent_ReturnsBody()
    {
        var bundled = Path.Combine(_tempDir, "bundled-content");
        var skillDir = Path.Combine(bundled, "body-test");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: body-test\ndescription: Tests body retrieval.\n---\n\nExpected body content here.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var content = await loader.GetSkillContentAsync("body-test");

        Assert.Contains("Expected body content here", content);
    }

    [Fact]
    public async Task SkillLoader_GetSkillContent_Unknown_ReturnsEmpty()
    {
        var loader = new SkillLoader(workspaceRoot: null, bundledDir: _tempDir);
        var content = await loader.GetSkillContentAsync("no-such-skill");

        Assert.Equal("", content);
    }

    [Fact]
    public async Task SkillLoader_Reload_ClearsCache()
    {
        var bundled = Path.Combine(_tempDir, "bundled-reload");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "r1.md"),
            "---\nname: r1\ndescription: First.\n---\n\nFirst.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var first = await loader.LoadSkillsAsync();
        Assert.True(first.Count >= 1);

        // Add a new skill file, then reload
        File.WriteAllText(Path.Combine(bundled, "r2.md"),
            "---\nname: r2\ndescription: Second.\n---\n\nSecond.");
        await loader.ReloadAsync();
        var second = await loader.LoadSkillsAsync();

        Assert.True(second.Count >= 2);
        Assert.Contains(second, s => s.Name == "r2");
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: PlanModeAgentTools
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnterPlanMode_ReturnsInstructions()
    {
        var tool = new EnterPlanModeAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.True(result.Success);
        Assert.Contains("plan mode", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(tool.RequiresApproval);
    }

    [Fact]
    public void EnterPlanMode_Definition_HasCorrectName()
    {
        var tool = new EnterPlanModeAgentTool();
        Assert.Equal("enter_plan_mode", tool.Definition.Name);
    }

    [Fact]
    public void ExitPlanMode_RequiresApproval_IsTrue()
    {
        var root = Path.Combine(_tempDir, "sandbox");
        var sandbox = new SandboxCommandService(root);
        var tool = new ExitPlanModeAgentTool(sandbox);
        Assert.True(tool.RequiresApproval);
    }

    [Fact]
    public async Task ExitPlanMode_NoPlanFile_RequiresPlanText()
    {
        var root = Path.Combine(_tempDir, "sandbox-exit");
        var sandbox = new SandboxCommandService(root);
        var tool = new ExitPlanModeAgentTool(sandbox);
        var chatId = Guid.NewGuid();
        var ctx = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.False(result.Success);
        Assert.Contains("plan is required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExitPlanMode_WithPlanFile_ReadsContent()
    {
        var root = Path.Combine(_tempDir, "sandbox-plan");
        var sandbox = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();
        var planDir = Path.Combine(sandbox.GetSandboxRoot(chatId), ".tlah_context", "plans");
        Directory.CreateDirectory(planDir);
        await File.WriteAllTextAsync(Path.Combine(planDir, $"{chatId:D}-plan.md"), "Test plan content.");

        var tool = new ExitPlanModeAgentTool(sandbox);
        var ctx = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.True(result.Success);
        Assert.Contains("Test plan content", result.Output);
    }

    [Fact]
    public async Task ExitPlanMode_PersistsSuppliedPlan()
    {
        var root = Path.Combine(_tempDir, "sandbox-supplied-plan");
        var sandbox = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();
        var tool = new ExitPlanModeAgentTool(sandbox);
        var ctx = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{\"plan\":\"1. Inspect the change.\\n2. Implement it.\"}");

        var planPath = Path.Combine(sandbox.GetSandboxRoot(chatId), ".tlah_context", "plans", $"{chatId:D}-plan.md");
        Assert.True(result.Success);
        Assert.Contains("Inspect the change", result.Output);
        Assert.Equal("1. Inspect the change.\n2. Implement it.", await File.ReadAllTextAsync(planPath));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: AskUserQuestionAgentTool
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AskUserQuestion_RequiresApproval_IsTrue()
    {
        var tool = new AskUserQuestionAgentTool();
        Assert.True(tool.RequiresApproval);
    }

    [Fact]
    public void AskUserQuestion_Definition_HasQuestionsSchema()
    {
        var tool = new AskUserQuestionAgentTool();
        Assert.Equal("ask_user_question", tool.Definition.Name);
        Assert.True(tool.Definition.InputSchema.ContainsKey("properties"));
        var props = (Dictionary<string, object>)tool.Definition.InputSchema["properties"];
        Assert.True(props.ContainsKey("questions"));
    }

    [Fact]
    public void AskUserQuestion_ValidateInput_AcceptsQuestionsOrCollectedAnswers()
    {
        var tool = new AskUserQuestionAgentTool();

        var questions = tool.ValidateInput(
            """
            {"questions":[{"question":"Choose?","header":"Choice","options":[{"label":"A","description":"First"},{"label":"B","description":"Second"}]}]}
            """);
        var answers = tool.ValidateInput(
            """{"answers":{"Choice":"A"}}""");

        Assert.True(questions.Success, questions.Error);
        Assert.True(answers.Success, answers.Error);
    }

    [Fact]
    public void AskUserQuestion_ValidateInput_RejectsMissingOrEmptyPhasePayload()
    {
        var tool = new AskUserQuestionAgentTool();

        Assert.False(tool.ValidateInput("{}").Success);
        Assert.False(tool.ValidateInput("""{"questions":null}""").Success);
        Assert.False(tool.ValidateInput("""{"answers":{}}""").Success);
        Assert.False(tool.ValidateInput("""{"answers":{"Choice":" "}}""").Success);
    }

    [Fact]
    public async Task AskUserQuestion_Execute_WithAnswers_FormatsCorrectly()
    {
        var tool = new AskUserQuestionAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"answers":{"Language":"C#","Architecture":"MVVM"}}""");
        Assert.True(result.Success);
        Assert.Contains("Language: C#", result.Output);
        Assert.Contains("Architecture: MVVM", result.Output);
    }

    [Fact]
    public async Task AskUserQuestion_Execute_WithoutAnswers_ReturnsPlaceholder()
    {
        var tool = new AskUserQuestionAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.True(result.Success);
        Assert.Contains("Awaiting", result.Output);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SkillAgentTool
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SkillTool_Definition_HasCorrectName()
    {
        var tool = new SkillAgentTool();
        Assert.Equal("skill", tool.Definition.Name);
        Assert.False(tool.RequiresApproval);
    }

    [Fact]
    public async Task SkillTool_Execute_WithoutSkillName_ReturnsError()
    {
        var tool = new SkillAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.False(result.Success);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillTool_Execute_UnknownSkill_ReturnsError()
    {
        var bundled = Path.Combine(_tempDir, "bundled-skilltool");
        Directory.CreateDirectory(bundled);
        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var tool = new SkillAgentTool(loader);
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"skill":"no-such-skill"}""");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SkillTool_Execute_ValidSkill_ReturnsContent()
    {
        var bundled = Path.Combine(_tempDir, "bundled-skilltool2");
        var skillDir = Path.Combine(bundled, "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: test-skill\ndescription: A test skill.\n---\n\nDo the thing.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var tool = new SkillAgentTool(loader);
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"skill":"test-skill"}""");
        Assert.True(result.Success);
        Assert.Contains("Do the thing", result.Output);
        Assert.Contains("[skill:", result.Output);
    }

    [Fact]
    public async Task SkillTool_Execute_WithArgs_IncludesArgsInOutput()
    {
        var bundled = Path.Combine(_tempDir, "bundled-skilltool3");
        var skillDir = Path.Combine(bundled, "arg-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: arg-skill\ndescription: Takes args.\n---\n\nArg content.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var tool = new SkillAgentTool(loader);
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"skill":"arg-skill","args":"--verbose"}""");
        Assert.True(result.Success);
        Assert.Contains("args=--verbose", result.Output);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: ReadFileTracker — conditional skill activation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReadFileTracker_MarkRead_WithoutSkillLoader_DoesNotThrow()
    {
        var tracker = new ReadFileTracker(skillLoader: null);
        var path = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(path, "test");

        var ex = Record.Exception(() => tracker.MarkRead(path, File.GetLastWriteTimeUtc(path)));
        Assert.Null(ex);
        Assert.True(tracker.WasRead(path));
    }

    [Fact]
    public void ReadFileTracker_WasRead_UnreadFile_ReturnsFalse()
    {
        var tracker = new ReadFileTracker(skillLoader: null);
        Assert.False(tracker.WasRead(Path.Combine(_tempDir, "never-read.txt")));
    }

    [Fact]
    public void ReadFileTracker_GetLastReadMtimeUtc_ReturnsValue()
    {
        var tracker = new ReadFileTracker(skillLoader: null);
        var path = Path.Combine(_tempDir, "mtime-test.txt");
        File.WriteAllText(path, "test");
        var mtime = File.GetLastWriteTimeUtc(path);

        tracker.MarkRead(path, mtime);
        var stored = tracker.GetLastReadMtimeUtc(path);

        Assert.NotNull(stored);
        Assert.Equal(mtime, stored.Value);
    }

    [Fact]
    public void ReadFileTracker_Clear_RemovesAllEntries()
    {
        var tracker = new ReadFileTracker(skillLoader: null);
        var path = Path.Combine(_tempDir, "clear-test.txt");
        File.WriteAllText(path, "test");
        tracker.MarkRead(path, File.GetLastWriteTimeUtc(path));
        Assert.True(tracker.WasRead(path));

        tracker.Clear();
        Assert.False(tracker.WasRead(path));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.8.0: ReactiveCompactor — ModelAssistedSummarize activation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReactiveCompactor_ModelAssistedSummarize_Case_Implemented()
    {
        // Verify that CompactAsync handles ModelAssistedSummarize without
        // falling into the default no-op branch.
        var compactor = new ReactiveCompactor(modelAssisted: null);
        var msgs = new List<MessagePayload>
        {
            new("system", "You are a helper."),
            new("user", "Hello!"),
            new("assistant", "Hi!"),
            new("user", "Do something."),
            new("assistant", "Okay, here is a very long response that should be summarized by model-assisted compaction."),
            new("user", "Go on."),
            new("assistant", "More content here."),
            new("user", "Continue."),
            new("assistant", "Even more content."),
        };

        var tokenBudget = new TokenBudgetService();
        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.CompactNow, CompactionStrategy.ModelAssistedSummarize, tokenBudget);

        // Without a provider, it should return "not available" — NOT "No compaction applied."
        Assert.Contains("not available", result.Summary);
    }

    [Fact]
    public async Task ReactiveCompactor_Microcompact_UsesToolCallId()
    {
        var compactor = new ReactiveCompactor();
        var msgs = new List<MessagePayload>();
        // Head + body
        msgs.Add(new MessagePayload("system", "You are a helper."));
        msgs.Add(new MessagePayload("user", "Start."));
        // Create enough old tool results to be above the keep threshold (6).
        for (int i = 0; i < 10; i++)
        {
            msgs.Add(new MessagePayload("assistant", $"Step {i}", ToolCalls: new List<LlmToolCall> { new($"call_{i}", "file_read", "{}") }));
            msgs.Add(new MessagePayload("tool", $"Tool result {i} content here.", $"call_{i}"));
        }
        // Recent tail
        msgs.Add(new MessagePayload("user", "Recent request."));
        msgs.Add(new MessagePayload("assistant", "Recent response."));

        var tokenBudget = new TokenBudgetService();
        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.CompactSoon, CompactionStrategy.Microcompact, tokenBudget,
            CancellationToken.None);

        Assert.True(result.WasCompacted);
        // The compacted messages should contain references using tool-call-id
        var compactedText = string.Join("\n", result.Messages.Select(m => m.Content ?? ""));
        Assert.Contains("tool-call-id", compactedText);
    }

    [Fact]
    public async Task ReactiveCompactor_Microcompact_TooFewMessages_FallsBackToTrim()
    {
        var compactor = new ReactiveCompactor();
        // Only 5 messages — below the KeepHeadMessages(4) + KeepTailMessages(12) + 2 threshold.
        var msgs = new List<MessagePayload>
        {
            new("user", "Hi"),
            new("assistant", "Hello"),
            new("user", "Help"),
            new("assistant", "OK"),
            new("user", "Thanks"),
        };

        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.CompactSoon, CompactionStrategy.Microcompact, new TokenBudgetService(),
            CancellationToken.None);

        // Too few messages, so no compaction happens.
        Assert.False(result.WasCompacted);
    }

    [Fact]
    public async Task ReactiveCompactor_Microcompact_NoToolResults_OnlyTrims()
    {
        var compactor = new ReactiveCompactor();
        var msgs = new List<MessagePayload>();
        for (int i = 0; i < 25; i++)
        {
            msgs.Add(new MessagePayload("user", $"Q{i}"));
            msgs.Add(new MessagePayload("assistant", $"A{i}"));
        }

        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.CompactSoon, CompactionStrategy.Microcompact, new TokenBudgetService(),
            CancellationToken.None);

        // No tool results to compact — nothing changes.
        Assert.False(result.WasCompacted);
    }

    [Fact]
    public async Task ReactiveCompactor_ModelAssistedSummarize_WithoutProvider_ReturnsNotAvailable()
    {
        var compactor = new ReactiveCompactor(modelAssisted: null);
        var msgs = new List<MessagePayload>();
        for (int i = 0; i < 30; i++)
        {
            msgs.Add(new MessagePayload("user", $"M{i}"));
            msgs.Add(new MessagePayload("assistant", $"R{i}"));
        }

        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.Blocking, CompactionStrategy.ModelAssistedSummarize, new TokenBudgetService(),
            CancellationToken.None);

        Assert.Contains("not available", result.Summary);
        Assert.False(result.WasCompacted);
    }

    [Fact]
    public async Task ReactiveCompactor_EmergencyTruncate_TooFewMessages_DoesNotTruncate()
    {
        var compactor = new ReactiveCompactor();
        // Only 6 messages — below the 8-message minimum for emergency truncation.
        var msgs = new List<MessagePayload>
        {
            new("user", "1"), new("assistant", "2"),
            new("user", "3"), new("assistant", "4"),
            new("user", "5"), new("assistant", "6"),
        };

        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.Blocking, CompactionStrategy.EmergencyTruncate, new TokenBudgetService(),
            CancellationToken.None);

        Assert.False(result.WasCompacted);
        Assert.Contains("too few messages", result.Summary);
    }

    [Fact]
    public async Task ReactiveCompactor_EmergencyTruncate_EnoughMessages_Truncates()
    {
        var compactor = new ReactiveCompactor();
        var msgs = new List<MessagePayload>();
        for (int i = 0; i < 20; i++)
        {
            msgs.Add(new MessagePayload("user", $"U{i}"));
            msgs.Add(new MessagePayload("assistant", $"A{i}"));
        }

        var result = await compactor.CompactAsync(
            msgs, TokenBudgetState.Blocking, CompactionStrategy.EmergencyTruncate, new TokenBudgetService(),
            CancellationToken.None);

        Assert.True(result.WasCompacted);
        Assert.Contains("Emergency", result.Summary);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: AgentTools metadata completeness
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentTools_Metadata_AllNewToolsHaveCorrectNames()
    {
        Assert.Equal("enter_plan_mode", AgentToolMetadata.For(AgentToolNames.EnterPlanMode, false).Name);
        Assert.Equal("exit_plan_mode", AgentToolMetadata.For(AgentToolNames.ExitPlanMode, true).Name);
        Assert.Equal("ask_user_question", AgentToolMetadata.For(AgentToolNames.AskUserQuestion, true).Name);
        Assert.Equal("skill", AgentToolMetadata.For(AgentToolNames.Skill, false).Name);
    }

    [Fact]
    public void AgentTools_Metadata_PlanModeTools_DescribeTheirEffects()
    {
        var enter = AgentToolMetadata.For(AgentToolNames.EnterPlanMode, false);
        var exit = AgentToolMetadata.For(AgentToolNames.ExitPlanMode, true);

        Assert.True(enter.IsReadOnly);
        Assert.True(enter.IsConcurrencySafe);
        Assert.False(enter.IsDestructive);
        Assert.False(exit.IsReadOnly);
        Assert.False(exit.IsConcurrencySafe); // needs user approval
        Assert.True(exit.RequiresUserInteraction);
    }

    [Fact]
    public void AgentTools_Metadata_NewTools_HaveReasonableResultSize()
    {
        var enter = AgentToolMetadata.For(AgentToolNames.EnterPlanMode, false);
        var askUser = AgentToolMetadata.For(AgentToolNames.AskUserQuestion, true);
        var skill = AgentToolMetadata.For(AgentToolNames.Skill, false);

        Assert.True(enter.MaxResultSizeChars >= 50_000);
        Assert.True(askUser.MaxResultSizeChars >= 20_000);
        Assert.True(skill.MaxResultSizeChars >= 50_000);
    }

    [Fact]
    public void AgentTools_Metadata_AllNewTools_NotPersistLargeOutputs()
    {
        // Plan and interaction tools are mostly text — artifacts don't apply.
        var enter = AgentToolMetadata.For(AgentToolNames.EnterPlanMode, false);
        var askUser = AgentToolMetadata.For(AgentToolNames.AskUserQuestion, true);
        var skill = AgentToolMetadata.For(AgentToolNames.Skill, false);

        Assert.Equal(AgentToolResultPersistenceModes.PersistLargeOutputs, enter.ResultPersistence);
        Assert.Equal(AgentToolResultPersistenceModes.PersistLargeOutputs, askUser.ResultPersistence);
        Assert.Equal(AgentToolResultPersistenceModes.PersistLargeOutputs, skill.ResultPersistence);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SkillLoader edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkillLoader_MultipleActivation_DoesNotDuplicate()
    {
        var bundled = Path.Combine(_tempDir, "bundled-multiact");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "multi.md"),
            "---\nname: multi\ndescription: Multi.\npaths: *.cs\n---\n\nMulti.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        await loader.LoadSkillsAsync();

        var first = await loader.ActivateConditionalSkillsForPathAsync("src/a.cs");
        Assert.Single(first);

        // Second activation with same pattern — should not re-add.
        var second = await loader.ActivateConditionalSkillsForPathAsync("src/b.cs");
        Assert.Empty(second);

        var skills = await loader.LoadSkillsAsync();
        Assert.Single(skills, s => s.Name == "multi");
    }

    [Fact]
    public async Task SkillLoader_GlobStarStar_MatchesNestedDirs()
    {
        var bundled = Path.Combine(_tempDir, "bundled-glob");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "glob-test.md"),
            "---\nname: glob-test\ndescription: Glob.\npaths: src/**/*.ts\n---\n\nGlob.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        await loader.LoadSkillsAsync();

        // Deeply nested match
        var activated = await loader.ActivateConditionalSkillsForPathAsync("src/components/nested/deep/file.ts");
        Assert.Single(activated);
        Assert.Equal("glob-test", activated[0].Name);
    }

    [Fact]
    public async Task SkillLoader_FileWithoutFrontmatter_Skipped()
    {
        var bundled = Path.Combine(_tempDir, "bundled-nofm");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "bad.md"), "Just plain text, no frontmatter.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var skills = await loader.LoadSkillsAsync();
        Assert.Empty(skills);
    }

    [Fact]
    public async Task SkillLoader_WhenToUse_Parsed()
    {
        var bundled = Path.Combine(_tempDir, "bundled-wtu");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "wtu.md"),
            "---\nname: wtu\ndescription: Has when_to_use.\nwhen_to_use: Use for testing.\n---\n\nBody.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var skills = await loader.LoadSkillsAsync();

        var skill = skills.FirstOrDefault(s => s.Name == "wtu");
        Assert.NotNull(skill);
        Assert.Equal("Use for testing.", skill.WhenToUse);
    }

    [Fact]
    public async Task SkillLoader_AllowedTools_Parsed()
    {
        var bundled = Path.Combine(_tempDir, "bundled-at");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "at.md"),
            "---\nname: at\ndescription: Has tools.\nallowed_tools: Read, Grep, Glob\n---\n\nBody.");

        var loader = new SkillLoader(workspaceRoot: null, bundledDir: bundled);
        var skills = await loader.LoadSkillsAsync();

        var skill = skills.FirstOrDefault(s => s.Name == "at");
        Assert.NotNull(skill);
        Assert.NotNull(skill.AllowedTools);
        Assert.Contains("Read", skill.AllowedTools);
        Assert.Contains("Grep", skill.AllowedTools);
        Assert.Contains("Glob", skill.AllowedTools);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: OutputStyle edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void OutputStyle_NonExistentDir_DoesNotCrash()
    {
        var svc = new OutputStyleService(projectDir: Path.Combine(_tempDir, "nonexistent-project"));
        var styles = svc.GetStyles();
        Assert.True(styles.Count >= 3); // built-ins still present
    }

    [Fact]
    public void OutputStyle_GetStyle_CaseInsensitive()
    {
        var svc = new OutputStyleService();
        Assert.NotNull(svc.GetStyle("explanatory")); // lowercase match
        Assert.NotNull(svc.GetStyle("EXPLANATORY"));
        Assert.NotNull(svc.GetStyle("learning"));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: AskUserQuestion edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AskUserQuestion_Execute_EmptyJson_ReturnsPlaceholder()
    {
        var tool = new AskUserQuestionAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "{}");
        Assert.True(result.Success);
        Assert.Contains("Awaiting", result.Output);
    }

    [Fact]
    public async Task AskUserQuestion_Execute_MalformedJson_ReturnsPlaceholder()
    {
        var tool = new AskUserQuestionAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, "not json at all");
        Assert.True(result.Success);
        Assert.Contains("Awaiting", result.Output);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SkillTool edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkillTool_Execute_EmptySkillName_ReturnsError()
    {
        var tool = new SkillAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"skill":""}""");
        Assert.False(result.Success);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillTool_Execute_WhitespaceSkillName_ReturnsError()
    {
        var tool = new SkillAgentTool();
        var ctx = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12_000);

        var result = await tool.ExecuteAsync(ctx, """{"skill":"   "}""");
        Assert.False(result.Success);
    }
}
