using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public sealed class ToolAuthorizationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TLAHStudio.Authorization.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullAccess_BypassesContextualRestrictionAndDenyRule()
    {
        var safety = AssessTerminal("Get-Content C:\\Users\\Public\\note.txt");
        Assert.True(safety.IsBlocked);
        Assert.True(safety.CanOverrideBlock);

        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.BypassPermissions,
            safety,
            new ToolPolicyEvaluation(ToolPolicyDecisions.Deny, ToolPolicyScopes.Global),
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: true);

        Assert.False(decision.IsBlocked);
        Assert.False(decision.RequiresApproval);
        Assert.True(decision.WasOverridden);
    }

    [Fact]
    public void AskMode_ContextualRestrictionRequiresApproval_ThenExecutes()
    {
        var safety = AssessTerminal("Get-Content C:\\Users\\Public\\note.txt");
        var before = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.RequestApproval,
            safety,
            policy: null,
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false);
        var after = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.RequestApproval,
            safety,
            new ToolPolicyEvaluation(ToolPolicyDecisions.Deny, ToolPolicyScopes.Chat),
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false,
            explicitlyApproved: true);

        Assert.True(before.RequiresApproval);
        Assert.False(after.IsBlocked);
        Assert.False(after.RequiresApproval);
        Assert.Equal("explicit_user_approval", after.ReasonCode);
    }

    [Fact]
    public void PlanPermissionMode_RequiresApprovalForWriteEvenIfRuntimeFlagIsStale()
    {
        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.Plan,
            ToolSafetyAssessment.Medium(
                "file_write",
                isReadOnly: false,
                isWrite: true,
                summary: "Write a file."),
            policy: null,
            toolRequiresApproval: false,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false);

        Assert.True(decision.RequiresApproval);
        Assert.Equal("plan_mode_write", decision.ReasonCode);
    }

    [Fact]
    public void StoredAllow_BypassesOverridableContextualRestriction()
    {
        var safety = AssessTerminal("Get-Content C:\\Users\\Public\\note.txt");
        Assert.True(safety.IsBlocked);
        Assert.True(safety.CanOverrideBlock);

        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.RequestApproval,
            safety,
            new ToolPolicyEvaluation(ToolPolicyDecisions.Allow, ToolPolicyScopes.Project),
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false);

        Assert.False(decision.IsBlocked);
        Assert.False(decision.RequiresApproval);
        Assert.Equal("stored_allow_policy", decision.ReasonCode);
    }

    [Fact]
    public void PlanPermissionMode_AllowsSafeReadWithoutPerToolPrompt()
    {
        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.Plan,
            ToolSafetyAssessment.LowRead(
                "file_read",
                summary: "Read a workspace file."),
            policy: null,
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false);

        Assert.False(decision.IsBlocked);
        Assert.False(decision.RequiresApproval);
        Assert.Equal("plan_mode_read", decision.ReasonCode);
    }

    [Fact]
    public void PlanPermissionMode_DoesNotLetStoredAllowAuthorizeWrites()
    {
        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.Plan,
            ToolSafetyAssessment.Medium(
                "file_write",
                isReadOnly: false,
                isWrite: true,
                summary: "Write a file."),
            new ToolPolicyEvaluation(ToolPolicyDecisions.Allow, ToolPolicyScopes.Global),
            toolRequiresApproval: false,
            requiresUserInteraction: false,
            isDestructive: false,
            isPlanMode: false,
            autoApproveTools: false);

        Assert.True(decision.RequiresApproval);
        Assert.Equal("plan_mode_write", decision.ReasonCode);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -r -f --no-preserve-root /")]
    [InlineData("rm --recursive --force -- /")]
    [InlineData("Remove-Item C:\\Windows -Recurse -Force")]
    [InlineData("Remove-Item C:\\Windows -Recurse:$true -Force")]
    [InlineData("Remove-Item C:\\Windows -Rec -Force")]
    [InlineData("Remove-Item C:\\Windows -Re -Force")]
    [InlineData("Get-ChildItem C:\\Windows | Remove-Item -Recurse -Force")]
    [InlineData("Get-ChildItem -Path C:\\Windows | Remove-Item -Rec:$true -Force")]
    [InlineData("'C:\\Windows' | Remove-Item -Recurse -Force")]
    [InlineData("Write-Output C:\\Windows | Remove-Item -Recurse -Force")]
    [InlineData("echo C:\\Windows | Remove-Item -Re -Force")]
    [InlineData("Remove-Item -LiteralPath $env:SystemDrive\\ -Recurse -Force")]
    [InlineData("Remove-Item $env:SystemRoot\\System32 -Recurse -Force")]
    [InlineData("Remove-Item C:\\Users\\Alice -Recurse -Force")]
    [InlineData("del C:\\ -Recurse -Force")]
    [InlineData("del C:\\Windows -Rec -Force")]
    [InlineData("rd C:\\ -Recurse -Force")]
    [InlineData("rmdir C:\\Windows -Recurse:$true -Force")]
    [InlineData("rd /s /q C:\\")]
    [InlineData("Remove-Item C:\\Windows\\*.* -Recurse -Force")]
    [InlineData("Remove-Item C:\\Windows\\System32\\* -Rec:$true -Force")]
    [InlineData("Remove-Item C:\\Temp\\..\\Windows -Rec -Force")]
    [InlineData("Remove-Item FileSystem::C:\\Windows\\* -Rec -Force")]
    [InlineData("1 | ForEach-Object { Remove-Item C:\\Windows -Recurse -Force }")]
    [InlineData("1 | Where-Object { Remove-Item C:\\Windows -Recurse -Force; $true }")]
    [InlineData("Invoke-Command -ScriptBlock { Remove-Item C:\\Windows -Recurse -Force }")]
    [InlineData("Start-Job { Remove-Item C:\\Windows -Recurse -Force }")]
    [InlineData("Get-ChildItem C:\\Windows | % { Remove-Item $_ -Rec -Force }")]
    [InlineData("format C:")]
    [InlineData("cmd /d /s /c \"format C: /Q /Y\"")]
    [InlineData("cmd /c \"fo^rmat C:\"")]
    [InlineData("powershell -Command \"Remove-Item $env:SystemDrive\\ -Recurse -Force\"")]
    [InlineData("powershell -Command \"& { Clear-Disk -Number 0 -RemoveData }\"")]
    [InlineData("Invoke-Expression 'Clear-Disk -Number 0 -RemoveData'")]
    [InlineData("sh -c 'rm -r -f /'")]
    [InlineData("bash -lc 'rm -r -f /'")]
    [InlineData("wsl r\\m -rf /")]
    [InlineData("wsl -u root --exec rm -rf /etc /usr /bin")]
    [InlineData("wsl -- rm -rf /var/*")]
    [InlineData("wsl --cd / --exec rm -rf etc")]
    [InlineData("wsl --cd=/ --exec sh -c 'rm -rf usr'")]
    [InlineData("rm -rf /bin")]
    [InlineData("rm -rf /boot")]
    [InlineData("rm -rf /dev")]
    [InlineData("rm -rf /etc")]
    [InlineData("rm -rf /lib")]
    [InlineData("rm -rf /lib64")]
    [InlineData("rm -rf /proc")]
    [InlineData("rm -rf /root")]
    [InlineData("rm -rf /sbin")]
    [InlineData("rm -rf /sys")]
    [InlineData("rm -rf /usr")]
    [InlineData("rm -rf /var")]
    [InlineData("rm -rf /etc/*")]
    [InlineData("rm -rf /*")]
    [InlineData("sh -c 'rm -rf /boot/*'")]
    [InlineData("bash -lc 'rm -rf /lib64/*'")]
    [InlineData("Clear-Disk -Number 0 -RemoveData")]
    [InlineData("bcdedit /delete {current}")]
    [InlineData("bootrec /fixmbr")]
    [InlineData("Remove-LocalUser -Name old-user")]
    [InlineData("net user old-user /delete")]
    [InlineData("echo clean | diskpart")]
    public void CatastrophicCommands_AreImmutableInFullAccess(string command)
    {
        var safety = AssessTerminal(command);
        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.BypassPermissions,
            safety,
            policy: null,
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: true,
            isPlanMode: false,
            autoApproveTools: true,
            explicitlyApproved: true);

        Assert.True(safety.IsBlocked);
        Assert.False(safety.CanOverrideBlock);
        Assert.True(decision.IsBlocked);
        Assert.Equal("immutable_safety_block", decision.ReasonCode);
    }

    [Theory]
    [InlineData("Get-Date -Format o")]
    [InlineData("powershell -NoProfile -Command Get-Date")]
    [InlineData("Remove-Item .\\artifacts -Recurse -Force")]
    [InlineData("Remove-Item .\\artifacts -Re -Force")]
    [InlineData("rm -rf ./build")]
    [InlineData("Remove-Item C:\\Windows\\Temp -Recurse -Force")]
    [InlineData("Remove-Item C:\\Windows\\Temp\\*.* -Rec -Force")]
    [InlineData("Remove-Item C:\\Windows\\Temp\\..\\Temp -Rec -Force")]
    [InlineData("del .\\workspace -Rec -Force")]
    [InlineData("rmdir .\\workspace -Recurse:$true -Force")]
    [InlineData("ForEach-Object { Remove-Item .\\workspace -Recurse -Force }")]
    [InlineData("'.\\workspace' | Remove-Item -Recurse -Force")]
    [InlineData("Write-Output .\\workspace | Remove-Item -Recurse -Force")]
    [InlineData("wsl -- rm -rf /home/user/workspace")]
    [InlineData("wsl -- rm -rf /var/tmp/workspace")]
    [InlineData("wsl --cd /home/user --exec rm -rf workspace")]
    [InlineData("rg -n \"diskpart\" docs")]
    [InlineData("rg \"rm -rf /\" .")]
    [InlineData("Write-Output 'rm -rf /'")]
    [InlineData("cmd /c \"echo format C:\"")]
    [InlineData("powershell -Command \"Write-Output 'rm -rf /'\"")]
    [InlineData("Remove-Item C:\\ -Recurse -WhatIf")]
    [InlineData("Remove-Item C:\\Windows -Recurse:$false -Force")]
    [InlineData("Remove-Item C:\\Windows -Re:$false -Force")]
    [InlineData("del C:\\Windows -Rec:$false -Force")]
    [InlineData("rmdir C:\\Windows -Recurse:$false -Force")]
    [InlineData("Remove-Item C:\\Windows\\*.* -Rec -Force -WhatIf:$true")]
    [InlineData("Get-ChildItem C:\\Windows | Remove-Item -Rec -Force -WhatIf")]
    [InlineData("Write-Output C:\\Windows | Remove-Item -Re -Force -WhatIf")]
    [InlineData("Clear-Disk -Number 0 -RemoveData -WhatIf")]
    [InlineData("Clear-Disk -Number 0 -RemoveData -WhatIf:$true")]
    [InlineData("echo list disk | diskpart")]
    [InlineData("bcdedit /enum")]
    [InlineData("bootrec /scanos")]
    [InlineData("net user")]
    public void OrdinaryCommands_AreNotHardBlocked(string command)
    {
        var safety = AssessTerminal(command);

        Assert.False(safety.IsBlocked && !safety.CanOverrideBlock);
    }

    [Fact]
    public void EncodedCatastrophicPowerShell_IsImmutableInFullAccess()
    {
        var payload = Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes("Clear-Disk -Number 0 -RemoveData"));
        var safety = AssessTerminal($"powershell -EncodedCommand {payload}");

        Assert.True(safety.IsBlocked);
        Assert.False(safety.CanOverrideBlock);
    }

    [Fact]
    public void InvalidEncodedPowerShell_IsHighButNotHardBlocked()
    {
        var safety = AssessTerminal("powershell -EncodedCommand not-base64!");

        Assert.Equal(ToolSafetyLevels.High, safety.Level);
        Assert.False(safety.IsBlocked);
    }

    [Fact]
    public void DiskPartReadOnlyScript_IsNotHardBlocked()
    {
        var safety = AssessTerminal(
            "diskpart /s list-only.txt",
            root => File.WriteAllText(
                Path.Combine(root, "list-only.txt"),
                "list disk\r\nlist volume\r\n"));

        Assert.False(safety.IsBlocked && !safety.CanOverrideBlock);
    }

    [Fact]
    public void DiskPartDestructiveScript_IsImmutableInFullAccess()
    {
        var safety = AssessTerminal(
            "diskpart /s destructive.txt",
            root => File.WriteAllText(
                Path.Combine(root, "destructive.txt"),
                "select disk 0\r\nclean\r\n"));

        Assert.True(safety.IsBlocked);
        Assert.False(safety.CanOverrideBlock);
    }

    [Fact]
    public void DiskPartRedirectedDestructiveInput_IsImmutableInFullAccess()
    {
        var safety = AssessTerminal(
            "diskpart < destructive.txt",
            root => File.WriteAllText(
                Path.Combine(root, "destructive.txt"),
                "select disk 0\r\nclean\r\n"));

        var decision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.BypassPermissions,
            safety,
            policy: null,
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: true,
            isPlanMode: false,
            autoApproveTools: true,
            explicitlyApproved: true);

        Assert.True(safety.IsBlocked);
        Assert.False(safety.CanOverrideBlock);
        Assert.True(decision.IsBlocked);
        Assert.Equal("immutable_safety_block", decision.ReasonCode);
    }

    [Fact]
    public void DiskPartRedirectedReadOnlyInput_IsNotHardBlocked()
    {
        var safety = AssessTerminal(
            "diskpart < list-only.txt",
            root => File.WriteAllText(
                Path.Combine(root, "list-only.txt"),
                "list disk\r\nlist volume\r\n"));

        Assert.False(safety.IsBlocked && !safety.CanOverrideBlock);
    }

    private ToolSafetyAssessment AssessTerminal(
        string command,
        Action<string>? prepare = null)
    {
        var sandbox = new SandboxCommandService(_root);
        var chatId = Guid.NewGuid();
        prepare?.Invoke(sandbox.GetSandboxRoot(chatId));
        return ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.TerminalExec,
            System.Text.Json.JsonSerializer.Serialize(new { command }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
