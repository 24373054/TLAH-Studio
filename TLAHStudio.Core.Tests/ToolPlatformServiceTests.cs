using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class ToolPlatformServiceTests
{
    [Fact]
    public async Task ProjectAllowAppliesToProjectAndGlobalDenyWins()
    {
        await using var db = TestDb.Create();
        var project = new ProjectSpace { Name = "Tool project" };
        var chat = new Chat { Title = "Tool chat", ProjectSpaceId = project.Id };
        db.Set<ProjectSpace>().Add(project);
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var service = new ToolPlatformService(db);

        await service.SavePolicyAsync(
            chat.Id,
            AgentToolNames.FileRead,
            ToolPolicyScopes.Project,
            ToolPolicyDecisions.Allow);

        var allowed = await service.EvaluatePolicyAsync(chat.Id, AgentToolNames.FileRead);
        Assert.True(allowed.IsAllowed);
        Assert.Equal(ToolPolicyScopes.Project, allowed.Scope);

        await service.SavePolicyAsync(
            chat.Id,
            AgentToolNames.FileRead,
            ToolPolicyScopes.Global,
            ToolPolicyDecisions.Deny);

        var denied = await service.EvaluatePolicyAsync(chat.Id, AgentToolNames.FileRead);
        Assert.True(denied.IsDenied);
        Assert.Equal(ToolPolicyScopes.Global, denied.Scope);
    }

    [Fact]
    public async Task CredentialBrokerEnforcesToolAndDomainRules()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var service = new ToolPlatformService(db);
        await service.SaveCredentialAsync(
            null,
            "github",
            "secret-value-123",
            "api.github.com",
            AgentToolNames.HttpRequest);

        Assert.Equal(
            "secret-value-123",
            await service.ResolveCredentialAsync(
                "github", AgentToolNames.HttpRequest, "api.github.com"));
        Assert.Null(await service.ResolveCredentialAsync(
            "github", AgentToolNames.McpCall, "api.github.com"));
        Assert.Null(await service.ResolveCredentialAsync(
            "github", AgentToolNames.HttpRequest, "example.com"));
        Assert.DoesNotContain(
            "secret-value-123",
            (await db.Set<CredentialEntry>().SingleAsync()).ProtectedValue);
    }

    [Theory]
    [InlineData("api.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.org", "example.com", false)]
    public void DomainMatcherSupportsExactAndWildcard(
        string domain,
        string allowlist,
        bool expected)
    {
        Assert.Equal(expected, ToolPlatformService.MatchesDomainList(allowlist, domain));
    }

    [Fact]
    public async Task McpServerRoundTripsJsonConfiguration()
    {
        await using var db = TestDb.Create();
        var service = new ToolPlatformService(db);

        var saved = await service.SaveMcpServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "python-time",
            McpTransportTypes.Stdio,
            "python",
            """["C:\\tools\\server.py"]""",
            string.Empty,
            "{}",
            """{"PYTHONUTF8":"1"}""",
            true));

        var loaded = Assert.Single(await service.ListMcpServersAsync());
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("""["C:\\tools\\server.py"]""", loaded.ArgumentsJson);
        Assert.Equal("""{"PYTHONUTF8":"1"}""", loaded.EnvironmentJson);
    }

    [Fact]
    public async Task McpServerRejectsWrongJsonShapes()
    {
        await using var db = TestDb.Create();
        var service = new ToolPlatformService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveMcpServerAsync(new McpServerConfigDto(
                Guid.Empty,
                null,
                "invalid",
                McpTransportTypes.Stdio,
                "python",
                """{"path":"server.py"}""",
                string.Empty,
                "{}",
                "{}",
                true)));

        Assert.Contains("array of strings", error.Message);
    }
}
