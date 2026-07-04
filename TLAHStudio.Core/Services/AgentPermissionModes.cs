namespace TLAHStudio.Core.Services;

public static class AgentPermissionModes
{
    public const string BypassPermissions = "bypass_permissions";
    public const string AutoApprove = "auto_approve";
    public const string RequestApproval = "request_approval";

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            BypassPermissions => BypassPermissions,
            AutoApprove => AutoApprove,
            RequestApproval => RequestApproval,
            "bypasspermissions" => BypassPermissions,
            "bypass" => BypassPermissions,
            "auto" => AutoApprove,
            "ask" => RequestApproval,
            _ => RequestApproval
        };

    public static bool IsBypass(string? value) =>
        Normalize(value) == BypassPermissions;

    public static bool IsAutoApprove(string? value) =>
        Normalize(value) is BypassPermissions or AutoApprove;

    public static string DisplayName(string? value) =>
        Normalize(value) switch
        {
            BypassPermissions => "Full access",
            AutoApprove => "Auto approve",
            RequestApproval => "Ask approval",
            _ => "Ask approval"
        };
}
