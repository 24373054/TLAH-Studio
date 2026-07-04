namespace TLAHStudio.Core.Services;

public static class AgentPermissionModes
{
    public const string BypassPermissions = "bypass_permissions";
    public const string AutoApprove = "auto_approve";
    public const string RequestApproval = "request_approval";
    public const string Plan = "plan";   // M4.9.0

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            BypassPermissions => BypassPermissions,
            AutoApprove => AutoApprove,
            RequestApproval => RequestApproval,
            Plan => Plan,
            "bypasspermissions" => BypassPermissions,
            "bypass" => BypassPermissions,
            "auto" => AutoApprove,
            "ask" => RequestApproval,
            _ => RequestApproval
        };

    public static bool IsBypass(string? value) =>
        Normalize(value) == BypassPermissions;

    public static bool IsAutoApprove(string? value)
    {
        var n = Normalize(value);
        return n is BypassPermissions or AutoApprove && n != Plan;
    }

    public static bool IsPlan(string? value) =>
        Normalize(value) == Plan;

    public static string DisplayName(string? value) =>
        Normalize(value) switch
        {
            BypassPermissions => "Full access",
            AutoApprove => "Auto approve",
            RequestApproval => "Ask approval",
            Plan => "Plan",
            _ => "Ask approval"
        };
}
