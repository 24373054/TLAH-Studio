using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Plugins;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.0: Invoke a named skill within the agent conversation.
/// Loads the skill body lazily via ISkillLoader and injects it as a user message.
/// Adopted from Claude Code's SkillTool.ts.
/// </summary>
public sealed class SkillAgentTool : IAgentTool
{
    private readonly ISkillLoader _skillLoader;

    public SkillAgentTool(ISkillLoader? skillLoader = null)
    {
        _skillLoader = skillLoader ?? new SkillLoader();
    }

    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.Skill,
        "Invoke a named skill. Skills provide specialized capabilities and domain knowledge. Available skills are listed in system-reminder messages. When a skill matches the user's request, invoke this tool BEFORE generating any other response.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = new List<string> { "skill" },
            ["properties"] = new Dictionary<string, object>
            {
                ["skill"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "The skill name to invoke."
                },
                ["args"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Optional arguments to pass to the skill."
                }
            }
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);

        if (!root.TryGetProperty("skill", out var skillProp))
            return new AgentToolResult(false, string.Empty, "skill name is required.");
        var skillName = skillProp.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(skillName))
            return new AgentToolResult(false, string.Empty, "skill name is required.");

        var content = await _skillLoader.GetSkillContentAsync(skillName, ct);
        if (string.IsNullOrWhiteSpace(content))
            return new AgentToolResult(false, string.Empty,
                $"Skill '{skillName}' not found or has no content. Use the skill listing in system reminders to see available skills.");

        var args = root.TryGetProperty("args", out var a) ? a.GetString() : null;
        var result = string.IsNullOrWhiteSpace(args)
            ? $"[skill: {skillName}]\n{content}\n[/skill]"
            : $"[skill: {skillName} args={args}]\n{content}\n[/skill]";

        return new AgentToolResult(true, result);
    }
}
