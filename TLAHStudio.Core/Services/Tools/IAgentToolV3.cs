using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services.Tools;

/// <summary>
/// M2.9.0: Extended agent tool interface with safety classification,
/// effect planning, progress streaming, and rollback support.
/// Extends the existing IAgentTool for backward compatibility.
/// </summary>
public interface IAgentToolV3 : IAgentTool
{
    /// <summary>
    /// Classify safety for this specific tool invocation.
    /// Replaces the centralized regex-based assessment with per-tool logic.
    /// </summary>
    Task<ToolSafetyClassification> ClassifySafetyAsync(
        string argumentsJson,
        Guid chatId,
        ISandboxCommandService sandbox,
        CancellationToken ct = default);

    /// <summary>
    /// Predict the concrete effects of this tool invocation.
    /// Returns paths, domains, commands, and other resources affected.
    /// </summary>
    Task<ToolEffectPlan> PlanEffectsAsync(
        string argumentsJson,
        Guid chatId,
        ISandboxCommandService sandbox,
        CancellationToken ct = default);

    /// <summary>
    /// Execute the tool with progress streaming.
    /// </summary>
    Task<AgentToolResult> ExecuteWithProgressAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        IProgress<AgentToolProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Create a rollback plan for this tool invocation.
    /// Returns null if rollback is not feasible.
    /// </summary>
    Task<ToolRollbackPlan?> CreateRollbackPlanAsync(
        string argumentsJson,
        AgentToolResult result,
        CancellationToken ct = default);

    /// <summary>
    /// Which hook triggers this tool supports.
    /// </summary>
    ToolHookTriggers SupportedHooks { get; }
}

/// <summary>
/// Base class for V3 tools with default implementations.
/// </summary>
public abstract class AgentToolV3Base : IAgentToolV3
{
    public abstract LlmToolDefinition Definition { get; }
    public abstract bool RequiresApproval { get; }
    public abstract Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context, string argumentsJson, CancellationToken ct);

    public virtual ToolHookTriggers SupportedHooks => ToolHookTriggers.All;

    public virtual Task<ToolSafetyClassification> ClassifySafetyAsync(
        string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        // Default: delegate to the existing ToolSafetyKernel for backward compat
        var assessment = ToolSafetyKernel.Assess(sandbox, chatId, Definition.Name, argumentsJson);
        return Task.FromResult(new ToolSafetyClassification(
            assessment.Level, assessment.Category, assessment.IsReadOnly,
            assessment.IsWriteOperation, assessment.RequiresExplicitApproval,
            assessment.IsBlocked, assessment.Summary, assessment.Warning, null,
            assessment.CanOverrideBlock, assessment.BypassImmune));
    }

    public virtual Task<ToolEffectPlan> PlanEffectsAsync(
        string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
        => Task.FromResult(ToolEffectPlan.Empty);

    public virtual Task<AgentToolResult> ExecuteWithProgressAsync(
        AgentToolExecutionContext context, string argumentsJson,
        IProgress<AgentToolProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new AgentToolProgress("running", 50, $"Executing {Definition.Name}..."));
        var result = ExecuteAsync(context, argumentsJson, ct);
        progress?.Report(new AgentToolProgress("cleanup", 100,
            result.IsCompletedSuccessfully ? "Completed" : "Failed"));
        return result;
    }

    public virtual Task<ToolRollbackPlan?> CreateRollbackPlanAsync(
        string argumentsJson, AgentToolResult result, CancellationToken ct = default)
        => Task.FromResult<ToolRollbackPlan?>(null);
}
