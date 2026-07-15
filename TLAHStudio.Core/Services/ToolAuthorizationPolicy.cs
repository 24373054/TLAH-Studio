namespace TLAHStudio.Core.Services;

/// <summary>
/// Single source of truth for permission-mode semantics. Safety classification
/// describes an operation; this policy decides whether it is blocked, needs a
/// user decision, or may execute.
/// </summary>
public static class ToolAuthorizationPolicy
{
    public static ToolAuthorizationDecision Evaluate(
        string? permissionMode,
        ToolSafetyAssessment safety,
        ToolPolicyEvaluation? policy,
        bool toolRequiresApproval,
        bool requiresUserInteraction,
        bool isDestructive,
        bool isPlanMode,
        bool autoApproveTools,
        bool explicitlyApproved = false)
    {
        var mode = isPlanMode
            ? AgentPermissionModes.Plan
            : AgentPermissionModes.Normalize(permissionMode);

        if (safety.IsBlocked && !safety.CanOverrideBlock)
        {
            return ToolAuthorizationDecision.Block(
                "immutable_safety_block",
                safety.Warning ?? safety.Summary);
        }

        // Interaction tools still need a response in Full access; this is a
        // functional pause, not a safety interception.
        if (requiresUserInteraction && !explicitlyApproved)
            return ToolAuthorizationDecision.Approval("user_interaction");

        // A one-time approval authorizes the exact persisted invocation. Policy
        // changes and contextual sandbox restrictions must not veto it later.
        if (explicitlyApproved)
            return ToolAuthorizationDecision.Allow("explicit_user_approval", overridden: true);

        // Full access means what the UI says: ordinary policies, host paths,
        // network allowlists, and sensitive-file prompts are bypassed. Only the
        // immutable guard above remains.
        if (AgentPermissionModes.IsBypass(mode))
            return ToolAuthorizationDecision.Allow("full_access", overridden: policy?.IsDenied == true || safety.IsBlocked);

        if (policy?.IsDenied == true)
            return ToolAuthorizationDecision.Block("denied_by_policy", policy.Description ?? "Denied by tool policy.");

        var writeOrDestructive = safety.IsWriteOperation || isDestructive || !safety.IsReadOnly;
        if (mode == AgentPermissionModes.Plan && writeOrDestructive)
            return ToolAuthorizationDecision.Approval("plan_mode_write");

        // A stored allow is a durable user decision for this policy subject. It
        // must be considered before overridable host-path/network restrictions,
        // otherwise "allow for project/global" would ask again forever.
        if (policy?.IsAllowed == true)
            return ToolAuthorizationDecision.Allow("stored_allow_policy");

        if (safety.IsBlocked && safety.CanOverrideBlock)
            return ToolAuthorizationDecision.Approval("contextual_restriction");

        // Plan mode is read-only, not "ask for every tool". Safe inspection and
        // research should proceed without prompts so a plan can actually be
        // assembled; contextual and explicitly-sensitive reads were handled
        // above and still require a decision.
        if (mode == AgentPermissionModes.Plan && safety.IsReadOnly && !safety.RequiresExplicitApproval)
            return ToolAuthorizationDecision.Allow("plan_mode_read");

        var autoMode = mode == AgentPermissionModes.AutoApprove || autoApproveTools;
        if (autoMode)
        {
            return safety.BypassImmune
                ? ToolAuthorizationDecision.Approval("sensitive_path_auto_mode")
                : ToolAuthorizationDecision.Allow("auto_approve");
        }

        return toolRequiresApproval || safety.RequiresExplicitApproval
            ? ToolAuthorizationDecision.Approval("ask_mode")
            : ToolAuthorizationDecision.Allow("safe_operation");
    }

    public static bool CanExecuteAtBoundary(
        ToolSafetyAssessment safety,
        string? permissionMode,
        bool explicitlyApproved)
    {
        if (!safety.IsBlocked)
            return true;
        if (!safety.CanOverrideBlock)
            return false;
        return explicitlyApproved || AgentPermissionModes.IsBypass(permissionMode);
    }
}

public sealed record ToolAuthorizationDecision(
    bool IsBlocked,
    bool RequiresApproval,
    bool WasOverridden,
    string ReasonCode,
    string? Message = null)
{
    public static ToolAuthorizationDecision Allow(string reasonCode, bool overridden = false) =>
        new(false, false, overridden, reasonCode);

    public static ToolAuthorizationDecision Approval(string reasonCode) =>
        new(false, true, false, reasonCode);

    public static ToolAuthorizationDecision Block(string reasonCode, string? message = null) =>
        new(true, false, false, reasonCode, message);
}
