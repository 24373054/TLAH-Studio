using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TLAHStudio.Core.Services.Plugins;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.5 Phase E2: Aggregates slash commands from four sources into a single
/// ordered list for the input-box completion UI:
///   1. Built-in chat commands (clear/new/agent/stop/help/regenerate/settings)
///   2. Skills (ISkillLoader) — /<skill-id>
///   3. Agent tools (IAgentToolRegistry) — /<tool-name>
///   4. MCP tools (IMcpClientService) — /mcp:<server>:<tool>
///
/// Ordering: BuiltIn > Skill > Tool > MCP, then alphabetical within each.
/// Duplicate names keep the higher-priority source.
/// </summary>
public sealed class SlashCommandService : ISlashCommandProvider
{
    private static readonly SlashCommand[] BuiltIns = new[]
    {
        new SlashCommand("clear", "Clear the current conversation", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("new", "Start a new conversation", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("agent", "Toggle agent mode on/off", "Chat", "[on|off]", "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("stop", "Stop the active generation / agent run", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("regenerate", "Regenerate the last assistant response", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("settings", "Open settings", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
        new SlashCommand("help", "Show available slash commands", "Chat", null, "builtin", SlashCommandKind.BuiltIn),
    };

    private readonly ISkillLoader _skills;
    private readonly IAgentToolRegistry _tools;
    private readonly IMcpClientService _mcp;

    public SlashCommandService(ISkillLoader skills, IAgentToolRegistry tools, IMcpClientService mcp)
    {
        _skills = skills;
        _tools = tools;
        _mcp = mcp;
    }

    public async Task<IReadOnlyList<SlashCommand>> GetCommandsAsync(Guid chatId, CancellationToken ct = default)
    {
        var byName = new Dictionary<string, SlashCommand>();

        // 1. Built-ins (highest priority).
        foreach (var c in BuiltIns)
            byName[c.Name] = c;

        // 2. Skills.
        try
        {
            var skills = await _skills.LoadSkillsAsync(ct);
            if (skills != null)
            {
                foreach (var s in skills)
                {
                    var name = s.Id;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!byName.ContainsKey(name))
                        byName[name] = new SlashCommand(
                            name,
                            string.IsNullOrWhiteSpace(s.Description) ? s.Name : s.Description,
                            "Skill",
                            null,
                            "skill",
                            SlashCommandKind.Skill);
                }
            }
        }
        catch { /* skills are optional; never block completion on them */ }

        // 3. Agent tools.
        try
        {
            var defs = _tools.Definitions;
            if (defs != null)
            {
                foreach (var d in defs)
                {
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    if (byName.ContainsKey(d.Name)) continue;
                    byName[d.Name] = new SlashCommand(
                        d.Name,
                        Truncate(d.Description, 80),
                        "Tool",
                        HintFromSchema(d.InputSchema),
                        "tool",
                        SlashCommandKind.Tool);
                }
            }
        }
        catch { }

        // 4. MCP tools (lowest priority, namespaced mcp:<server>:<tool>).
        try
        {
            var tools = await _mcp.ListToolsAsync(chatId, null, ct);
            if (tools != null)
            {
                foreach (var t in tools)
                {
                    var name = $"mcp:{t.Server}:{t.Name}";
                    if (byName.ContainsKey(name)) continue;
                    byName[name] = new SlashCommand(
                        name,
                        Truncate(t.Description, 80),
                        "MCP",
                        HintFromSchemaJson(t.InputSchema),
                        $"mcp:{t.Server}",
                        SlashCommandKind.Mcp);
                }
            }
        }
        catch { }

        return byName.Values
            .OrderBy(c => CategoryOrder(c.Category))
            .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Chat" => 0,
        "Skill" => 1,
        "Tool" => 2,
        "MCP" => 3,
        _ => 9
    };

    private static string? HintFromSchema(Dictionary<string, object>? schema)
    {
        if (schema == null) return null;
        // Best-effort: pull required property names from a JSON-schema-style dict.
        if (schema.TryGetValue("required", out var reqObj) && reqObj is System.Collections.IEnumerable req)
        {
            var names = new List<string>();
            foreach (var item in req)
                if (item is string s) names.Add(s);
            if (names.Count > 0)
                return "<" + string.Join("> <", names) + ">";
        }
        return null;
    }

    private static string? HintFromSchemaJson(System.Text.Json.JsonElement schema)
    {
        try
        {
            if (schema.ValueKind == System.Text.Json.JsonValueKind.Object &&
                schema.TryGetProperty("required", out var req) &&
                req.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var item in req.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        names.Add(item.GetString() ?? string.Empty);
                if (names.Count > 0)
                    return "<" + string.Join("> <", names) + ">";
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var clean = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}
