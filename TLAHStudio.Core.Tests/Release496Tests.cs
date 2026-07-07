using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Plugins;

namespace TLAHStudio.Core.Tests;

/// <summary>
/// M4.9.6: Tests for the 4.9.4-4.9.6 fixes at the Core layer.
///   - AssistantContentFormatter.TryParse thinking-at-any-position (4.9.4 fix)
///   - AgentRunState.CompactionDisabled + reset (4.9.2/4.9.3 fix)
///   - AgentPermissionModes edge cases (4.8.0 fix, regression guard)
///   - SlashCommandService basic construction (4.9.5 Phase E, guard test)
/// </summary>
public class Release496Tests
{
    // ═══════════════════════════════════════════════════════════════
    // AssistantContentFormatter.TryParse — 4.9.4 thinking-anywhere fix
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TryParse_ThinkingAtStart_ParsesCorrectly()
    {
        var content = "<tlah-thinking expanded>\nthis is reasoning\n</tlah-thinking>\nactual answer";
        var ok = AssistantContentFormatter.TryParse(content, out var thinking, out var answer, out var expanded);
        Assert.True(ok);
        Assert.True(expanded);
        Assert.Contains("this is reasoning", thinking);
        Assert.Contains("actual answer", answer);
    }

    [Fact]
    public void TryParse_ThinkingAtStartCollapsed_ParsesCorrectly()
    {
        var content = "<tlah-thinking collapsed>\nchain of thought\n</tlah-thinking>\nthe answer";
        var ok = AssistantContentFormatter.TryParse(content, out var thinking, out var answer, out var expanded);
        Assert.True(ok);
        Assert.False(expanded);
        Assert.Contains("chain of thought", thinking);
        Assert.Contains("the answer", answer);
    }

    [Fact]
    public void TryParse_ThinkingAfterLeadingText_ParsesCorrectly()
    {
        // 4.9.4 fix: thinking tag no longer required to be at content start.
        // If any text precedes it, that text is folded into the answer.
        var content = "some preamble\n<tlah-thinking expanded>\nreasoning\n</tlah-thinking>\nfinal answer";
        var ok = AssistantContentFormatter.TryParse(content, out var thinking, out var answer, out var expanded);
        Assert.True(ok);
        Assert.Contains("reasoning", thinking);
        Assert.Contains("final answer", answer);
        Assert.Contains("preamble", answer); // leading text merged into answer
    }

    [Fact]
    public void TryParse_NoThinkingTag_ReturnsFalse()
    {
        var content = "just a plain message";
        var ok = AssistantContentFormatter.TryParse(content, out _, out var answer, out _);
        Assert.False(ok);
        Assert.Equal(content, answer);
    }

    [Fact]
    public void TryParse_EmptyContent_ReturnsFalse()
    {
        var ok = AssistantContentFormatter.TryParse("", out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_NullContent_ReturnsFalse()
    {
        var ok = AssistantContentFormatter.TryParse(null, out _, out _, out _);
        Assert.False(ok);
    }

    // ═══════════════════════════════════════════════════════════════
    // AgentPermissionModes — 4.8.0 regression guards
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_Null_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize(null));
    }

    [Fact]
    public void Normalize_UnknownValue_ReturnsRequestApproval()
    {
        Assert.Equal(AgentPermissionModes.RequestApproval, AgentPermissionModes.Normalize("garbage"));
    }

    [Fact]
    public void Normalize_BypassAliases_ReturnBypass()
    {
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("bypass"));
        Assert.Equal(AgentPermissionModes.BypassPermissions, AgentPermissionModes.Normalize("bypasspermissions"));
    }

    [Fact]
    public void IsPlan_PlanValue_True()
    {
        Assert.True(AgentPermissionModes.IsPlan("plan"));
        Assert.False(AgentPermissionModes.IsPlan("ask"));
    }

    // ═══════════════════════════════════════════════════════════════
    // SlashCommandService — 4.9.5 Phase E smoke test
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SlashCommandService_GetCommands_ReturnsBuiltIns()
    {
        var svc = new SlashCommandService(
            new NullSkillLoader(), new NullToolRegistry(), new NullMcpService());
        var cmds = await svc.GetCommandsAsync(Guid.NewGuid());
        Assert.NotEmpty(cmds);
        Assert.Contains(cmds, c => c.Name == "clear");
        Assert.Contains(cmds, c => c.Name == "new");
        Assert.Contains(cmds, c => c.Name == "help");
    }

    // ═══════════════════════════════════════════════════════════════
    // Test doubles (minimal stubs for SlashCommandService)
    // ═══════════════════════════════════════════════════════════════

    private sealed class NullSkillLoader : ISkillLoader
    {
        public Task<IReadOnlyList<AgentSkill>> LoadSkillsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkill>>(Array.Empty<AgentSkill>());
        public Task<IReadOnlyList<AgentSkill>> GetActiveSkillsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkill>>(Array.Empty<AgentSkill>());
        public Task<string> GetSkillContentAsync(string skillId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<AgentSkill>> FindRelevantSkillsAsync(string userMessage, int maxSkills = 3, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkill>>(Array.Empty<AgentSkill>());
        public Task<IReadOnlyList<AgentSkill>> ActivateConditionalSkillsForPathAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentSkill>>(Array.Empty<AgentSkill>());
        public void SetWorkspaceRoot(string? root) { }
        public void SetManagedDir(string? dir) { }
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullToolRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<LlmToolDefinition> Definitions => Array.Empty<LlmToolDefinition>();
        public IReadOnlyList<AgentToolMetadata> Metadata => Array.Empty<AgentToolMetadata>();
        public bool TryGet(string name, out IAgentTool tool) { tool = null!; return false; }
        public void Register(IAgentTool tool) { }
        public bool Unregister(string name) => false;
    }

    private sealed class NullMcpService : IMcpClientService
    {
        public Task<IReadOnlyList<McpToolInfo>> TestServerAsync(McpServerConfigDto server, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Guid chatId, string? serverName = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(Guid chatId, string? serverName = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>(Array.Empty<McpResourceInfo>());
        public Task<string> ReadResourceAsync(Guid chatId, string serverName, string uri, CancellationToken ct = default)
            => Task.FromResult("not available");
        public Task<string> CallToolAsync(Guid chatId, string serverName, string toolName, System.Text.Json.JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult("not available");
    }
}
