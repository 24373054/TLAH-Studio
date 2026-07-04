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
    IReadOnlyList<string>? Hooks = null,
    string Source = "user");  // M4.9.0: "bundled" | "project" | "user" | "managed" | "conditional"

/// <summary>
/// M2.12.0: Skill loader — discovers Markdown skills, matches by trigger keywords.
/// </summary>
public interface ISkillLoader
{
    Task<IReadOnlyList<AgentSkill>> LoadSkillsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentSkill>> GetActiveSkillsAsync(CancellationToken ct = default);
    Task<string> GetSkillContentAsync(string skillId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSkill>> FindRelevantSkillsAsync(
        string userMessage, int maxSkills = 3, CancellationToken ct = default);
    /// <summary>
    /// M4.9.0: Activate conditional skills whose paths patterns match the given file path.
    /// Returns newly activated skills that should be added to the skill listing.
    /// </summary>
    Task<IReadOnlyList<AgentSkill>> ActivateConditionalSkillsForPathAsync(
        string filePath, CancellationToken ct = default);
    /// <summary>M4.9.0: Update workspace root for project-level skill discovery.</summary>
    void SetWorkspaceRoot(string? root);
    /// <summary>M4.9.2: Set managed (policy-level) skills directory, or null to disable.</summary>
    void SetManagedDir(string? dir);
    Task ReloadAsync(CancellationToken ct = default);
}

public class SkillLoader : ISkillLoader
{
    private string _userDir;
    private string? _projectDir;
    private string _bundledDir;
    private string? _managedDir;
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly Dictionary<string, AgentSkill> _conditionalSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AgentSkill> _activeConditional = [];

    public SkillLoader(string? workspaceRoot = null, string? bundledDir = null)
    {
        _userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "skills");
        SetWorkspaceRoot(workspaceRoot);
        _bundledDir = bundledDir ?? Path.Combine(
            AppContext.BaseDirectory, "Assets", "bundled-skills");
    }

    // ── Directory accessors (M4.9.0 hotfix) ──────────────────────

    /// <summary>Bundled skills directory (shipped with the app, read-only).</summary>
    public string BundledDir => _bundledDir;

    /// <summary>User skills directory (%LOCALAPPDATA%\TLAH Studio\skills).</summary>
    public string UserDir => _userDir;

    /// <summary>Project skills directory (<workspace>/.tlah/skills), or null.</summary>
    public string? ProjectDir => _projectDir;

    /// <summary>
    /// M4.9.2: Managed skills directory (policy-level, optional). When set,
    /// skills here sit between project and user in priority. Used for
    /// enterprise/team policy skills. Null by default.
    /// </summary>
    public string? ManagedDir => _managedDir;

    /// <summary>M4.9.2: Set the managed skills directory (policy-level).</summary>
    public void SetManagedDir(string? dir) => _managedDir = dir;

    /// <summary>Update the workspace root to load project-level skills.</summary>
    public void SetWorkspaceRoot(string? root)
    {
        _projectDir = root != null ? Path.Combine(root, ".tlah", "skills") : null;
    }

    // ── Multi-source loading (stateless — always rescans) ────────

    public async Task<IReadOnlyList<AgentSkill>> LoadSkillsAsync(CancellationToken ct = default)
    {
        var all = new List<AgentSkill>();
        _conditionalSkills.Clear();

        // Scan all sources fresh — no caching.
        // M4.9.2: Priority order — project > managed > user > bundled.
        // First source wins on name collision, so highest priority goes first.
        var sources = new List<(string? Dir, string Source)>
        {
            (_projectDir, "project"),
            (_managedDir, "managed"),
            (_userDir, "user"),
            (_bundledDir, "bundled"),
        };

        foreach (var (dir, source) in sources)
        {
            if (dir == null || !Directory.Exists(dir))
                continue;
            var skills = new List<AgentSkill>();
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                var s = await LoadSkillFileAsync(file, source, ct);
                if (s != null) skills.Add(s);
            }
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var skillMd = Path.Combine(sub, "SKILL.md");
                if (File.Exists(skillMd))
                {
                    var s = await LoadSkillFileAsync(skillMd, source, ct);
                    if (s != null) skills.Add(s);
                }
            }
            // Deduplicate: first source wins (project > managed > user > bundled).
            foreach (var s in skills)
            {
                if (!all.Any(e => string.Equals(e.Id, s.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    if (s.Paths is { Count: > 0 })
                        _conditionalSkills[s.Id] = s;
                    else
                        all.Add(s);
                }
            }
        }

        all.AddRange(_activeConditional);
        return all;
    }

    public async Task<IReadOnlyList<AgentSkill>> GetActiveSkillsAsync(CancellationToken ct = default)
    {
        return await LoadSkillsAsync(ct);
    }

    public async Task<string> GetSkillContentAsync(string skillId, CancellationToken ct = default)
    {
        // Check conditional first.
        if (_conditionalSkills.TryGetValue(skillId, out var cond))
            return cond.Content;
        foreach (var s in _activeConditional)
            if (string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase))
                return s.Content;
        // Reload and search.
        var all = await LoadSkillsAsync(ct);
        var skill = all.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase));
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

    // ── Conditional activation (M4.9.0) ───────────────────────────

    public Task<IReadOnlyList<AgentSkill>> ActivateConditionalSkillsForPathAsync(
        string filePath, CancellationToken ct = default)
    {
        var activated = new List<AgentSkill>();
        if (_conditionalSkills.Count == 0)
            return Task.FromResult<IReadOnlyList<AgentSkill>>(activated);

        var fileName = Path.GetFileName(filePath);
        var normalized = filePath.Replace('\\', '/');

        foreach (var (id, skill) in _conditionalSkills)
        {
            if (skill.Paths == null || skill.Paths.Count == 0) continue;
            foreach (var pattern in skill.Paths)
            {
                var glob = pattern.Replace('\\', '/');
                if (SimpleGlobMatch(glob, normalized) || SimpleGlobMatch(glob, fileName))
                {
                    _activeConditional.Add(skill);
                    _conditionalSkills.Remove(id);
                    activated.Add(skill);
                    break;
                }
            }
        }
        return Task.FromResult<IReadOnlyList<AgentSkill>>(activated);
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>Simple glob matcher supporting * and ** wildcards.</summary>
    private static bool SimpleGlobMatch(string pattern, string input)
    {
        // Convert glob to regex: ** → .+, * → [^/]+, ? → .
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".+")
            .Replace("\\*", "[^/]+")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
            RegexOptions.IgnoreCase);
    }

    // ── File loading ──────────────────────────────────────────────

    private async Task<AgentSkill?> LoadSkillFileAsync(string path, string source, CancellationToken ct)
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
                hooks,
                source);
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> SplitList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();
        // M4.9.2: Support both comma-separated scalar form ("a, b") and
        // YAML array form ('["a","b"]' or '["a", "b"]') for frontmatter
        // list fields like `paths` and `triggers`. Strip the surrounding
        // brackets/quotes so the parsed items are clean scalars.
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];
        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Trim('"', '\''))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    private static string? ExtractField(string frontmatter, string field)
    {
        var match = Regex.Match(frontmatter, $@"{field}:\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
