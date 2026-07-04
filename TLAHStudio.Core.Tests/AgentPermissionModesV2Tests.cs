using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

/// <summary>
/// 4.8.0 + 4.9.0: Tests for AgentPermissionModes default fix and Plan mode addition.
/// </summary>
public class AgentPermissionModesV2Tests
{
    // ═══════════════════════════════════════════════════════════════
    // 4.8.0: Default fallback — unknown → RequestApproval
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_Null_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize(null));
    }

    [Fact]
    public void Normalize_Empty_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize(""));
    }

    [Fact]
    public void Normalize_Unknown_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("garbage_value"));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("   "));
    }

    [Fact]
    public void Normalize_KnownValues_Unchanged()
    {
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("bypass_permissions"));
        Assert.Equal(AgentPermissionModes.AutoApprove, AgentPermissionModes.Normalize("auto_approve"));
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("request_approval"));
    }

    [Fact]
    public void Normalize_Aliases_Work()
    {
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("bypass"));
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("bypasspermissions"));
        Assert.Equal(AgentPermissionModes.AutoApprove, AgentPermissionModes.Normalize("auto"));
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("ask"));
    }

    [Fact]
    public void Normalize_CaseInsensitive()
    {
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("BYPASS_PERMISSIONS"));
        Assert.Equal(AgentPermissionModes.AutoApprove, AgentPermissionModes.Normalize("Auto_Approve"));
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("REQUEST_APPROVAL"));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Plan mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_Plan_ReturnsPlan()
    {
        Assert.Equal(AgentPermissionModes.Plan, AgentPermissionModes.Normalize("plan"));
    }

    [Fact]
    public void IsPlan_Plan_ReturnsTrue()
    {
        Assert.True(AgentPermissionModes.IsPlan("plan"));
        Assert.True(AgentPermissionModes.IsPlan(AgentPermissionModes.Plan));
    }

    [Fact]
    public void IsPlan_Bypass_ReturnsFalse()
    {
        Assert.False(AgentPermissionModes.IsPlan(AgentPermissionModes.BypassPermissions));
        Assert.False(AgentPermissionModes.IsPlan("bypass"));
    }

    [Fact]
    public void IsPlan_AutoApprove_ReturnsFalse()
    {
        Assert.False(AgentPermissionModes.IsPlan(AgentPermissionModes.AutoApprove));
    }

    [Fact]
    public void IsPlan_RequestApproval_ReturnsFalse()
    {
        Assert.False(AgentPermissionModes.IsPlan(AgentPermissionModes.RequestApproval));
    }

    [Fact]
    public void IsAutoApprove_Plan_ReturnsFalse()
    {
        // Plan is NOT auto-approve — plan mode writes require explicit approval.
        Assert.False(AgentPermissionModes.IsAutoApprove("plan"));
        Assert.False(AgentPermissionModes.IsAutoApprove(AgentPermissionModes.Plan));
    }

    [Fact]
    public void IsAutoApprove_BypassAndAuto_ReturnsTrue()
    {
        Assert.True(AgentPermissionModes.IsAutoApprove(AgentPermissionModes.BypassPermissions));
        Assert.True(AgentPermissionModes.IsAutoApprove(AgentPermissionModes.AutoApprove));
    }

    [Fact]
    public void IsBypass_Plan_ReturnsFalse()
    {
        Assert.False(AgentPermissionModes.IsBypass(AgentPermissionModes.Plan));
        Assert.False(AgentPermissionModes.IsBypass("plan"));
    }

    [Fact]
    public void IsBypass_Bypass_ReturnsTrue()
    {
        Assert.True(AgentPermissionModes.IsBypass(AgentPermissionModes.BypassPermissions));
    }

    [Fact]
    public void DisplayName_Plan_ReturnsPlan()
    {
        Assert.Equal("Plan", AgentPermissionModes.DisplayName("plan"));
    }

    [Fact]
    public void DisplayName_KnownModes()
    {
        Assert.Equal("Full access", AgentPermissionModes.DisplayName(AgentPermissionModes.BypassPermissions));
        Assert.Equal("Auto approve", AgentPermissionModes.DisplayName(AgentPermissionModes.AutoApprove));
        Assert.Equal("Ask approval", AgentPermissionModes.DisplayName(AgentPermissionModes.RequestApproval));
        Assert.Equal("Plan", AgentPermissionModes.DisplayName(AgentPermissionModes.Plan));
    }

    [Fact]
    public void DisplayName_Unknown_ReturnsAskApproval()
    {
        Assert.Equal("Ask approval", AgentPermissionModes.DisplayName(null));
        Assert.Equal("Ask approval", AgentPermissionModes.DisplayName("random_string"));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Plan mode interaction matrix
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsPlan_AllModes_OnlyPlanReturnsTrue()
    {
        Assert.True(AgentPermissionModes.IsPlan("plan"));
        Assert.False(AgentPermissionModes.IsPlan("bypass_permissions"));
        Assert.False(AgentPermissionModes.IsPlan("auto_approve"));
        Assert.False(AgentPermissionModes.IsPlan("request_approval"));
        Assert.False(AgentPermissionModes.IsPlan(null));
        Assert.False(AgentPermissionModes.IsPlan(""));
    }

    [Fact]
    public void IsAutoApprove_AllModes_PlanExcluded()
    {
        Assert.True(AgentPermissionModes.IsAutoApprove("bypass_permissions"));
        Assert.True(AgentPermissionModes.IsAutoApprove("auto_approve"));
        Assert.True(AgentPermissionModes.IsAutoApprove("bypass"));
        Assert.False(AgentPermissionModes.IsAutoApprove("plan"));
        Assert.False(AgentPermissionModes.IsAutoApprove("request_approval"));
        Assert.False(AgentPermissionModes.IsAutoApprove("ask"));
        Assert.False(AgentPermissionModes.IsAutoApprove(null));
    }

    [Fact]
    public void IsBypass_AllModes_OnlyBypassReturnsTrue()
    {
        Assert.True(AgentPermissionModes.IsBypass("bypass_permissions"));
        Assert.True(AgentPermissionModes.IsBypass("bypass"));
        Assert.False(AgentPermissionModes.IsBypass("plan"));
        Assert.False(AgentPermissionModes.IsBypass("auto_approve"));
        Assert.False(AgentPermissionModes.IsBypass("request_approval"));
        Assert.False(AgentPermissionModes.IsBypass(null));
    }

    // ═══════════════════════════════════════════════════════════════
    // 4.9.0: Static field values
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Constants_HaveCorrectValues()
    {
        Assert.Equal("bypass_permissions", AgentPermissionModes.BypassPermissions);
        Assert.Equal("auto_approve", AgentPermissionModes.AutoApprove);
        Assert.Equal("request_approval", AgentPermissionModes.RequestApproval);
        Assert.Equal("plan", AgentPermissionModes.Plan);
    }

    [Fact]
    public void Normalize_LeadingTrailingWhitespace_Trims()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("  ask  "));
        Assert.Equal(AgentPermissionModes.Plan, AgentPermissionModes.Normalize("  plan  "));
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("  bypass  "));
    }

    [Fact]
    public void Normalize_PreservesPermissionsThroughPipeline()
    {
        // Simulate the entire pipeline: UI selects Plan → Normalize → IsPlan → DisplayName
        var fromUI = "plan";
        var normalized = AgentPermissionModes.Normalize(fromUI);
        Assert.True(AgentPermissionModes.IsPlan(normalized));
        Assert.False(AgentPermissionModes.IsAutoApprove(normalized));
        Assert.False(AgentPermissionModes.IsBypass(normalized));
        Assert.Equal("Plan", AgentPermissionModes.DisplayName(normalized));
    }
}
