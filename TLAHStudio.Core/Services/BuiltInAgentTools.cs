using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.Research;
using TLAHStudio.Core.Services.Tools;

namespace TLAHStudio.Core.Services;

internal static class AgentToolSupport
{
    public static bool TryParse(string json, out JsonElement root, out string? error)
    {
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            root = default;
            error = $"Invalid tool arguments: {ex.Message}";
            return false;
        }
    }

    public static string GetString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static string ResolveSandboxPath(ISandboxCommandService sandbox, Guid chatId, string relativePath) =>
        ResolveSandboxPath(sandbox, chatId, relativePath, AgentPermissionModes.RequestApproval);

    public static string ResolveSandboxPath(
        ISandboxCommandService sandbox,
        Guid chatId,
        string relativePath,
        string? permissionMode)
    {
        relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = ".";

        var root = Path.GetFullPath(sandbox.GetSandboxRoot(chatId))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(root, relativePath));
        var isOutsideSandbox = !full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                               !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (isOutsideSandbox && AgentPermissionModes.IsBypass(permissionMode))
            return full;

        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Absolute paths are not allowed outside Full access or an exact approved invocation.");
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested path escapes the chat sandbox.");
        return full;
    }

    public static LlmToolDefinition Definition(
        string name,
        string description,
        Dictionary<string, object> properties,
        string[]? required = null)
    {
        // `reason` is runtime metadata consumed by the activity timeline and
        // approval UI.  Older built-in schemas accepted it implicitly, so the
        // strict schema validator must also advertise it explicitly instead
        // of rejecting otherwise valid model calls.
        var declaredProperties = new Dictionary<string, object>(properties, StringComparer.Ordinal);
        declaredProperties.TryAdd(
            "reason",
            StringProperty("Briefly explain why this tool invocation is needed."));

        return new(name, description, new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = declaredProperties,
            ["required"] = required ?? [],
            ["additionalProperties"] = false
        });
    }

    public static Dictionary<string, object> StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    public static Dictionary<string, object> BooleanProperty(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    public static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[output truncated]";

    public static async Task<AgentToolArtifact> ArtifactAsync(
        string root,
        string path,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return new AgentToolArtifact(
            Path.GetRelativePath(root, path),
            ContentType(path),
            info.Length,
            hash);
    }

    public static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" or ".ps1" or ".cs" or ".js" or ".ts" or ".py" or ".xml" or ".xaml" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            ".rar" => "application/vnd.rar",
            _ => "application/octet-stream"
        };
}

public sealed class TerminalExecAgentTool : IAgentTool
{
    private readonly IExecutionBackendRouter _router;
    private readonly IFlagLevelValidationService? _flagValidator;

    public TerminalExecAgentTool(IExecutionBackendRouter router, IFlagLevelValidationService? flagValidator = null)
    {
        _router = router;
        _flagValidator = flagValidator;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TerminalExec,
        "Execute a command through the configured local, WSL2, Docker, or remote backend. In Full access mode, local execution is unrestricted.",
        new Dictionary<string, object>
        {
            ["command"] = AgentToolSupport.StringProperty("The command to run inside the isolated chat workspace."),
            ["backend"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[]
                {
                    "restricted_local", "unrestricted_local", "wsl", "docker", "remote"
                },
                ["description"] = "Optional backend override. Omit it to use the configured default."
            },
            ["reason"] = AgentToolSupport.StringProperty("Why this execution is needed.")
        },
        ["command"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        var command = AgentToolSupport.GetString(root, "command");
        var backend = AgentToolSupport.GetString(root, "backend");
        if (string.IsNullOrWhiteSpace(command))
            return new AgentToolResult(false, string.Empty, "The command argument is required.");

        var result = await _router.ExecuteAsync(
            new ExecutionRequest(
                context.ChatId,
                command,
                context.TimeoutSeconds,
                context.MaxOutputChars,
                context.EffectivePermissionMode),
            backend,
            ct);
        var output = $"""
            Backend: {result.Backend}
            Exit code: {result.ExitCode}
            Timed out: {result.TimedOut}
            Duration: {result.Duration.TotalMilliseconds:F0} ms
            Working directory: {result.WorkingDirectory}

            stdout:
            {result.StandardOutput}

            stderr:
            {result.StandardError}
            """;
        // M4.6.1: Check destructive warnings even when using non-sandbox backends.
        string? destructiveWarning = null;
        if (_flagValidator != null)
        {
            foreach (var (pattern, warning) in FlagLevelValidationService.DestructiveWarnings)
            {
                if (pattern.IsMatch(command))
                {
                    destructiveWarning = warning;
                    break;
                }
            }
        }

        return new AgentToolResult(
            result.Success,
            output,
            result.BlockedReason ?? (result.TimedOut ? "Execution timed out." : result.ExitCode == 0 ? null : "Command failed."),
            Warning: destructiveWarning,
            OutcomeUncertain: result.OutcomeUncertain,
            MayHaveCommitted: result.MayHaveCommitted);
    }
}

public sealed class FileListAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public FileListAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileList,
        "List files and folders inside the current chat sandbox.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative directory path. Defaults to the sandbox root."),
            ["recursive"] = AgentToolSupport.BooleanProperty("Whether to include descendants.")
        });

    public bool RequiresApproval => true;

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return Task.FromResult(new AgentToolResult(false, string.Empty, error));
            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path", "."),
                context.EffectivePermissionMode);
            var recursive = root.TryGetProperty("recursive", out var recursiveValue) &&
                            recursiveValue.ValueKind == JsonValueKind.True;
            if (!Directory.Exists(path))
                return Task.FromResult(new AgentToolResult(false, string.Empty, "Directory not found."));

            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var entries = Directory.EnumerateFileSystemEntries(
                    path,
                    "*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Take(500)
                .Select(entry =>
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(sandboxRoot, entry);
                    return Directory.Exists(entry)
                        ? $"dir  {relative}"
                        : $"file {relative} ({new FileInfo(entry).Length} bytes)";
                });
            return Task.FromResult(new AgentToolResult(
                true,
                AgentToolSupport.Limit(string.Join(Environment.NewLine, entries), context.MaxOutputChars)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AgentToolResult(false, string.Empty, ex.Message));
        }
    }
}

public sealed class FileReadAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;
    private readonly IReadFileTracker? _readFileTracker;

    public FileReadAgentTool(ISandboxCommandService sandbox, IToolPlatformService platform, IReadFileTracker? readFileTracker = null)
    {
        _sandbox = sandbox;
        _platform = platform;
        _readFileTracker = readFileTracker;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileRead,
        "Read a UTF-8 text file from the current chat sandbox.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the sandbox.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");
            var settings = await _platform.GetSettingsAsync(ct);
            var info = new FileInfo(path);
            if (info.Length > settings.MaxFileBytes)
                return new AgentToolResult(false, string.Empty, $"File exceeds the {settings.MaxFileBytes}-byte limit.");
            var content = await File.ReadAllTextAsync(path, ct);
            // M4.5.0: Track the read for subsequent write/edit guards.
            _readFileTracker?.MarkRead(path, File.GetLastWriteTimeUtc(path));
            return new AgentToolResult(
                true,
                AgentToolSupport.Limit(content, Math.Min(context.MaxOutputChars, settings.MaxOutputChars)));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class FileWriteAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;
    private readonly IReadFileTracker? _readFileTracker;

    public FileWriteAgentTool(ISandboxCommandService sandbox, IToolPlatformService platform, IReadFileTracker? readFileTracker = null)
    {
        _sandbox = sandbox;
        _platform = platform;
        _readFileTracker = readFileTracker;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileWrite,
        "Create or replace a UTF-8 text file inside the current chat sandbox.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the sandbox."),
            ["content"] = AgentToolSupport.StringProperty("Complete UTF-8 file content."),
            ["append"] = AgentToolSupport.BooleanProperty("Append instead of replacing the file.")
        },
        ["path", "content"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            var content = AgentToolSupport.GetString(root, "content");
            var append = root.TryGetProperty("append", out var appendValue) &&
                         appendValue.ValueKind == JsonValueKind.True;
            var bypassGuards = AgentPermissionModes.IsBypass(context.EffectivePermissionMode);
            // M4.5.0: Require the file to have been read before writing.
            // This prevents the model from hallucinating file contents.
            if (!bypassGuards && File.Exists(path) && _readFileTracker != null && !_readFileTracker.WasRead(path))
                return new AgentToolResult(false, string.Empty,
                    $"Cannot write to '{AgentToolSupport.GetString(root, "path")}' — the file has not been read in this session. Use file_read or read first to inspect its contents.");
            // M4.5.0: Detect stale writes — file was modified externally after being read.
            if (!bypassGuards && File.Exists(path) && _readFileTracker != null)
            {
                var recordedMtime = _readFileTracker.GetLastReadMtimeUtc(path);
                var currentMtime = File.GetLastWriteTimeUtc(path);
                if (recordedMtime.HasValue && currentMtime != recordedMtime.Value)
                    return new AgentToolResult(false, string.Empty,
                        $"Cannot write to '{AgentToolSupport.GetString(root, "path")}' — the file was modified externally since it was last read. Re-read the file first.");
            }
            var settings = await _platform.GetSettingsAsync(ct);
            var bytes = Encoding.UTF8.GetByteCount(content);
            var existingBytes = append && File.Exists(path) ? new FileInfo(path).Length : 0;
            if (bytes + existingBytes > settings.MaxFileBytes)
                return new AgentToolResult(false, string.Empty, $"Write exceeds the {settings.MaxFileBytes}-byte file limit.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (append)
                await File.AppendAllTextAsync(path, content, Encoding.UTF8, ct);
            else
                await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
            var artifact = await AgentToolSupport.ArtifactAsync(
                _sandbox.GetSandboxRoot(context.ChatId), path, ct);
            return new AgentToolResult(
                true,
                $"Wrote {bytes} bytes to {artifact.RelativePath}.",
                Artifacts: [artifact]);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class FileSendAgentTool : IAgentTool
{
    private const long MaxSendBytes = 100L * 1024L * 1024L;
    private readonly ISandboxCommandService _sandbox;

    public FileSendAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileSend,
        "Send an existing sandbox file to the user as a visible chat attachment. Use this after creating an image, video, archive, text file, report, or any other file the user should be able to preview or download.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the current chat sandbox."),
            ["caption"] = AgentToolSupport.StringProperty("Optional short caption shown near the attachment.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);

            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");

            var info = new FileInfo(path);
            if (info.Length <= 0)
                return new AgentToolResult(false, string.Empty, "The selected file is empty.");
            if (info.Length > MaxSendBytes)
                return new AgentToolResult(false, string.Empty, $"File exceeds the {MaxSendBytes}-byte send limit.");

            var artifact = await AgentToolSupport.ArtifactAsync(
                _sandbox.GetSandboxRoot(context.ChatId),
                path,
                ct);
            var caption = AgentToolSupport.GetString(root, "caption").Trim();
            var output = string.IsNullOrWhiteSpace(caption)
                ? $"Sent {artifact.RelativePath} ({artifact.SizeBytes} bytes)."
                : $"Sent {artifact.RelativePath} ({artifact.SizeBytes} bytes).\nCaption: {caption}";

            return new AgentToolResult(true, output, Artifacts: [artifact]);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class FileInfoAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public FileInfoAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileInfo,
        "Inspect a sandbox file or directory: existence, kind, size, content type, SHA256, text encoding, newline style, and child counts.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path inside the current chat sandbox.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);

            var fullPath = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var relative = Path.GetRelativePath(sandboxRoot, fullPath);
            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                await using var stream = File.OpenRead(fullPath);
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                var textInfo = string.Empty;
                if (IsLikelyTextFile(fullPath) && info.Length <= 512 * 1024)
                {
                    var snapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(fullPath, ct);
                    textInfo = $"""
                        Encoding: {snapshot.Encoding.WebName}{(snapshot.HasBom ? " with BOM" : string.Empty)}
                        Newline: {WorkspaceCodeToolSupport.DisplayNewLine(snapshot.NewLine)}
                        Lines: {WorkspaceCodeToolSupport.SplitLines(snapshot.Content).Length}
                        """;
                }

                var output = $"""
                    Path: {relative}
                    Exists: True
                    Kind: file
                    Size bytes: {info.Length}
                    Content type: {AgentToolSupport.ContentType(fullPath)}
                    SHA256: {sha}
                    Last write UTC: {info.LastWriteTimeUtc:O}
                    {textInfo}
                    """;
                return new AgentToolResult(true, AgentToolSupport.Limit(output.TrimEnd(), context.MaxOutputChars));
            }

            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                var childFiles = 0;
                var childDirs = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(entry))
                        childDirs++;
                    else
                        childFiles++;
                }

                var output = $"""
                    Path: {relative}
                    Exists: True
                    Kind: directory
                    Child files: {childFiles}
                    Child directories: {childDirs}
                    Last write UTC: {directory.LastWriteTimeUtc:O}
                    """;
                return new AgentToolResult(true, AgentToolSupport.Limit(output.TrimEnd(), context.MaxOutputChars));
            }

            return new AgentToolResult(
                true,
                $"Path: {relative}{Environment.NewLine}Exists: False");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static bool IsLikelyTextFile(string path)
    {
        var contentType = AgentToolSupport.ContentType(path);
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            contentType is "application/json" or "image/svg+xml")
        {
            return true;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".csproj" or ".sln" or ".props" or ".targets" or ".config" or ".yaml" or ".yml" or ".toml";
    }
}

public sealed class FileMkdirAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public FileMkdirAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileMkdir,
        "Create a directory inside the current chat sandbox. Parent directories are created automatically.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative directory path inside the sandbox.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return Task.FromResult(new AgentToolResult(false, string.Empty, error));
            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            Directory.CreateDirectory(path);
            var relative = Path.GetRelativePath(_sandbox.GetSandboxRoot(context.ChatId), path);
            return Task.FromResult(new AgentToolResult(true, $"Created directory {relative}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new AgentToolResult(false, string.Empty, ex.Message));
        }
    }
}

public sealed class FileMoveAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public FileMoveAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileMove,
        "Move or copy a file or directory inside the current chat sandbox without shell commands.",
        new Dictionary<string, object>
        {
            ["from_path"] = AgentToolSupport.StringProperty("Relative source path inside the sandbox."),
            ["to_path"] = AgentToolSupport.StringProperty("Relative destination path inside the sandbox."),
            ["mode"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "move", "copy" },
                ["description"] = "Use move to rename/relocate, or copy to duplicate."
            },
            ["overwrite"] = AgentToolSupport.BooleanProperty("Overwrite an existing destination file. Directory copy may merge and overwrite files.")
        },
        ["from_path", "to_path"]);

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var source = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "from_path"),
                context.EffectivePermissionMode);
            var destination = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "to_path"),
                context.EffectivePermissionMode);
            var mode = AgentToolSupport.GetString(root, "mode", "move").Trim().ToLowerInvariant();
            var overwrite = root.TryGetProperty("overwrite", out var overwriteValue) &&
                            overwriteValue.ValueKind == JsonValueKind.True;
            if (mode is not ("move" or "copy"))
                return new AgentToolResult(false, string.Empty, "mode must be move or copy.");
            if (!File.Exists(source) && !Directory.Exists(source))
                return new AgentToolResult(false, string.Empty, "Source path was not found.");
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                return new AgentToolResult(false, string.Empty, "Source and destination are the same path.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var sourceIsDirectory = Directory.Exists(source);
            if (!sourceIsDirectory)
            {
                if (mode == "copy")
                    File.Copy(source, destination, overwrite);
                else
                    File.Move(source, destination, overwrite);
            }
            else if (mode == "move")
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                    return new AgentToolResult(false, string.Empty, "Directory move cannot overwrite an existing destination.");
                Directory.Move(source, destination);
            }
            else
            {
                if (Directory.Exists(destination) && !overwrite)
                    return new AgentToolResult(false, string.Empty, "Directory copy destination already exists. Set overwrite to true to merge and overwrite files.");
                await CopyDirectoryAsync(source, destination, overwrite, ct);
            }

            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var artifacts = new List<AgentToolArtifact>();
            if (File.Exists(destination))
                artifacts.Add(await AgentToolSupport.ArtifactAsync(sandboxRoot, destination, ct));
            var verb = mode == "copy" ? "Copied" : "Moved";
            var output = $"{verb} {Path.GetRelativePath(sandboxRoot, source)} to {Path.GetRelativePath(sandboxRoot, destination)}.";
            return new AgentToolResult(true, output, Artifacts: artifacts);
        }
        catch (IOException ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    public bool RequiresApproval => true;

    private static async Task CopyDirectoryAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var sourceStream = File.OpenRead(file);
            await using var targetStream = overwrite
                ? File.Create(target)
                : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(targetStream, ct);
        }
    }
}

public sealed class FileDeleteAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public FileDeleteAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileDelete,
        "Delete a file or directory. Host paths require Full access or approval for the exact invocation; critical host roots can never be recursively deleted.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path inside the sandbox."),
            ["recursive"] = AgentToolSupport.BooleanProperty("Delete directory contents recursively.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return Task.FromResult(new AgentToolResult(false, string.Empty, error));
            var recursive = root.TryGetProperty("recursive", out var recursiveValue) &&
                            recursiveValue.ValueKind == JsonValueKind.True;
            var path = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path"),
                context.EffectivePermissionMode);
            if (recursive && ToolSafetyKernel.IsImmutableRecursiveDeleteTarget(path))
            {
                return Task.FromResult(new AgentToolResult(
                    false,
                    string.Empty,
                    "Recursive deletion of a critical drive, Windows, Users, profile, or system-data root is blocked in every permission mode."));
            }
            var sandboxRoot = Path.GetFullPath(_sandbox.GetSandboxRoot(context.ChatId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedPath.Equals(sandboxRoot, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new AgentToolResult(false, string.Empty, "Deleting the sandbox root is blocked."));

            var relative = Path.GetRelativePath(sandboxRoot, path);
            if (File.Exists(path))
            {
                File.Delete(path);
                return Task.FromResult(new AgentToolResult(true, $"Deleted file {relative}."));
            }

            if (!Directory.Exists(path))
                return Task.FromResult(new AgentToolResult(true, $"Path {relative} did not exist."));

            var counts = CountDirectory(path);
            Directory.Delete(path, recursive);
            return Task.FromResult(new AgentToolResult(
                true,
                $"Deleted directory {relative}. Files: {counts.Files}. Directories: {counts.Directories}. Recursive: {recursive}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new AgentToolResult(false, string.Empty, ex.Message));
        }
    }

    private static (int Files, int Directories) CountDirectory(string path)
    {
        var files = 0;
        var directories = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
                directories++;
            else
                files++;
        }

        return (files, directories);
    }
}

public sealed class FileSearchAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;

    public FileSearchAgentTool(ISandboxCommandService sandbox, IToolPlatformService platform)
    {
        _sandbox = sandbox;
        _platform = platform;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.FileSearch,
        "Search filenames and UTF-8 text content inside the current chat sandbox using literal text or regex.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Text or regex to search for."),
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to the sandbox root."),
            ["glob"] = AgentToolSupport.StringProperty("Optional filename pattern such as *.cs."),
            ["regex"] = AgentToolSupport.BooleanProperty("Treat query as a .NET regular expression."),
            ["case_sensitive"] = AgentToolSupport.BooleanProperty("Use case-sensitive matching."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum matches to return." },
            ["include_binary"] = AgentToolSupport.BooleanProperty("Attempt to search binary-looking files. Defaults to false.")
        },
        ["query"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var query = AgentToolSupport.GetString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return new AgentToolResult(false, string.Empty, "The query argument is required.");
            var start = AgentToolSupport.ResolveSandboxPath(
                _sandbox,
                context.ChatId,
                AgentToolSupport.GetString(root, "path", "."),
                context.EffectivePermissionMode);
            var glob = AgentToolSupport.GetString(root, "glob", "*");
            var useRegex = root.TryGetProperty("regex", out var regexValue) &&
                           regexValue.ValueKind == JsonValueKind.True;
            var caseSensitive = root.TryGetProperty("case_sensitive", out var caseValue) &&
                                caseValue.ValueKind == JsonValueKind.True;
            var includeBinary = root.TryGetProperty("include_binary", out var binaryValue) &&
                                binaryValue.ValueKind == JsonValueKind.True;
            var maxResults = Math.Clamp(ReadInt(root, "max_results", 200), 1, 2000);
            Regex? regex = null;
            if (useRegex)
            {
                try
                {
                    regex = new Regex(
                        query,
                        RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                        TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException ex)
                {
                    return new AgentToolResult(false, string.Empty, $"Invalid regex: {ex.Message}");
                }
            }

            var settings = await _platform.GetSettingsAsync(ct);
            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var files = EnumerateSearchFiles(start, glob, ct).Take(10_000);
            var matches = new List<string>();
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (WorkspaceCodeToolSupport.ShouldSkipPath(file))
                    continue;
                if (!includeBinary && IsProbablyBinary(file))
                    continue;

                var relative = Path.GetRelativePath(sandboxRoot, file);
                var normalizedRelative = relative.Replace('\\', '/');
                if (Matches(normalizedRelative, query, regex, comparison))
                    matches.Add($"{relative}: filename match");
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > settings.MaxFileBytes)
                    continue;
                string content;
                try { content = await File.ReadAllTextAsync(file, ct); }
                catch { continue; }
                var lines = WorkspaceCodeToolSupport.SplitLines(content);
                for (var i = 0; i < lines.Length && matches.Count < maxResults; i++)
                {
                    if (Matches(lines[i], query, regex, comparison))
                        matches.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
                if (matches.Count >= maxResults)
                    break;
            }
            return new AgentToolResult(
                true,
                matches.Count == 0
                    ? "No matches."
                    : AgentToolSupport.Limit(string.Join(Environment.NewLine, matches), context.MaxOutputChars));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static bool Matches(string value, string query, Regex? regex, StringComparison comparison) =>
        regex?.IsMatch(value) ?? value.Contains(query, comparison);

    private static IEnumerable<string> EnumerateSearchFiles(string start, string glob, CancellationToken ct)
    {
        if (File.Exists(start))
            return [start];
        if (!Directory.Exists(start))
            return [];

        return EnumerateDirectory(start, glob, ct);
    }

    private static IEnumerable<string> EnumerateDirectory(string directory, string glob, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(directory, glob, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (WorkspaceCodeToolSupport.ShouldSkipPath(child))
                continue;
            foreach (var file in EnumerateDirectory(child, glob, ct))
                yield return file;
        }
    }

    private static bool IsProbablyBinary(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or
            ".zip" or ".7z" or ".rar" or ".dll" or ".exe" or ".pdb" or ".mp3" or ".mp4" or ".pdf";
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;
}

public sealed class GitAgentTool : IAgentTool
{
    private static readonly HashSet<string> SandboxOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "init", "add", "commit", "branch", "checkout", "switch"
    };
    private static readonly HashSet<string> AuthorizedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "fetch", "pull", "push", "merge", "rebase", "cherry-pick", "revert", "remote", "tag"
    };
    private static readonly string[] AllOperations = SandboxOperations
        .Concat(AuthorizedOperations)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private readonly ISandboxCommandService _sandbox;

    public GitAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.Git,
        "Run a structured Git operation. Remote and repository-integrating operations require Full access or approval for the exact invocation.",
        new Dictionary<string, object>
        {
            ["operation"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = AllOperations,
                ["description"] = "Git operation."
            },
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Relative path to the Git repository root inside the sandbox. Defaults to '.' (sandbox root). Use when .git is in a subdirectory."
            },
            ["arguments"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["description"] = "Additional Git arguments. Shell metacharacters are rejected."
            },
            ["reason"] = AgentToolSupport.StringProperty("Why the Git operation is needed.")
        },
        ["operation"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        var operation = AgentToolSupport.GetString(root, "operation");
        if (!SandboxOperations.Contains(operation) && !AuthorizedOperations.Contains(operation))
            return new AgentToolResult(false, string.Empty, $"Git operation is not allowed: {operation}");
        if (AuthorizedOperations.Contains(operation) &&
            !AgentPermissionModes.IsBypass(context.EffectivePermissionMode))
        {
            return new AgentToolResult(
                false,
                string.Empty,
                $"git {operation} requires Full access or approval for this exact invocation.");
        }

        // M4.4.1: Resolve working directory. Default to sandbox root; when `path`
        // is given, resolve it relative to the sandbox root and validate it stays
        // within the sandbox (no ../ escape).
        var sandboxRoot = Path.GetFullPath(_sandbox.GetSandboxRoot(context.ChatId));
        var repoPath = AgentToolSupport.GetString(root, "path");
        var workingDir = sandboxRoot;
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            string resolved;
            try
            {
                resolved = AgentToolSupport.ResolveSandboxPath(
                    _sandbox,
                    context.ChatId,
                    repoPath,
                    context.EffectivePermissionMode);
            }
            catch (Exception ex)
            {
                return new AgentToolResult(false, string.Empty, ex.Message);
            }
            if (!Directory.Exists(resolved))
                return new AgentToolResult(false, string.Empty, $"Git path does not exist or is not a directory: {repoPath}");
            workingDir = resolved;
        }

        var bypassRestrictions = AgentPermissionModes.IsBypass(context.EffectivePermissionMode);
        var args = new List<string>
        {
            "-c", "core.hooksPath=NUL",
            "-c", $"protocol.file.allow={(bypassRestrictions ? "always" : "never")}",
            "-c", "diff.external=",
            operation
        };
        if (root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in arguments.EnumerateArray())
            {
                var arg = value.GetString() ?? string.Empty;
                if (arg.IndexOfAny([';', '|', '&', '\r', '\n']) >= 0)
                    return new AgentToolResult(false, string.Empty, "Git arguments contain blocked shell metacharacters.");
                args.Add(arg);
            }
        }

        // M4.4.3: Prevent external diff spawn failures. Git inherits the
        // parent process environment, which may carry a stale GIT_EXTERNAL_DIFF
        // pointing to a non-existent tool. Appending --no-ext-diff ensures we
        // always use the built-in diff, matching terminal_exec behaviour.
        if (operation == "diff" && !args.Contains("--no-ext-diff"))
            args.Add("--no-ext-diff");

        // M4.4.3: Safety guard for checkout/switch.
        if (!bypassRestrictions && operation is "checkout" or "switch")
        {
            var userArgs = args.Skip(7).ToList(); // skip 6 config kv-pairs + operation

            // Block "checkout ." / "checkout -- ." — silently discards ALL
            // working-tree changes without listing which files are affected.
            if (userArgs.Count == 0 || userArgs is ["."] or ["--", "."] ||
                (userArgs.Count >= 2 && userArgs[^1] == "." && userArgs[^2] == "--"))
                return new AgentToolResult(false, string.Empty,
                    "git checkout/switch with '.' is blocked — it would discard all working-tree changes. Specify individual files or a branch name instead.");

            // Block force-flag without explicit target — the most dangerous case.
            // git checkout -f (no target) forces an implicit HEAD checkout, nuking
            // all local modifications without listing what's lost.
            var hasForce = userArgs.Any(a => a is "-f" or "--force");
            var hasTarget = userArgs.Any(a => !a.StartsWith('-'));
            if (hasForce && !hasTarget)
                return new AgentToolResult(false, string.Empty,
                    "git checkout/switch with -f/--force without a branch target is blocked. Specify the branch you want to switch to.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!bypassRestrictions)
        {
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        }
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) { return new AgentToolResult(false, string.Empty, $"Git is unavailable: {ex.Message}"); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(context.TimeoutSeconds, 1, 120)));
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new AgentToolResult(
                process.ExitCode == 0,
                AgentToolSupport.Limit(stdout, context.MaxOutputChars),
                process.ExitCode == 0 ? null : AgentToolSupport.Limit(stderr, context.MaxOutputChars));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return OutcomeUncertainFailure(
                "Git operation timed out; it may have partially changed the repository or remote.");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            try { process.Kill(true); } catch { }
            return OutcomeUncertainFailure(
                $"Git result transport failed: {SecretRedactor.RedactText(ex.Message)}");
        }
    }

    internal static AgentToolResult OutcomeUncertainFailure(string error) =>
        new(
            false,
            string.Empty,
            error,
            OutcomeUncertain: true,
            MayHaveCommitted: true);
}

public sealed class HttpRequestAgentTool : IAgentTool
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpRequestAgentTool(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.HttpRequest,
        "Make a bounded HTTPS request to an allowlisted public domain. Optional credentials are injected by name and never revealed to the model.",
        new Dictionary<string, object>
        {
            ["url"] = AgentToolSupport.StringProperty("Absolute HTTPS URL."),
            ["method"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" }
            },
            ["body"] = AgentToolSupport.StringProperty("Optional request body."),
            ["content_type"] = AgentToolSupport.StringProperty("Request content type. Defaults to application/json."),
            ["credential"] = AgentToolSupport.StringProperty("Optional credential broker entry name."),
            ["auth_scheme"] = AgentToolSupport.StringProperty("Authorization scheme. Defaults to Bearer."),
            ["reason"] = AgentToolSupport.StringProperty("Why the network request is needed.")
        },
        ["url"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var requestDispatched = false;
        var mutatingRequest = false;
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var settings = await _platform.GetSettingsAsync(ct);
            var uri = await _network.ValidateAsync(
                AgentToolSupport.GetString(root, "url"),
                settings,
                ct,
                bypassRestrictions: AgentPermissionModes.IsBypass(context.EffectivePermissionMode));
            var methodName = AgentToolSupport.GetString(root, "method", "GET").ToUpperInvariant();
            var method = new HttpMethod(methodName);
            mutatingRequest = method != HttpMethod.Get && method != HttpMethod.Head;
            using var request = new HttpRequestMessage(method, uri);
            var body = AgentToolSupport.GetString(root, "body");
            if (!string.IsNullOrEmpty(body) && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                if (Encoding.UTF8.GetByteCount(body) > settings.MaxFileBytes)
                    return new AgentToolResult(false, string.Empty, "HTTP request body exceeds the configured file-size limit.");
                request.Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    AgentToolSupport.GetString(root, "content_type", "application/json"));
            }

            var credentialName = AgentToolSupport.GetString(root, "credential");
            string? secret = null;
            if (!string.IsNullOrWhiteSpace(credentialName))
            {
                secret = await _platform.ResolveCredentialAsync(
                    credentialName, AgentToolNames.HttpRequest, uri.IdnHost, ct);
                if (string.IsNullOrWhiteSpace(secret))
                    return new AgentToolResult(false, string.Empty, "Credential is unavailable or not permitted for this tool and domain.");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    AgentToolSupport.GetString(root, "auth_scheme", "Bearer"),
                    secret);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.MaxRuntimeSeconds));
            // From this boundary onward a timeout or broken response cannot
            // prove that a mutating request was not accepted by the server.
            requestDispatched = true;
            using var response = await _httpClientFactory.CreateClient("Tools")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var text = method == HttpMethod.Head
                ? string.Empty
                : await response.Content.ReadAsStringAsync(timeoutCts.Token);
            text = SecretRedactor.RedactText(text, secret);
            var headers = string.Join(
                Environment.NewLine,
                response.Headers.Concat(response.Content.Headers)
                    .Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
            var output = $"""
                HTTP {(int)response.StatusCode} {response.ReasonPhrase}
                URL: {uri}

                Headers:
                {headers}

                Body:
                {AgentToolSupport.Limit(text, Math.Min(context.MaxOutputChars, settings.MaxOutputChars))}
                """;
            return new AgentToolResult(response.IsSuccessStatusCode, output,
                response.IsSuccessStatusCode ? null : $"HTTP request returned {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var uncertain = requestDispatched && mutatingRequest;
            return new AgentToolResult(
                false,
                string.Empty,
                "HTTP request timed out.",
                OutcomeUncertain: uncertain,
                MayHaveCommitted: uncertain);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            var uncertain = requestDispatched && mutatingRequest;
            return new AgentToolResult(
                false,
                string.Empty,
                SecretRedactor.RedactText(ex.Message),
                OutcomeUncertain: uncertain,
                MayHaveCommitted: uncertain);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, SecretRedactor.RedactText(ex.Message));
        }
    }
}

public sealed partial class WebSearchAgentTool : IAgentTool
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IResearchWorkbenchService _research;

    public WebSearchAgentTool(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
        _research = new ResearchWorkbenchService(platform, network, httpClientFactory);
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.WebSearch,
        "Search the public web with quick, balanced, or deep retrieval. Supports domain, language, and recency filters and returns deduplicated structured sources. Use research_verify when claims need cross-source evidence.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Search query."),
            ["reason"] = AgentToolSupport.StringProperty("Why web search is needed."),
            ["mode"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "quick", "balanced", "deep" },
                ["description"] = "Search breadth. Defaults to balanced."
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum deduplicated results (1-20)."
            },
            ["allowed_domains"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = AgentToolSupport.StringProperty("Domain such as docs.microsoft.com."),
                ["description"] = "Optional domains to include, including their subdomains."
            },
            ["blocked_domains"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = AgentToolSupport.StringProperty("Domain to exclude."),
                ["description"] = "Optional domains to exclude."
            },
            ["recency"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "any", "day", "week", "month", "year" },
                ["description"] = "Preferred publication recency."
            },
            ["language"] = AgentToolSupport.StringProperty("Optional language/locale, for example en-US or zh-CN.")
        },
        ["query"]) with
    {
        Namespace = "research",
        Category = "research",
        Strict = true,
        Deferred = true,
        InputExamples =
        [
            new Dictionary<string, object>
            {
                ["query"] = "Windows App SDK release notes",
                ["mode"] = "balanced",
                ["allowed_domains"] = new[] { "learn.microsoft.com", "github.com" },
                ["recency"] = "year"
            }
        ],
        OutputSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string" },
                ["mode"] = new Dictionary<string, object> { ["type"] = "string" },
                ["resultCount"] = new Dictionary<string, object> { ["type"] = "integer" },
                ["attempts"] = new Dictionary<string, object> { ["type"] = "integer" },
                ["sources"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                },
                ["failures"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                }
            },
            ["required"] = new[] { "query", "mode", "resultCount", "attempts", "sources", "failures" },
            ["additionalProperties"] = false
        },
        Annotations = new LlmToolAnnotations(
            ReadOnly: true,
            Destructive: false,
            Idempotent: true,
            OpenWorld: true,
            ConcurrencySafe: true)
    };

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var query = AgentToolSupport.GetString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return new AgentToolResult(false, string.Empty, "The query argument is required.");
            var mode = ReadMode(root, "mode", ResearchMode.Balanced);
            var filters = new ResearchFilters(
                ReadStringArray(root, "allowed_domains"),
                ReadStringArray(root, "blocked_domains"),
                ReadRecency(root, "recency"),
                AgentToolSupport.GetString(root, "language", string.Empty),
                Math.Clamp(ReadInt(root, "max_results", mode == ResearchMode.Deep ? 16 : 10), 1, 20));
            var result = await _research.SearchAsync(
                query,
                mode,
                filters,
                new ResearchWorkspace(context.EffectivePermissionMode, TimeoutSeconds: context.TimeoutSeconds),
                ct);
            var structured = new
            {
                query = result.Query,
                mode = result.Mode.ToString().ToLowerInvariant(),
                resultCount = result.Sources.Count,
                attempts = result.Attempts,
                sources = result.Sources.Select((source, index) => new
                {
                    citationId = $"search-{index + 1}",
                    index = index + 1,
                    source.Title,
                    url = source.Url.AbsoluteUri,
                    source.Domain,
                    source.Snippet,
                    publishedAt = source.PublishedAt?.ToString("O"),
                    provider = source.SearchProvider
                }),
                failures = result.Failures.Select(failure => new
                {
                    kind = failure.Kind.ToString(),
                    failure.Message,
                    url = failure.Url?.AbsoluteUri,
                    failure.HttpStatus,
                    failure.Retryable,
                    failure.Attempts
                })
            };
            var output = JsonSerializer.Serialize(
                structured,
                new JsonSerializerOptions { WriteIndented = true });
            var success = result.Sources.Count > 0 || result.Failures.Count == 0;
            var primaryFailure = result.Failures.FirstOrDefault();
            return new AgentToolResult(
                success,
                AgentToolSupport.Limit(output, context.MaxOutputChars),
                success ? null : primaryFailure?.Message ?? "Web search failed.",
                StructuredContent: structured,
                ErrorCode: success ? null : primaryFailure?.Kind.ToString().ToLowerInvariant() ?? "search_failed",
                Retryable: !success && primaryFailure?.Retryable == true,
                Sources: result.Sources.Select((source, index) =>
                    new AgentToolSource(
                        source.Url.AbsoluteUri,
                        source.Title,
                        source.SearchProvider,
                        DateTime.UtcNow,
                        $"search-{index + 1}")).ToArray(),
                DurationMs: stopwatch.ElapsedMilliseconds,
                Diagnostics: new Dictionary<string, object>
                {
                    ["attempts"] = result.Attempts,
                    ["result_count"] = result.Sources.Count
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                ex.Message,
                ErrorCode: "search_failed",
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
    }

    private static IEnumerable<string> BuildSearchUrls(string query)
    {
        var escaped = Uri.EscapeDataString(query);
        yield return $"https://html.duckduckgo.com/html/?q={escaped}";
        yield return $"https://lite.duckduckgo.com/lite/?q={escaped}";
    }

    private async Task<SearchFetchResult> FetchSearchPageAsync(
        HttpClient client,
        Uri uri,
        ToolPlatformSettings settings,
        bool bypassRestrictions,
        CancellationToken ct)
    {
        var current = uri;
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TLAHStudio/4.1; +https://matrixlabs.cn)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
            {
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                current = await _network.ValidateAsync(next.ToString(), settings, ct, bypassRestrictions);
                continue;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            return new SearchFetchResult(response.StatusCode, current, response.IsSuccessStatusCode, html);
        }

        return new SearchFetchResult(HttpStatusCode.TooManyRequests, current, false, string.Empty);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static IReadOnlyList<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();
        var matches = ResultRegex().Matches(html).Cast<Match>().ToArray();
        for (var i = 0; i < matches.Length; i++)
        {
            var match = matches[i];
            var segmentStart = match.Index + match.Length;
            var segmentEnd = i + 1 < matches.Length ? matches[i + 1].Index : html.Length;
            var segment = html.Substring(segmentStart, Math.Max(0, segmentEnd - segmentStart));
            var snippet = SnippetRegex().Match(segment).Groups["snippet"].Value;
            AddResult(results, match.Groups["title"].Value, match.Groups["url"].Value, snippet);
            if (results.Count >= maxResults)
                return results;
        }

        foreach (Match match in LiteResultRegex().Matches(html).Cast<Match>())
        {
            AddResult(results, match.Groups["title"].Value, match.Groups["url"].Value, string.Empty);
            if (results.Count >= maxResults)
                return results;
        }

        foreach (Match match in AnchorRegex().Matches(html).Cast<Match>())
        {
            var href = match.Groups["url"].Value;
            var normalizedUrl = NormalizeResultUrl(href);
            if (!IsUsefulSearchResultUrl(normalizedUrl))
                continue;
            AddResult(results, match.Groups["title"].Value, href, FindNearbySnippet(html, match.Index + match.Length));
            if (results.Count >= maxResults)
                return results;
        }

        return results;
    }

    private static void AddResult(List<SearchResult> results, string titleHtml, string rawUrl, string snippetHtml)
    {
        var title = CleanHtml(titleHtml);
        var url = NormalizeResultUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            return;
        if (results.Any(r => string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase)))
            return;
        results.Add(new SearchResult(title, url, CleanHtml(snippetHtml)));
    }

    private static string FormatResults(IReadOnlyList<SearchResult> results, int maxChars)
    {
        var lines = results.Select((result, index) =>
        {
            var snippet = string.IsNullOrWhiteSpace(result.Snippet)
                ? string.Empty
                : $"{Environment.NewLine}   {result.Snippet}";
            return $"{index + 1}. {result.Title}{Environment.NewLine}   {result.Url}{snippet}";
        });
        return AgentToolSupport.Limit(string.Join(Environment.NewLine + Environment.NewLine, lines), maxChars);
    }

    private static string NormalizeResultUrl(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Trim();
        if (decoded.StartsWith("//", StringComparison.Ordinal))
            decoded = "https:" + decoded;
        if (decoded.StartsWith("/l/", StringComparison.OrdinalIgnoreCase) ||
            decoded.StartsWith("/html/", StringComparison.OrdinalIgnoreCase))
            decoded = "https://duckduckgo.com" + decoded;
        if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            var uddg = ReadQueryValue(uri.Query, "uddg");
            if (!string.IsNullOrWhiteSpace(uddg))
                decoded = WebUtility.UrlDecode(uddg);
        }

        return decoded;
    }

    private static bool IsUsefulSearchResultUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return !value.Contains("/y.js?", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindNearbySnippet(string html, int start)
    {
        var length = Math.Min(800, Math.Max(0, html.Length - start));
        if (length == 0)
            return string.Empty;
        var segment = html.Substring(start, length);
        var snippet = SnippetRegex().Match(segment).Groups["snippet"].Value;
        if (!string.IsNullOrWhiteSpace(snippet))
            return snippet;
        return CleanHtml(segment);
    }

    private static string? ReadQueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 &&
                string.Equals(WebUtility.UrlDecode(parts[0]), name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.UrlDecode(parts[1]);
        }

        return null;
    }

    private static string CleanHtml(string value) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(value, " ")), " ").Trim();

    private sealed record SearchResult(string Title, string Url, string Snippet);

    private sealed record SearchFetchResult(HttpStatusCode StatusCode, Uri FinalUri, bool IsSuccess, string Html);

    [GeneratedRegex("""<a[^>]+class="[^"]*result__a[^"]*"[^>]+href="(?<url>[^"]+)"[^>]*>(?<title>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultRegex();

    [GeneratedRegex("""<(?:a|div)[^>]+class="[^"]*result__snippet[^"]*"[^>]*>(?<snippet>.*?)</(?:a|div)>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex("""<a[^>]+class="[^"]*(?:result-link|result__a)[^"]*"[^>]+href="(?<url>[^"]+)"[^>]*>(?<title>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LiteResultRegex();

    [GeneratedRegex("""<a[^>]+href=(?:"(?<url>[^"]+)"|'(?<url>[^']+)'|(?<url>[^\s>]+))[^>]*>(?<title>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Singleline)]
    private static partial Regex WhitespaceRegex();

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];

    private static ResearchMode ReadMode(JsonElement root, string name, ResearchMode fallback) =>
        Enum.TryParse<ResearchMode>(AgentToolSupport.GetString(root, name), true, out var parsed)
            ? parsed
            : fallback;

    private static ResearchRecency ReadRecency(JsonElement root, string name) =>
        Enum.TryParse<ResearchRecency>(AgentToolSupport.GetString(root, name), true, out var parsed)
            ? parsed
            : ResearchRecency.Any;
}

public sealed partial class BrowserReadAgentTool : IAgentTool
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IResearchWorkbenchService _research;

    public BrowserReadAgentTool(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
        _research = new ResearchWorkbenchService(platform, network, httpClientFactory);
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.BrowserRead,
        "Safely fetch a public page or PDF and extract its title, publication date, readable main content, and resolved links without running scripts.",
        new Dictionary<string, object>
        {
            ["url"] = AgentToolSupport.StringProperty("Absolute HTTPS page URL."),
            ["reason"] = AgentToolSupport.StringProperty("Why this page is needed."),
            ["query"] = AgentToolSupport.StringProperty("Optional topic used to identify the most relevant excerpt.")
        },
        ["url"]) with
    {
        Namespace = "research",
        Category = "research",
        Strict = true,
        Deferred = true,
        InputExamples =
        [
            new Dictionary<string, object>
            {
                ["url"] = "https://example.com/report",
                ["query"] = "key findings"
            }
        ],
        OutputSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["requestedUrl"] = new Dictionary<string, object> { ["type"] = "string" },
                ["finalUrl"] = new Dictionary<string, object> { ["type"] = "string" },
                ["httpStatus"] = new Dictionary<string, object> { ["type"] = "integer" },
                ["contentKind"] = new Dictionary<string, object> { ["type"] = "string" },
                ["title"] = new Dictionary<string, object> { ["type"] = "string" },
                ["text"] = new Dictionary<string, object> { ["type"] = "string" },
                ["links"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                }
            },
            ["required"] = new[] { "requestedUrl", "finalUrl", "httpStatus", "contentKind", "title", "text", "links" },
            ["additionalProperties"] = true
        },
        Annotations = new LlmToolAnnotations(
            ReadOnly: true,
            Destructive: false,
            Idempotent: true,
            OpenWorld: true,
            ConcurrencySafe: true)
    };

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var page = await _research.ReadPageAsync(
                AgentToolSupport.GetString(root, "url"),
                AgentToolSupport.GetString(root, "query"),
                new ResearchWorkspace(context.EffectivePermissionMode, TimeoutSeconds: context.TimeoutSeconds),
                ct);
            var excerpt = string.IsNullOrWhiteSpace(AgentToolSupport.GetString(root, "query"))
                ? string.Empty
                : ResearchContentExtractor.BuildExcerpt(
                    page.Text,
                    AgentToolSupport.GetString(root, "query"));
            var structured = new
            {
                requestedUrl = page.RequestedUrl.AbsoluteUri,
                finalUrl = page.FinalUrl.AbsoluteUri,
                page.HttpStatus,
                contentKind = page.ContentKind.ToString().ToLowerInvariant(),
                page.ContentType,
                page.Title,
                page.Description,
                page.Language,
                publishedAt = page.PublishedAt?.ToString("O"),
                page.Truncated,
                page.AttemptCount,
                relevantExcerpt = excerpt,
                text = page.Text,
                links = page.Links.Take(100).Select(link => new
                {
                    link.Text,
                    url = link.Url.AbsoluteUri
                })
            };
            var output = JsonSerializer.Serialize(
                structured,
                new JsonSerializerOptions { WriteIndented = true });
            return new AgentToolResult(
                true,
                AgentToolSupport.Limit(output, context.MaxOutputChars),
                StructuredContent: structured,
                Sources:
                [
                    new AgentToolSource(
                        page.FinalUrl.AbsoluteUri,
                        page.Title,
                        "browser_read",
                        DateTime.UtcNow,
                        "page-1")
                ],
                DurationMs: stopwatch.ElapsedMilliseconds,
                Diagnostics: new Dictionary<string, object>
                {
                    ["attempts"] = page.AttemptCount,
                    ["truncated"] = page.Truncated,
                    ["content_kind"] = page.ContentKind.ToString().ToLowerInvariant()
                });
        }
        catch (ResearchServiceException ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                $"[{ex.Failure.Kind}] {ex.Failure.Message} Retryable: {ex.Failure.Retryable}.",
                ErrorCode: ex.Failure.Kind.ToString().ToLowerInvariant(),
                Retryable: ex.Failure.Retryable,
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                ex.Message,
                ErrorCode: "browser_read_failed",
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
    }

    private static string CleanText(string value) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    [GeneratedRegex("<(script|style|noscript)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("""<a[^>]+href="(?<url>[^"]+)"[^>]*>(?<text>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
