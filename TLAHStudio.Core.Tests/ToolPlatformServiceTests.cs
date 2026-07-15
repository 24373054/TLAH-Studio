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

    [Fact]
    public async Task PolicyRulesMatchToolPatternsMcpPathsAndDomains()
    {
        await using var db = TestDb.Create();
        var project = new ProjectSpace { Name = "Policy project" };
        var chat = new Chat { Title = "Policy chat", ProjectSpaceId = project.Id };
        db.Set<ProjectSpace>().Add(project);
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var service = new ToolPlatformService(db);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Tool,
            Pattern: "tool(read)",
            Scope: ToolPolicyScopes.Global,
            Decision: ToolPolicyDecisions.Allow,
            Description: "Read code globally."));
        var read = await service.EvaluatePolicyAsync(chat.Id, AgentToolNames.CodeRead, """{"path":"src/App.xaml"}""");
        Assert.True(read.IsAllowed);
        Assert.Equal(ToolPolicySubjects.Tool, read.SubjectKind);
        Assert.Equal("read", read.Pattern);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Tool,
            Pattern: "mcp__time_server__get_time",
            Scope: ToolPolicyScopes.Project,
            Decision: ToolPolicyDecisions.Allow,
            Description: "Allow project time server.",
            ProjectSpaceId: project.Id));
        var mcp = await service.EvaluatePolicyAsync(
            chat.Id,
            AgentToolNames.McpCall,
            """{"server":"Time Server","tool":"get time"}""");
        Assert.True(mcp.IsAllowed);
        Assert.Equal(ToolPolicyScopes.Project, mcp.Scope);
        Assert.Equal("mcp__time_server__get_time", mcp.MatchedValue);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Path,
            Pattern: "src/generated/**",
            Scope: ToolPolicyScopes.Global,
            Decision: ToolPolicyDecisions.Deny,
            Description: "Generated files are read-only."));
        var pathDenied = await service.EvaluatePolicyAsync(
            chat.Id,
            AgentToolNames.CodeEdit,
            """{"path":"src/generated/out.cs","new_text":"x"}""");
        Assert.True(pathDenied.IsDenied);
        Assert.Equal(ToolPolicySubjects.Path, pathDenied.SubjectKind);
        Assert.Equal("src/generated/out.cs", pathDenied.MatchedValue);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Domain,
            Pattern: "*.example.com",
            Scope: ToolPolicyScopes.Global,
            Decision: ToolPolicyDecisions.Allow,
            Description: "Allow example API reads."));
        var domainAllowed = await service.EvaluatePolicyAsync(
            chat.Id,
            AgentToolNames.HttpRequest,
            """{"method":"GET","url":"https://api.example.com/v1/models"}""");
        Assert.True(domainAllowed.IsAllowed);
        Assert.Equal(ToolPolicySubjects.Domain, domainAllowed.SubjectKind);
        Assert.Equal("api.example.com", domainAllowed.MatchedValue);
    }

    [Fact]
    public async Task CommandPolicyRulesRetainSubjectKindAndMatchExtractedCommands()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "Command policy chat" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var service = new ToolPlatformService(db);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Command,
            Pattern: "git status*",
            Scope: ToolPolicyScopes.Global,
            Decision: ToolPolicyDecisions.Allow,
            Description: "Allow Git status commands."));

        var allowed = await service.EvaluatePolicyAsync(
            chat.Id,
            AgentToolNames.TerminalExec,
            """{"command":"git status --short"}""");

        Assert.True(allowed.IsAllowed);
        Assert.Equal(ToolPolicySubjects.Command, allowed.SubjectKind);
        Assert.Equal("git status --short", allowed.MatchedValue);

        await service.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            Id: null,
            SubjectKind: ToolPolicySubjects.Command,
            Pattern: "remove-item *",
            Scope: ToolPolicyScopes.Global,
            Decision: ToolPolicyDecisions.Deny,
            Description: "Deny Remove-Item commands."));

        var denied = await service.EvaluatePolicyAsync(
            chat.Id,
            AgentToolNames.SandboxExec,
            """{"command":"Remove-Item .\\build -Recurse"}""");

        Assert.True(denied.IsDenied);
        Assert.Equal(ToolPolicySubjects.Command, denied.SubjectKind);
        Assert.Equal("remove-item .\\build -recurse", denied.MatchedValue);
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
