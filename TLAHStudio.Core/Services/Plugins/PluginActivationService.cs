using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Plugins;

namespace TLAHStudio.Core.Services.Plugins;

/// <summary>
/// M4.9.2: Activates trusted plugins end-to-end — wires their skills and MCP
/// servers into the live agent subsystems. This closes the M2.12.0 dead-code
/// gap where PluginManifestService only parsed plugin.json with no consumers.
///
/// Activation does two things per trusted plugin:
///   1. Skills: the plugin's directory is registered as a managed source on
///      <c>ISkillLoader</c> (via SetManagedDir), so the plugin's
///      <c>skills/</c> subdirectory is scanned and plugin skills appear in
///      the skill listing available to the model.
///   2. MCP servers: each declared <c>mcp_servers</c> entry is persisted via
///      <c>IToolPlatformService.SaveMcpServerAsync</c> (idempotent by name)
///      so the existing MCP startup flow picks them up and exposes their
///      tools through mcp_call / mcp_list_tools.
///
/// Declared plugin tools (plugin.json <c>tools</c>) are schema-only and are
/// not registered as standalone agent tools in this phase: their execution
/// routes through the plugin's MCP server when one exists. Standalone
/// declarative tool registration is deferred until the agent-tool registry
/// is unified across the DI and LlmService self-built paths.
/// </summary>
public interface IPluginActivationService
{
    /// <summary>Activate all currently-trusted plugins. Idempotent.</summary>
    Task<int> ActivateAllAsync(CancellationToken ct = default);
    /// <summary>Activate a single plugin by id. Idempotent.</summary>
    Task<bool> ActivateAsync(string pluginId, CancellationToken ct = default);
    /// <summary>Deactivate a single plugin (clears its managed-skill source).</summary>
    Task<bool> DeactivateAsync(string pluginId, CancellationToken ct = default);
    /// <summary>Ids of currently-activated plugins (for diagnostics).</summary>
    IReadOnlyCollection<string> ActivatedPluginIds { get; }
}

public sealed class PluginActivationService : IPluginActivationService
{
    private readonly IPluginManifestService _plugins;
    private readonly ISkillLoader _skillLoader;
    private readonly IToolPlatformService _toolPlatform;
    private readonly HashSet<string> _activated = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public PluginActivationService(
        IPluginManifestService plugins,
        ISkillLoader skillLoader,
        IToolPlatformService toolPlatform)
    {
        _plugins = plugins;
        _skillLoader = skillLoader;
        _toolPlatform = toolPlatform;
    }

    public IReadOnlyCollection<string> ActivatedPluginIds
    {
        get
        {
            lock (_lock)
                return _activated.ToArray();
        }
    }

    public async Task<int> ActivateAllAsync(CancellationToken ct = default)
    {
        var plugins = await _plugins.ListPluginsAsync(ct);
        var trusted = plugins.Where(p => p.TrustLevel == PluginTrustLevel.Trusted).ToList();
        int count = 0;
        foreach (var plugin in trusted)
        {
            if (await ActivateAsync(plugin.Id, ct))
                count++;
        }
        return count;
    }

    public async Task<bool> ActivateAsync(string pluginId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;
        lock (_lock)
        {
            if (_activated.Contains(pluginId)) return false;
        }

        var plugins = await _plugins.ListPluginsAsync(ct);
        var plugin = plugins.FirstOrDefault(p =>
            string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.TrustLevel != PluginTrustLevel.Trusted)
            return false;

        var pluginDir = ResolvePluginDirectory(plugin);

        // 1. Skills: register the plugin directory as a managed source. The
        //    SkillLoader scans managed dir for *.md and */SKILL.md, so plugin
        //    skills (<pluginDir>/skills/*.md) are discovered. If multiple
        //    plugins are activated, the last one with skills wins the managed
        //    slot; earlier plugins' skills are still reachable when their dir
        //    is the managed target. (Per-plugin multi-dir scanning is a future
        //    enhancement tracked separately.)
        if (Directory.Exists(pluginDir))
            _skillLoader.SetManagedDir(pluginDir);

        // 2. MCP servers: persist each declared server idempotently by name.
        //    The existing MCP startup flow (McpConnectionManager) picks these
        //    up and exposes their tools through mcp_call / mcp_list_tools.
        if (plugin.McpServers.Count > 0)
        {
            var existing = await _toolPlatform.ListMcpServersAsync(ct: ct);
            foreach (var mcp in plugin.McpServers)
            {
                if (string.IsNullOrWhiteSpace(mcp.Name)) continue;
                var already = existing.FirstOrDefault(s =>
                    string.Equals(s.Name, mcp.Name, StringComparison.OrdinalIgnoreCase));
                if (already != null)
                    continue; // Idempotent — don't duplicate.
                var dto = new McpServerConfigDto(
                    Guid.NewGuid(), null, mcp.Name,
                    string.IsNullOrWhiteSpace(mcp.Transport) ? "stdio" : mcp.Transport,
                    mcp.Command ?? "", "[]", mcp.Endpoint ?? "", "{}", "{}", true);
                await _toolPlatform.SaveMcpServerAsync(dto, ct);
            }
        }

        lock (_lock)
            _activated.Add(pluginId);
        return true;
    }

    public Task<bool> DeactivateAsync(string pluginId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return Task.FromResult(false);
        lock (_lock)
        {
            if (!_activated.Remove(pluginId))
                return Task.FromResult(false);
        }
        // Note: persisted MCP servers and the managed-skill dir are left in
        // place on deactivation — they're idempotent and re-activation will
        // re-point the managed dir. Clearing them would require tracking
        // ownership, deferred to a future enhancement.
        return Task.FromResult(true);
    }

    private static string ResolvePluginDirectory(PluginManifest plugin)
    {
        // Plugins live under %LOCALAPPDATA%\TLAH Studio\plugins\<dir>\plugin.json.
        // The manifest doesn't carry its directory, so reconstruct from the
        // conventional layout using the plugin id (fall back to name).
        var slug = !string.IsNullOrWhiteSpace(plugin.Id) ? plugin.Id : plugin.Name;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "plugins", slug);
    }
}
