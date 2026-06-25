using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class SandboxCommandServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RunsCommandInsideChatSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Sandbox.Tests", Guid.NewGuid().ToString("N"));
        var service = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();

        var result = await service.ExecuteAsync(chatId, "'hello sandbox' | Set-Content result.txt; Get-Content result.txt");

        Assert.False(result.WasBlocked);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello sandbox", result.StandardOutput);
        Assert.True(File.Exists(Path.Combine(service.GetSandboxRoot(chatId), "result.txt")));
    }

    [Fact]
    public async Task ExecuteAsync_PreservesUtf8ChineseOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Sandbox.Tests", Guid.NewGuid().ToString("N"));
        var service = new SandboxCommandService(root);

        var result = await service.ExecuteAsync(
            Guid.NewGuid(),
            "Write-Output '测试中文 - 你好世界'");

        Assert.False(result.WasBlocked);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("测试中文 - 你好世界", result.StandardOutput);
        Assert.DoesNotContain('\uFFFD', result.StandardOutput);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksDestructiveHostCommands()
    {
        var service = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Sandbox.Tests", Guid.NewGuid().ToString("N")));

        var result = await service.ExecuteAsync(Guid.NewGuid(), "Remove-Item $env:USERPROFILE\\Documents -Recurse -Force");

        Assert.True(result.WasBlocked);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("protected host path", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesNestedPowerShellFileInvocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Sandbox.Tests", Guid.NewGuid().ToString("N"));
        var service = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(service.GetSandboxRoot(chatId), "test.ps1"),
            "Write-Output 'script-ok'");

        var result = await service.ExecuteAsync(chatId, "powershell -ExecutionPolicy Bypass -File \"test.ps1\"");

        Assert.False(result.WasBlocked);
        Assert.Contains("script-ok", result.StandardOutput);
    }

    [Fact]
    public async Task ExecuteAsync_BlocksDangerousScriptContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Sandbox.Tests", Guid.NewGuid().ToString("N"));
        var service = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(service.GetSandboxRoot(chatId), "danger.ps1"),
            "Remove-Item $env:USERPROFILE\\Documents -Recurse -Force");

        var result = await service.ExecuteAsync(chatId, "& .\\danger.ps1");

        Assert.True(result.WasBlocked);
        Assert.Contains("blocked content", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }
}
