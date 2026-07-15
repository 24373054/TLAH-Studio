using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;

namespace TLAHStudio.Core.Tests;

/// <summary>
/// 4.8.0 + 4.9.0: Tests for new AgentRunState fields.
/// Plan mode (IsPlanMode, PrePlanMode), CompactionDisabled, SentSkillNames.
/// </summary>
public class AgentRunStateV2Tests
{
    [Theory]
    [InlineData("{\"answers\":{\"Recovery\":\"Stop and summarize\"}}", "Stop and summarize")]
    [InlineData("{\"answers\":{\"The agent could not recover. How should it continue?\":\"Try another way\"}}", "Try another way")]
    [InlineData("{\"answers\":{\"Recovery\":[\"Try another way\"]}}", "Try another way")]
    public void SyntheticRecoveryAnswer_ParsesHeaderAndLegacyQuestionKeys(
        string argumentsJson,
        string expected)
    {
        var call = new LlmToolCall(
            "recovery-test",
            AgentToolNames.AskUserQuestion,
            argumentsJson);

        Assert.Equal(
            expected,
            AgentRunEngineV2.ReadSyntheticQuestionAnswer(call, "Recovery"));
    }

    [Theory]
    [InlineData("Try another way", false)]
    [InlineData("Stop and summarize", true)]
    public void SyntheticRecoveryChoice_OnlyStopResolvesFailure(
        string choice,
        bool expectedResolution)
    {
        var state = new AgentRunState
        {
            ConsecutiveToolFailures = 1,
            LastFailureSummary = "A tool failed.",
            RecoveryDirectivePending = true
        };
        var call = new LlmToolCall(
            "recovery-test",
            AgentToolNames.AskUserQuestion,
            $"{{\"answers\":{{\"Recovery\":{System.Text.Json.JsonSerializer.Serialize(choice)}}}}}");

        Assert.Equal(
            expectedResolution,
            AgentRunEngineV2.ApplySyntheticQuestionResolution(state, call));
        Assert.Equal(1, state.ConsecutiveToolFailures);
    }

    [Theory]
    [InlineData("Reassess and run", 1, true)]
    [InlineData("Skip deferred work", 0, false)]
    public void SyntheticDeferredChoice_OnlySkipClearsDurableWork(
        string choice,
        int expectedCount,
        bool expectedDirective)
    {
        var state = new AgentRunState
        {
            DeferredToolCalls =
            [
                new LlmToolCall(
                    "deferred-one",
                    AgentToolNames.FileRead,
                    "{\"path\":\"README.md\"}")
            ],
            DeferredToolDirectivePending = true,
            DeferredToolRecoveryAttempts = 2
        };
        var call = new LlmToolCall(
            "recovery-deferred-test",
            AgentToolNames.AskUserQuestion,
            $"{{\"answers\":{{\"Deferred work\":{System.Text.Json.JsonSerializer.Serialize(choice)}}}}}");

        Assert.False(AgentRunEngineV2.ApplySyntheticQuestionResolution(state, call));
        Assert.Equal(expectedCount, state.DeferredToolCalls.Count);
        Assert.Equal(expectedDirective, state.DeferredToolDirectivePending);
        Assert.Equal(expectedCount == 0 ? 0 : 2, state.DeferredToolRecoveryAttempts);
    }

    [Fact]
    public void OrdinaryQuestion_DoesNotClaimToResolveAnUnrelatedFailure()
    {
        var state = new AgentRunState
        {
            ConsecutiveToolFailures = 2,
            RecoveryDirectivePending = true
        };
        var call = new LlmToolCall(
            "question-user-input",
            AgentToolNames.AskUserQuestion,
            "{\"answers\":{\"Choice\":\"Continue\"}}");

        Assert.Null(AgentRunEngineV2.ApplySyntheticQuestionResolution(state, call));
        Assert.Equal(2, state.ConsecutiveToolFailures);
        Assert.True(state.RecoveryDirectivePending);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.8.0: CompactionDisabled
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentRunState_CompactionDisabled_DefaultsToFalse()
    {
        var state = new AgentRunState();
        Assert.False(state.CompactionDisabled);
    }

    [Fact]
    public void AgentRunState_CompactionDisabled_CanBeSet()
    {
        var state = new AgentRunState { CompactionDisabled = true };
        Assert.True(state.CompactionDisabled);
    }

    [Fact]
    public void AgentRunState_DeepClone_PreservesCompactionDisabled()
    {
        var state = new AgentRunState { CompactionDisabled = true };
        var clone = state.DeepClone();
        Assert.True(clone.CompactionDisabled);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Plan mode state fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentRunState_IsPlanMode_DefaultsToFalse()
    {
        var state = new AgentRunState();
        Assert.False(state.IsPlanMode);
    }

    [Fact]
    public void AgentRunState_PrePlanMode_DefaultsToNull()
    {
        var state = new AgentRunState();
        Assert.Null(state.PrePlanMode);
    }

    [Fact]
    public void AgentRunState_PlanMode_CanBeSet()
    {
        var state = new AgentRunState
        {
            IsPlanMode = true,
            PrePlanMode = AgentPermissionModes.AutoApprove
        };
        Assert.True(state.IsPlanMode);
        Assert.Equal(AgentPermissionModes.AutoApprove, state.PrePlanMode);
    }

    [Fact]
    public void AgentRunState_DeepClone_PreservesPlanMode()
    {
        var state = new AgentRunState
        {
            IsPlanMode = true,
            PrePlanMode = AgentPermissionModes.BypassPermissions,
            Messages = [new("user", "test")]
        };
        var clone = state.DeepClone();

        Assert.True(clone.IsPlanMode);
        Assert.Equal(AgentPermissionModes.BypassPermissions, clone.PrePlanMode);
        // Mutation safety
        clone.IsPlanMode = false;
        clone.PrePlanMode = null;
        Assert.True(state.IsPlanMode);
        Assert.NotNull(state.PrePlanMode);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SentSkillNames
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentRunState_SentSkillNames_DefaultsToEmpty()
    {
        var state = new AgentRunState();
        Assert.NotNull(state.SentSkillNames);
        Assert.Empty(state.SentSkillNames);
    }

    [Fact]
    public void AgentRunState_SentSkillNames_CanAddAndCheck()
    {
        var state = new AgentRunState();
        Assert.True(state.SentSkillNames.Add("test-skill"));
        Assert.Contains("test-skill", state.SentSkillNames);
        Assert.False(state.SentSkillNames.Add("test-skill")); // Already present
        Assert.Single(state.SentSkillNames);
    }

    [Fact]
    public void AgentRunState_SentSkillNames_CaseInsensitive()
    {
        var state = new AgentRunState();
        Assert.True(state.SentSkillNames.Add("MySkill"));
        Assert.False(state.SentSkillNames.Add("myskill")); // Same, case-insensitive
        Assert.Contains("MYSKILL", state.SentSkillNames);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: AgentEngineOptions default
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentEngineOptions_PermissionMode_DefaultsToRequestApproval()
    {
        var options = new AgentEngineOptions();
        Assert.Equal(AgentPermissionModes.RequestApproval, options.PermissionMode);
    }

    [Fact]
    public void AgentEngineOptions_PermissionMode_CanBePlan()
    {
        var options = new AgentEngineOptions(PermissionMode: AgentPermissionModes.Plan);
        Assert.Equal(AgentPermissionModes.Plan, options.PermissionMode);
    }

    [Fact]
    public void AgentRunOptions_PermissionMode_DefaultsToRequestApproval()
    {
        var options = new AgentRunOptions();
        Assert.Equal(AgentPermissionModes.RequestApproval, options.PermissionMode);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.8.0: AgentRunState defaults completeness
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentRunState_NewFields_InitialDefaults()
    {
        var state = new AgentRunState();
        Assert.False(state.CompactionDisabled);
        Assert.False(state.IsPlanMode);
        Assert.Null(state.PrePlanMode);
        Assert.Null(state.EffectivePermissionMode);
        Assert.Null(state.EffectiveAutoApproveTools);
        Assert.Null(state.PrePlanAutoApproveTools);
        Assert.NotNull(state.SentSkillNames);
        Assert.Empty(state.SentSkillNames);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Plan mode state transition simulation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PlanMode_EnterThenExit_RestoresPreviousMode()
    {
        var state = new AgentRunState();
        var originalMode = AgentPermissionModes.BypassPermissions;

        // Enter plan mode
        state.IsPlanMode = true;
        state.PrePlanMode = originalMode;
        Assert.True(state.IsPlanMode);
        Assert.Equal(originalMode, state.PrePlanMode);

        // Exit plan mode
        var restored = state.PrePlanMode ?? AgentPermissionModes.RequestApproval;
        if (restored == AgentPermissionModes.Plan)
            restored = AgentPermissionModes.RequestApproval;
        state.IsPlanMode = false;

        Assert.False(state.IsPlanMode);
        Assert.Equal(AgentPermissionModes.BypassPermissions, restored);
        Assert.Equal(originalMode, restored);
    }

    [Fact]
    public void PlanMode_ExitWithoutPrePlan_FallsBackToRequestApproval()
    {
        var state = new AgentRunState { IsPlanMode = true, PrePlanMode = null };

        var restored = state.PrePlanMode ?? AgentPermissionModes.RequestApproval;
        state.IsPlanMode = false;

        Assert.Equal(AgentPermissionModes.RequestApproval, restored);
        Assert.False(state.IsPlanMode);
    }

    [Fact]
    public void PlanMode_ExitWithPlanAsPrePlan_CircuitBreakerFallsBack()
    {
        // Should never happen, but circuit breaker guard.
        var state = new AgentRunState { IsPlanMode = true, PrePlanMode = AgentPermissionModes.Plan };

        var restored = state.PrePlanMode ?? AgentPermissionModes.RequestApproval;
        if (restored == AgentPermissionModes.Plan)
            restored = AgentPermissionModes.RequestApproval;

        Assert.Equal(AgentPermissionModes.RequestApproval, restored);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: SentSkillNames integration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SentSkillNames_IncrementalSend_AcrossResume()
    {
        var original = new AgentRunState();
        original.SentSkillNames.Add("init");
        original.SentSkillNames.Add("code-review");
        Assert.Equal(2, original.SentSkillNames.Count);

        // Simulate a resume: DeepClone preserves SentSkillNames.
        var resumed = original.DeepClone();
        Assert.Equal(2, resumed.SentSkillNames.Count());
        Assert.Contains("init", resumed.SentSkillNames);
        Assert.Contains("code-review", resumed.SentSkillNames);

        // New skills that show up after resume should still dedup.
        Assert.False(resumed.SentSkillNames.Add("init")); // Already sent
        Assert.True(resumed.SentSkillNames.Add("verify")); // New
        Assert.Equal(3, resumed.SentSkillNames.Count);
    }

    [Fact]
    public void SentSkillNames_MutationSafety()
    {
        var state = new AgentRunState();
        state.SentSkillNames.Add("skill-a");
        var clone = state.DeepClone();
        clone.SentSkillNames.Add("skill-b");

        Assert.Contains("skill-a", state.SentSkillNames);
        Assert.DoesNotContain("skill-b", state.SentSkillNames);
        Assert.Equal(2, clone.SentSkillNames.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Combined state behavior
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CompactionDisabled_AndPlanMode_CanCoexist()
    {
        var state = new AgentRunState
        {
            CompactionDisabled = true,
            IsPlanMode = true,
            PrePlanMode = AgentPermissionModes.AutoApprove
        };

        Assert.True(state.CompactionDisabled);
        Assert.True(state.IsPlanMode);
        Assert.Equal(AgentPermissionModes.AutoApprove, state.PrePlanMode);
    }

    [Fact]
    public void DeepClone_PreservesAllNewFields()
    {
        var state = new AgentRunState
        {
            CompactionDisabled = true,
            IsPlanMode = true,
            PrePlanMode = AgentPermissionModes.BypassPermissions,
            EffectivePermissionMode = AgentPermissionModes.Plan,
            EffectiveAutoApproveTools = false,
            PrePlanAutoApproveTools = true,
            Messages = [new("user", "test")]
        };
        state.SentSkillNames.Add("init");

        var clone = state.DeepClone();

        Assert.True(clone.CompactionDisabled);
        Assert.True(clone.IsPlanMode);
        Assert.Equal(AgentPermissionModes.BypassPermissions, clone.PrePlanMode);
        Assert.Equal(AgentPermissionModes.Plan, clone.EffectivePermissionMode);
        Assert.False(clone.EffectiveAutoApproveTools);
        Assert.True(clone.PrePlanAutoApproveTools);
        Assert.Contains("init", clone.SentSkillNames);
        Assert.Single(clone.Messages);
    }

    [Fact]
    public void RecoveryState_SurvivesCheckpointJsonRoundTrip()
    {
        var state = new AgentRunState
        {
            ConsecutiveToolFailures = 2,
            ConsecutiveProviderFailures = 1,
            RepeatedFailureCount = 2,
            CompletionRecoveryAttempts = 1,
            RecoveryAttempts = 4,
            ResumeCount = 3,
            BudgetExtensionCount = 2,
            SuccessfulToolCalls = 7,
            LastSuccessfulStep = 41,
            LastFailedInvocationSignature = "ABC123",
            LastFailedToolName = AgentToolNames.SandboxExec,
            LastFailureSummary = "command failed",
            RecoveryDirectivePending = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AgentRunState>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.ConsecutiveToolFailures);
        Assert.Equal(1, restored.ConsecutiveProviderFailures);
        Assert.Equal(2, restored.RepeatedFailureCount);
        Assert.Equal(1, restored.CompletionRecoveryAttempts);
        Assert.Equal(4, restored.RecoveryAttempts);
        Assert.Equal(3, restored.ResumeCount);
        Assert.Equal(2, restored.BudgetExtensionCount);
        Assert.Equal(7, restored.SuccessfulToolCalls);
        Assert.Equal(41, restored.LastSuccessfulStep);
        Assert.Equal("ABC123", restored.LastFailedInvocationSignature);
        Assert.Equal(AgentToolNames.SandboxExec, restored.LastFailedToolName);
        Assert.Equal("command failed", restored.LastFailureSummary);
        Assert.True(restored.RecoveryDirectivePending);
    }

    [Fact]
    public void SoftStepBudget_ExtendsOnlyProductionRunsWithRecentProgress()
    {
        var progressing = new AgentRunState
        {
            CurrentStep = 48,
            MaxSteps = 48,
            LastSuccessfulStep = 46
        };
        var smallTestRun = new AgentRunState
        {
            CurrentStep = 3,
            MaxSteps = 3,
            LastSuccessfulStep = 3
        };
        var stalled = new AgentRunState
        {
            CurrentStep = 48,
            MaxSteps = 48,
            LastSuccessfulStep = 30
        };

        Assert.Equal(72, AgentRunEngineV2.CalculateExtendedSoftStepBudget(progressing));
        Assert.Equal(3, AgentRunEngineV2.CalculateExtendedSoftStepBudget(smallTestRun));
        Assert.Equal(48, AgentRunEngineV2.CalculateExtendedSoftStepBudget(stalled));
    }

    [Fact]
    public void SoftStepBudget_ExtendsForFirstFailureRecoveryAndHonorsHardCap()
    {
        var recovery = new AgentRunState
        {
            CurrentStep = 48,
            MaxSteps = 48,
            ConsecutiveToolFailures = 1,
            CompletionRecoveryAttempts = 0
        };
        var hardCapped = new AgentRunState
        {
            CurrentStep = 192,
            MaxSteps = 192,
            LastSuccessfulStep = 192
        };

        Assert.Equal(72, AgentRunEngineV2.CalculateExtendedSoftStepBudget(recovery));
        Assert.Equal(192, AgentRunEngineV2.CalculateExtendedSoftStepBudget(hardCapped));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: AgentEngineOptions — Plan mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AgentEngineOptions_PlanMode_AllCombinations()
    {
        var planOpts = new AgentEngineOptions(PermissionMode: "plan");
        Assert.Equal(AgentPermissionModes.Plan, AgentPermissionModes.Normalize(planOpts.PermissionMode));

        var bypassOpts = new AgentEngineOptions(PermissionMode: "bypass");
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize(bypassOpts.PermissionMode));

        var autoOpts = new AgentEngineOptions(PermissionMode: "auto");
        Assert.Equal(AgentPermissionModes.AutoApprove, AgentPermissionModes.Normalize(autoOpts.PermissionMode));

        var askOpts = new AgentEngineOptions(PermissionMode: "ask");
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize(askOpts.PermissionMode));

        var defaultOpts = new AgentEngineOptions();
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize(defaultOpts.PermissionMode));
    }
}
