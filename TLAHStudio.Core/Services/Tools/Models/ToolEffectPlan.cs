namespace TLAHStudio.Core.Services.Tools.Models;

/// <summary>
/// M2.9.0: Concrete effect prediction for a tool invocation.
/// Describes what the tool will read, write, access, and execute.
/// Used by permission UI and safety assessment.
/// </summary>
public sealed record ToolEffectPlan(
    IReadOnlyList<string> PathsRead,
    IReadOnlyList<string> PathsWritten,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> EnvironmentVariables,
    IReadOnlyList<string> CredentialNames,
    string RiskLevel, // none, low, medium, high, blocked
    bool IsDestructive,
    bool HasRollback
)
{
    public static ToolEffectPlan Empty { get; } = new(
        [], [], [], [], [], [], "none", false, false);

    public static ToolEffectPlan ReadOnly(IReadOnlyList<string> paths) => new(
        paths, [], [], [], [], [], "low", false, false);

    public static ToolEffectPlan Write(IReadOnlyList<string> pathsRead, IReadOnlyList<string> pathsWritten, bool hasRollback = false) => new(
        pathsRead, pathsWritten, [], [], [], [],
        "medium", true, hasRollback);

    public static ToolEffectPlan Network(IReadOnlyList<string> domains, string risk = "medium") => new(
        [], [], domains, [], [], [], risk, false, false);

    public static ToolEffectPlan Command(IReadOnlyList<string> commands, string risk = "high") => new(
        [], [], [], commands, [], [], risk, true, false);
}

/// <summary>
/// M2.9.0: Per-tool safety classification result.
/// Replaces the monolithic regex-based ToolSafetyKernel.Assess pattern.
/// </summary>
public sealed record ToolSafetyClassification(
    string Level,       // low, medium, high, blocked
    string Category,     // command, path, file_write, git, network, mcp, memory, code_write, code_patch, code_rollback, protocol, tool
    bool IsReadOnly,
    bool IsDestructive,
    bool RequiresExplicitApproval,
    bool IsBlocked,
    string Summary,
    string? Warning,
    ToolEffectPlan? EffectPlan
);

/// <summary>
/// M2.9.0: Tool execution progress update.
/// </summary>
public sealed record AgentToolProgress(
    string Phase,       // setup, running, writing, cleanup
    int Percent,        // 0-100
    string Message
);

/// <summary>
/// M2.9.0: Rollback plan for a completed tool invocation.
/// </summary>
public sealed record ToolRollbackPlan(
    bool IsFeasible,
    string Strategy,
    string? RollbackCommand,
    IReadOnlyList<string>? FilesToRestore
);
