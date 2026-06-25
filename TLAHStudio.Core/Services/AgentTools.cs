using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

public static class AgentToolNames
{
    public const string SandboxExec = "sandbox_exec";
    public const string LegacySandboxExec = "sandbox.exec";
    public const string TerminalExec = "terminal_exec";
    public const string FileList = "file_list";
    public const string FileRead = "file_read";
    public const string FileWrite = "file_write";
    public const string FileSearch = "file_search";
    public const string Git = "git";
    public const string HttpRequest = "http_request";
    public const string WebSearch = "web_search";
    public const string BrowserRead = "browser_read";
    public const string McpListTools = "mcp_list_tools";
    public const string McpCall = "mcp_call";

    public static string Normalize(string name) =>
        string.Equals(name, LegacySandboxExec, StringComparison.OrdinalIgnoreCase)
            ? SandboxExec
            : name;
}

public sealed record AgentToolExecutionContext(
    Guid ChatId,
    Guid AgentRunId,
    Guid InvocationId,
    int TimeoutSeconds,
    int MaxOutputChars);

public sealed record AgentToolArtifact(
    string RelativePath,
    string ContentType,
    long SizeBytes,
    string Sha256);

public sealed record AgentToolResult(
    bool Success,
    string Output,
    string? Error = null,
    IReadOnlyList<AgentToolArtifact>? Artifacts = null)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        success = Success,
        output = SecretRedactor.RedactText(Output),
        error = SecretRedactor.RedactText(Error ?? string.Empty),
        artifacts = Artifacts ?? []
    });
}

public interface IAgentTool
{
    LlmToolDefinition Definition { get; }
    bool RequiresApproval { get; }

    Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default);
}

public interface IAgentToolRegistry
{
    IReadOnlyList<LlmToolDefinition> Definitions { get; }
    bool TryGet(string name, out IAgentTool tool);
}

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly Regex ValidToolName = new(
        "^[a-zA-Z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, IAgentTool> _tools;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!ValidToolName.IsMatch(tool.Definition.Name))
            {
                throw new InvalidOperationException(
                    $"Agent tool name '{tool.Definition.Name}' is invalid. Tool names may only contain letters, numbers, underscores, and hyphens.");
            }
            _tools.Add(tool.Definition.Name, tool);
        }
    }

    public IReadOnlyList<LlmToolDefinition> Definitions =>
        _tools.Values.Select(t => t.Definition).ToArray();

    public bool TryGet(string name, out IAgentTool tool)
    {
        return _tools.TryGetValue(AgentToolNames.Normalize(name), out tool!);
    }
}

public sealed class SandboxExecAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public SandboxExecAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.SandboxExec,
        "Execute one restricted PowerShell command inside the chat sandbox. Host files, privileged operations, nested shells, and destructive system commands are blocked.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["command"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A PowerShell command that only reads or writes inside the sandbox working directory."
                },
                ["reason"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A short explanation of why this command is required."
                }
            },
            ["required"] = new[] { "command" },
            ["additionalProperties"] = false
        });

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        string command;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            command = document.RootElement.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            return new AgentToolResult(false, string.Empty, $"Invalid tool arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(command))
            return new AgentToolResult(false, string.Empty, "The command argument is required.");

        var result = await _sandbox.ExecuteAsync(
            context.ChatId,
            command,
            new SandboxCommandOptions(context.TimeoutSeconds, context.MaxOutputChars),
            ct);

        var artifacts = await DiscoverArtifactsAsync(context.ChatId, ct);
        var output = $"""
            Exit code: {result.ExitCode}
            Timed out: {result.TimedOut}
            Duration: {result.Duration.TotalMilliseconds:F0} ms
            Working directory: {result.WorkingDirectory}

            stdout:
            {result.StandardOutput}

            stderr:
            {result.StandardError}
            """;

        if (result.WasBlocked)
            output += $"\nBlocked: {result.BlockedReason}";

        return new AgentToolResult(
            !result.WasBlocked && !result.TimedOut && result.ExitCode == 0,
            output,
            result.WasBlocked ? result.BlockedReason : result.TimedOut ? "Command timed out." : null,
            artifacts);
    }

    private async Task<IReadOnlyList<AgentToolArtifact>> DiscoverArtifactsAsync(
        Guid chatId,
        CancellationToken ct)
    {
        var root = _sandbox.GetSandboxRoot(chatId);
        if (!Directory.Exists(root))
            return [];

        var artifacts = new List<AgentToolArtifact>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 100 * 1024 * 1024)
                continue;

            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            artifacts.Add(new AgentToolArtifact(
                Path.GetRelativePath(root, path),
                GetContentType(path),
                info.Length,
                hash));
        }

        return artifacts;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
}
