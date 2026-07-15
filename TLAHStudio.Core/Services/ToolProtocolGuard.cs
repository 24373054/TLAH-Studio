using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

public sealed record ToolProtocolGuardIssue(
    string Code,
    string Summary,
    string Severity);

public sealed record ToolProtocolGuardResult(
    IReadOnlyList<MessagePayload> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    IReadOnlyList<ToolProtocolGuardIssue> Issues,
    string? RejectionReason = null)
{
    public bool IsRejected => !string.IsNullOrWhiteSpace(RejectionReason);
    public bool HasRepairs => Issues.Count > 0;
}

public static partial class ToolProtocolGuard
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "assistant",
        "system",
        "tool"
    };

    public static ToolProtocolGuardResult RepairForProvider(
        IReadOnlyList<MessagePayload> messages,
        IReadOnlyList<LlmToolDefinition> tools)
    {
        var issues = new List<ToolProtocolGuardIssue>();
        var safeTools = SanitizeToolDefinitions(tools, issues);
        var safeMessages = new List<MessagePayload>();
        var pendingToolIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            var role = NormalizeRole(message.Role, issues);
            var content = message.Content ?? string.Empty;

            if (pendingToolIds.Count > 0 &&
                !string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                FlushMissingToolResults(safeMessages, pendingToolIds, issues);
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                var safeCalls = new List<LlmToolCall>();
                foreach (var call in message.ToolCalls)
                {
                    var safeCall = SanitizeToolCall(call, issues);
                    if (safeCall == null)
                        continue;
                    safeCalls.Add(safeCall);
                    pendingToolIds.Add(safeCall.Id);
                }

                if (safeCalls.Count > 0)
                {
                    safeMessages.Add(new MessagePayload("assistant", content, ToolCalls: safeCalls));
                    continue;
                }

                issues.Add(new ToolProtocolGuardIssue(
                    "tool_calls_removed",
                    "An assistant message had tool calls, but every call was invalid and was removed.",
                    "warning"));
            }

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId) ||
                    !pendingToolIds.Remove(message.ToolCallId))
                {
                    FlushMissingToolResults(safeMessages, pendingToolIds, issues);
                    safeMessages.Add(new MessagePayload(
                        "user",
                        $"[tool result]\n{content}"));
                    issues.Add(new ToolProtocolGuardIssue(
                        "orphan_tool_result",
                        "A tool result did not match a prior assistant tool call and was converted to a user message.",
                        "repair"));
                }
                else
                {
                    safeMessages.Add(new MessagePayload("tool", content, message.ToolCallId));
                }

                continue;
            }

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                safeMessages.Add(new MessagePayload("user", $"[system]\n{content}"));
                issues.Add(new ToolProtocolGuardIssue(
                    "inline_system_message",
                    "A system-role history message was converted to user content because providers expect one top-level system prompt.",
                    "repair"));
                continue;
            }

            safeMessages.Add(new MessagePayload(role, content));
        }

        FlushMissingToolResults(safeMessages, pendingToolIds, issues);

        if (safeMessages.Count == 0)
        {
            return new ToolProtocolGuardResult(
                safeMessages,
                safeTools,
                issues,
                "The request does not contain any provider-safe messages.");
        }

        return new ToolProtocolGuardResult(safeMessages, safeTools, issues);
    }

    public static LlmToolCall? SanitizeToolCall(
        LlmToolCall call,
        ICollection<ToolProtocolGuardIssue>? issues = null)
    {
        var name = AgentToolNames.Normalize(call.Name.Trim());
        if (!ValidToolNameRegex().IsMatch(name))
        {
            issues?.Add(new ToolProtocolGuardIssue(
                "invalid_tool_name",
                $"The model requested an invalid tool name: {call.Name}",
                "error"));
            return null;
        }

        var id = string.IsNullOrWhiteSpace(call.Id)
            ? $"call-{Guid.NewGuid():N}"
            : call.Id.Trim();
        if (!string.Equals(id, call.Id, StringComparison.Ordinal))
        {
            issues?.Add(new ToolProtocolGuardIssue(
                "tool_call_id_repaired",
                "A missing or whitespace tool call id was replaced before sending provider history.",
                "repair"));
        }

        var argumentsJson = NormalizeArgumentObject(call.ArgumentsJson, issues);
        if (!string.Equals(name, call.Name, StringComparison.Ordinal))
        {
            issues?.Add(new ToolProtocolGuardIssue(
                "tool_name_normalized",
                $"Tool name '{call.Name}' was normalized to '{name}'.",
                "repair"));
        }

        return new LlmToolCall(id, name, argumentsJson);
    }

    private static IReadOnlyList<LlmToolDefinition> SanitizeToolDefinitions(
        IReadOnlyList<LlmToolDefinition> tools,
        ICollection<ToolProtocolGuardIssue> issues)
    {
        var safe = new List<LlmToolDefinition>();
        foreach (var tool in tools)
        {
            var name = AgentToolNames.Normalize(tool.Name.Trim());
            if (!ValidToolNameRegex().IsMatch(name))
            {
                issues.Add(new ToolProtocolGuardIssue(
                    "tool_definition_dropped",
                    $"Tool definition '{tool.Name}' was dropped because its name is not provider-safe.",
                    "error"));
                continue;
            }

            safe.Add(string.Equals(name, tool.Name, StringComparison.Ordinal)
                ? tool
                : tool with { Name = name });
        }

        return safe;
    }

    private static string NormalizeRole(
        string role,
        ICollection<ToolProtocolGuardIssue> issues)
    {
        var normalized = role.Trim().ToLowerInvariant();
        if (AllowedRoles.Contains(normalized))
            return normalized;

        issues.Add(new ToolProtocolGuardIssue(
            "invalid_role",
            $"Message role '{role}' is not supported and was converted to user.",
            "repair"));
        return "user";
    }

    private static string NormalizeArgumentObject(
        string argumentsJson,
        ICollection<ToolProtocolGuardIssue>? issues)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            issues?.Add(new ToolProtocolGuardIssue(
                "empty_tool_arguments",
                "The tool call has empty arguments and will be returned to the model as a protocol error.",
                "error"));
            return argumentsJson;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
        }

        issues?.Add(new ToolProtocolGuardIssue(
            "invalid_tool_arguments",
            "Tool arguments are not a valid JSON object. They were preserved so the tool lifecycle can return an explicit protocol error instead of executing different arguments.",
            "error"));
        return argumentsJson;
    }

    private static void FlushMissingToolResults(
        ICollection<MessagePayload> safeMessages,
        ICollection<string> pendingToolIds,
        ICollection<ToolProtocolGuardIssue> issues)
    {
        if (pendingToolIds.Count == 0)
            return;

        foreach (var id in pendingToolIds.ToArray())
        {
            safeMessages.Add(new MessagePayload(
                "tool",
                JsonSerializer.Serialize(new
                {
                    success = false,
                    output = string.Empty,
                    error = "Tool result was not recorded for this tool call; history was repaired before the next model request."
                }),
                id));
            pendingToolIds.Remove(id);
        }

        issues.Add(new ToolProtocolGuardIssue(
            "missing_tool_result_repaired",
            "One or more assistant tool calls were missing matching tool messages and were repaired before contacting the provider.",
            "repair"));
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidToolNameRegex();
}
