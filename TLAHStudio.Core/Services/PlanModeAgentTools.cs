using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.AgentRuntime;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.0: Enter plan mode — switches the agent into read-only exploration.
/// Adopted from Claude Code's EnterPlanModeTool.ts.
/// </summary>
public sealed class EnterPlanModeAgentTool : IAgentTool
{
    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.EnterPlanMode,
        "Enter plan mode for read-only exploration and design. In this mode, file writes and terminal execution are blocked. Call exit_plan_mode when ready with your plan for user approval.",
        new Dictionary<string, object> { ["type"] = "object" });

    public bool RequiresApproval => false;

    public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        // The actual mode switch is performed by AgentRunEngine in the tool execution flow.
        // This tool returns the plan mode onboarding instructions, adopted from Claude Code.
        return Task.FromResult(new AgentToolResult(true, """
            Entered plan mode. You are now in a read-only exploration and design phase.

            In plan mode, you should:
            1. Thoroughly explore the codebase to understand existing patterns
            2. Identify similar features and architectural approaches
            3. Consider multiple approaches and their trade-offs
            4. Use ask_user_question if you need to clarify the approach
            5. Design a concrete implementation strategy
            6. When ready, use exit_plan_mode to present your plan for approval
            7. Call exit_plan_mode with the complete plan text

            Remember: DO NOT write or edit any files yet. This is a read-only exploration and planning phase.
            """));
    }
}

/// <summary>
/// M4.9.0: Exit plan mode — presents the plan for user approval, then restores
/// the previous permission mode. Adopted from Claude Code's ExitPlanModeV2Tool.ts.
/// </summary>
public sealed class ExitPlanModeAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public ExitPlanModeAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.ExitPlanMode,
        "Exit plan mode and present your complete plan for user approval. Requires explicit user approval.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["plan"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "The complete implementation plan to present for approval."
                }
            },
            ["required"] = new[] { "plan" }
        });

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var parseError))
            return new AgentToolResult(false, string.Empty, parseError);

        var suppliedPlan = root.TryGetProperty("plan", out var planElement) &&
            planElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? planElement.GetString()?.Trim()
            : null;

        var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
        var planPath = Path.Combine(sandboxRoot, ".tlah_context", "plans", $"{context.ChatId:D}-plan.md");
        string plan;
        if (!string.IsNullOrWhiteSpace(suppliedPlan))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            await File.WriteAllTextAsync(planPath, suppliedPlan, ct);
            plan = suppliedPlan;
        }
        else if (File.Exists(planPath))
        {
            plan = await File.ReadAllTextAsync(planPath, ct);
        }
        else
        {
            return new AgentToolResult(false, string.Empty, "A non-empty plan is required before leaving plan mode.");
        }

        return new AgentToolResult(true, $"""
            Plan presented for approval.

            ---
            {plan}
            ---

            Awaiting user approval to exit plan mode and resume with write access.
            """);
    }
}
