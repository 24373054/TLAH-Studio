using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services.Plugins;

/// <summary>
/// M2.12.0: Plugin trust levels.
/// </summary>
public enum PluginTrustLevel { Untrusted, Trusted, Partial }

/// <summary>
/// M2.12.0: Plugin manifest definition.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    string Author,
    PluginTrustLevel TrustLevel,
    IReadOnlyList<PluginToolDef> Tools,
    IReadOnlyList<string> Skills,
    IReadOnlyList<PluginMcpServerDef> McpServers);

public sealed record PluginToolDef(string Name, string Description, Dictionary<string, object> InputSchema);

public sealed record PluginMcpServerDef(string Name, string Transport, string Command, string Endpoint);

/// <summary>
/// M2.12.0: Plugin manifest discovery, loading, and trust management.
/// </summary>
public interface IPluginManifestService
{
    Task<IReadOnlyList<PluginManifest>> DiscoverPluginsAsync(CancellationToken ct = default);
    Task<PluginManifest?> LoadPluginAsync(string pluginPath, CancellationToken ct = default);
    Task TrustPluginAsync(string pluginId, CancellationToken ct = default);
    Task RevokeTrustAsync(string pluginId, CancellationToken ct = default);
    Task<IReadOnlyList<PluginManifest>> ListPluginsAsync(CancellationToken ct = default);
}

public class PluginManifestService : IPluginManifestService
{
    private readonly string _pluginsDir;

    public PluginManifestService()
    {
        _pluginsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "plugins");
    }

    public async Task<IReadOnlyList<PluginManifest>> DiscoverPluginsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_pluginsDir))
            return Array.Empty<PluginManifest>();

        var result = new List<PluginManifest>();
        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (File.Exists(manifestPath))
            {
                var manifest = await LoadPluginFromFileAsync(manifestPath, ct);
                if (manifest != null)
                    result.Add(manifest);
            }
        }
        return result;
    }

    public Task<PluginManifest?> LoadPluginAsync(string pluginPath, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(pluginPath, "plugin.json");
        return File.Exists(manifestPath)
            ? LoadPluginFromFileAsync(manifestPath, ct)
            : Task.FromResult<PluginManifest?>(null);
    }

    public async Task TrustPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var trustPath = Path.Combine(_pluginsDir, ".trusted.json");
        var trusted = File.Exists(trustPath)
            ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(trustPath, ct)) ?? []
            : [];
        trusted.Add(pluginId);
        await File.WriteAllTextAsync(trustPath, JsonSerializer.Serialize(trusted), ct);
    }

    public async Task RevokeTrustAsync(string pluginId, CancellationToken ct = default)
    {
        var trustPath = Path.Combine(_pluginsDir, ".trusted.json");
        if (!File.Exists(trustPath)) return;
        var trusted = JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(trustPath, ct)) ?? [];
        trusted.Remove(pluginId);
        await File.WriteAllTextAsync(trustPath, JsonSerializer.Serialize(trusted), ct);
    }

    public async Task<IReadOnlyList<PluginManifest>> ListPluginsAsync(CancellationToken ct = default)
    {
        var plugins = await DiscoverPluginsAsync(ct);
        var trustPath = Path.Combine(_pluginsDir, ".trusted.json");
        var trusted = File.Exists(trustPath)
            ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(trustPath, ct)) ?? []
            : [];

        return plugins.Select(p => p with
        {
            TrustLevel = trusted.Contains(p.Id) ? PluginTrustLevel.Trusted : PluginTrustLevel.Untrusted
        }).ToList();
    }

    private async Task<PluginManifest?> LoadPluginFromFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
            var name = root.GetProperty("name").GetString() ?? "Unknown";
            var version = root.GetProperty("version").GetString() ?? "0.0.0";
            var description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
            var author = root.TryGetProperty("author", out var auth) ? auth.GetString() ?? "" : "";

            var tools = new List<PluginToolDef>();
            if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in toolsEl.EnumerateArray())
                {
                    tools.Add(new PluginToolDef(
                        tool.GetProperty("name").GetString() ?? "",
                        tool.TryGetProperty("description", out var td) ? td.GetString() ?? "" : "",
                        tool.TryGetProperty("input_schema", out var schema)
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(schema.GetRawText()) ?? []
                            : []));
                }
            }

            var skills = new List<string>();
            if (root.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
                foreach (var s in skillsEl.EnumerateArray())
                    skills.Add(s.GetString() ?? "");

            var mcpServers = new List<PluginMcpServerDef>();
            if (root.TryGetProperty("mcp_servers", out var mcpEl) && mcpEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mcpEl.EnumerateArray())
                {
                    mcpServers.Add(new PluginMcpServerDef(
                        m.GetProperty("name").GetString() ?? "",
                        m.TryGetProperty("transport", out var t) ? t.GetString() ?? "stdio" : "stdio",
                        m.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                        m.TryGetProperty("endpoint", out var e) ? e.GetString() ?? "" : ""));
                }
            }

            return new PluginManifest(id, name, version, description, author,
                PluginTrustLevel.Untrusted, tools, skills, mcpServers);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// M2.12.0: Skill definition with trigger-based loading.
/// </summary>
public sealed record AgentSkill(
    string Id, string Name, string Description, string Content,
    IReadOnlyList<string> Triggers, string SourcePath,
    string WhenToUse = "",
    IReadOnlyList<string>? AllowedTools = null,
    string Model = "",
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<string>? Hooks = null);

/// <summary>
/// M2.12.0: Skill loader — discovers Markdown skills, matches by trigger keywords.
/// </summary>
public interface ISkillLoader
{
    Task<IReadOnlyList<AgentSkill>> LoadSkillsAsync(CancellationToken ct = default);
    Task<string> GetSkillContentAsync(string skillId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSkill>> FindRelevantSkillsAsync(
        string userMessage, int maxSkills = 3, CancellationToken ct = default);
}

public class SkillLoader : ISkillLoader
{
    private readonly string _skillsDir;
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline | RegexOptions.Compiled);

    public SkillLoader()
    {
        _skillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "skills");
    }

    public async Task<IReadOnlyList<AgentSkill>> LoadSkillsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_skillsDir))
            return Array.Empty<AgentSkill>();

        var skills = new List<AgentSkill>();
        foreach (var file in Directory.GetFiles(_skillsDir, "*.md"))
        {
            var skill = await LoadSkillFromFileAsync(file, ct);
            if (skill != null) skills.Add(skill);
        }
        return skills;
    }

    public async Task<string> GetSkillContentAsync(string skillId, CancellationToken ct = default)
    {
        var skills = await LoadSkillsAsync(ct);
        var skill = skills.FirstOrDefault(s => s.Id == skillId);
        return skill?.Content ?? string.Empty;
    }

    public async Task<IReadOnlyList<AgentSkill>> FindRelevantSkillsAsync(
        string userMessage, int maxSkills = 3, CancellationToken ct = default)
    {
        var skills = await LoadSkillsAsync(ct);
        var lower = userMessage.ToLowerInvariant();
        return skills
            .Where(s => s.Triggers.Any(t => lower.Contains(t.ToLowerInvariant())) ||
                        (!string.IsNullOrWhiteSpace(s.WhenToUse) &&
                         lower.Contains(s.WhenToUse.ToLowerInvariant())) ||
                        lower.Contains(s.Name.ToLowerInvariant()))
            .Take(maxSkills)
            .ToList();
    }

    private static async Task<AgentSkill?> LoadSkillFromFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, ct);
            var match = FrontmatterRegex.Match(content);
            if (!match.Success) return null;

            var fm = match.Groups[1].Value;
            var name = ExtractField(fm, "name") ?? Path.GetFileNameWithoutExtension(path);
            var description = ExtractField(fm, "description") ?? "";
            var triggersStr = ExtractField(fm, "triggers") ?? "";
            var triggers = SplitList(triggersStr);
            var whenToUse = ExtractField(fm, "when_to_use") ?? "";
            var allowedTools = SplitList(ExtractField(fm, "allowed_tools") ?? "");
            var model = ExtractField(fm, "model") ?? "";
            var paths = SplitList(ExtractField(fm, "paths") ?? "");
            var hooks = SplitList(ExtractField(fm, "hooks") ?? "");
            var body = FrontmatterRegex.Replace(content, "").Trim();

            return new AgentSkill(
                name.ToLowerInvariant().Replace(' ', '-'),
                name,
                description,
                body,
                triggers,
                path,
                whenToUse,
                allowedTools,
                model,
                paths,
                hooks);
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    private static string? ExtractField(string frontmatter, string field)
    {
        var match = Regex.Match(frontmatter, $@"{field}:\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
