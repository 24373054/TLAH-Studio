using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services;

internal static class AgentToolParser
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParseSandboxCommand(string assistantText, out AgentToolRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(assistantText))
            return false;

        var json = ExtractJsonObject(assistantText);
        if (json == null)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var tool = ReadString(root, "tlah_tool")
                ?? ReadString(root, "tool")
                ?? ReadString(root, "name");
            if (!string.Equals(tool, "sandbox.exec", StringComparison.OrdinalIgnoreCase))
                return false;

            var command = ReadString(root, "command")
                ?? ReadString(root, "cmd")
                ?? ReadString(root, "input");
            if (string.IsNullOrWhiteSpace(command))
                return false;

            request = new AgentToolRequest(
                "sandbox.exec",
                command.Trim(),
                ReadString(root, "reason") ?? ReadString(root, "description") ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var fence = JsonFenceRegex.Match(text);
        if (fence.Success)
            return fence.Groups[1].Value;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed record AgentToolRequest(string Tool, string Command, string Reason);
