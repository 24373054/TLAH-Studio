using System.Text.Json;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.0: Structured multi-question tool — lets the model ask the user
/// clarifying questions through the approval UI.
/// Adopted from Claude Code's AskUserQuestionTool.tsx.
/// </summary>
public sealed class AskUserQuestionAgentTool : IAgentTool
{
    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.AskUserQuestion,
        "Ask the user structured questions to clarify the task. Use when you need to disambiguate requirements, choose between approaches, or confirm a decision before proceeding.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = new List<string> { "questions" },
            ["properties"] = new Dictionary<string, object>
            {
                ["questions"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "1-4 questions to ask the user.",
                    ["minItems"] = 1,
                    ["maxItems"] = 4,
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["required"] = new List<string> { "question", "header", "options" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["question"] = new Dictionary<string, object> { ["type"] = "string" },
                            ["header"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["description"] = "Very short label (max 12 chars)."
                            },
                            ["options"] = new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["minItems"] = 2,
                                ["maxItems"] = 4,
                                ["items"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["required"] = new List<string> { "label", "description" },
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["label"] = new Dictionary<string, object> { ["type"] = "string" },
                                        ["description"] = new Dictionary<string, object> { ["type"] = "string" },
                                        ["preview"] = new Dictionary<string, object>
                                        {
                                            ["type"] = "string",
                                            ["description"] = "Optional preview content (code snippet, layout, or example) shown in a monospace box to help the user compare this option against others. Max ~500 chars."
                                        }
                                    }
                                }
                            },
                            ["multiSelect"] = new Dictionary<string, object>
                            {
                                ["type"] = "boolean",
                                ["description"] = "Allow multiple options to be selected."
                            }
                        }
                    }
                }
            }
        });

    public bool RequiresApproval => true;

    public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        // M4.9.0: The arguments have been updated by the approval flow with user answers.
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("answers", out var answers))
            {
                var formatted = new System.Text.StringBuilder();
                foreach (var kv in answers.EnumerateObject())
                    formatted.AppendLine($"{kv.Name}: {kv.Value}");
                return Task.FromResult(new AgentToolResult(true, formatted.ToString().TrimEnd()));
            }
        }
        catch { }

        return Task.FromResult(new AgentToolResult(true,
            "Questions asked. Awaiting user response."));
    }
}
