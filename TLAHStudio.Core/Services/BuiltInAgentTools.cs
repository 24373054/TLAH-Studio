using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;

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

    public static string ResolveSandboxPath(ISandboxCommandService sandbox, Guid chatId, string relativePath)
    {
        relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = ".";
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Absolute paths are not allowed.");

        var root = Path.GetFullPath(sandbox.GetSandboxRoot(chatId))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested path escapes the chat sandbox.");
        return full;
    }

    public static LlmToolDefinition Definition(
        string name,
        string description,
        Dictionary<string, object> properties,
        string[]? required = null) =>
        new(name, description, new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required ?? [],
            ["additionalProperties"] = false
        });

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

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" or ".ps1" or ".cs" or ".js" or ".ts" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
}

public sealed class TerminalExecAgentTool : IAgentTool
{
    private readonly IExecutionBackendRouter _router;

    public TerminalExecAgentTool(IExecutionBackendRouter router)
    {
        _router = router;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TerminalExec,
        "Execute a command through the configured restricted local, WSL2, Docker, or remote sandbox backend.",
        new Dictionary<string, object>
        {
            ["command"] = AgentToolSupport.StringProperty("The command to run inside the isolated chat workspace."),
            ["backend"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[]
                {
                    "restricted_local", "wsl", "docker", "remote"
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
                context.ChatId, command, context.TimeoutSeconds, context.MaxOutputChars),
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
        return new AgentToolResult(
            result.Success,
            output,
            result.BlockedReason ?? (result.TimedOut ? "Execution timed out." : result.ExitCode == 0 ? null : "Command failed."));
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
                _sandbox, context.ChatId, AgentToolSupport.GetString(root, "path", "."));
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

    public FileReadAgentTool(ISandboxCommandService sandbox, IToolPlatformService platform)
    {
        _sandbox = sandbox;
        _platform = platform;
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
                _sandbox, context.ChatId, AgentToolSupport.GetString(root, "path"));
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");
            var settings = await _platform.GetSettingsAsync(ct);
            var info = new FileInfo(path);
            if (info.Length > settings.MaxFileBytes)
                return new AgentToolResult(false, string.Empty, $"File exceeds the {settings.MaxFileBytes}-byte limit.");
            var content = await File.ReadAllTextAsync(path, ct);
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

    public FileWriteAgentTool(ISandboxCommandService sandbox, IToolPlatformService platform)
    {
        _sandbox = sandbox;
        _platform = platform;
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
                _sandbox, context.ChatId, AgentToolSupport.GetString(root, "path"));
            var content = AgentToolSupport.GetString(root, "content");
            var append = root.TryGetProperty("append", out var appendValue) &&
                         appendValue.ValueKind == JsonValueKind.True;
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
        "Search filenames and UTF-8 text content inside the current chat sandbox.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Case-insensitive search text."),
            ["path"] = AgentToolSupport.StringProperty("Relative directory path. Defaults to the sandbox root."),
            ["glob"] = AgentToolSupport.StringProperty("Optional filename pattern such as *.cs.")
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
            var directory = AgentToolSupport.ResolveSandboxPath(
                _sandbox, context.ChatId, AgentToolSupport.GetString(root, "path", "."));
            var glob = AgentToolSupport.GetString(root, "glob", "*");
            var settings = await _platform.GetSettingsAsync(ct);
            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var matches = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, glob, SearchOption.AllDirectories).Take(500))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sandboxRoot, file);
                if (relative.Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add($"{relative}: filename match");
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > settings.MaxFileBytes)
                    continue;
                string content;
                try { content = await File.ReadAllTextAsync(file, ct); }
                catch { continue; }
                var lines = content.Split('\n');
                for (var i = 0; i < lines.Length && matches.Count < 200; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        matches.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
                if (matches.Count >= 200)
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
}

public sealed class GitAgentTool : IAgentTool
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "init", "add", "commit", "branch"
    };
    private readonly ISandboxCommandService _sandbox;

    public GitAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.Git,
        "Run a constrained Git operation in the current chat sandbox repository.",
        new Dictionary<string, object>
        {
            ["operation"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = AllowedOperations.OrderBy(x => x).ToArray(),
                ["description"] = "Git operation."
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
        if (!AllowedOperations.Contains(operation))
            return new AgentToolResult(false, string.Empty, $"Git operation is not allowed: {operation}");

        var args = new List<string>
        {
            "-c", "core.hooksPath=NUL",
            "-c", "protocol.file.allow=never",
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

        var psi = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = _sandbox.GetSandboxRoot(context.ChatId),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
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
            return new AgentToolResult(false, string.Empty, "Git operation timed out.");
        }
    }
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
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var settings = await _platform.GetSettingsAsync(ct);
            var uri = await _network.ValidateAsync(AgentToolSupport.GetString(root, "url"), settings, ct);
            var methodName = AgentToolSupport.GetString(root, "method", "GET").ToUpperInvariant();
            var method = new HttpMethod(methodName);
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

    public WebSearchAgentTool(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.WebSearch,
        "Search the public web through DuckDuckGo and return a bounded list of result titles, URLs, and snippets.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Search query."),
            ["reason"] = AgentToolSupport.StringProperty("Why web search is needed.")
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
            var settings = await _platform.GetSettingsAsync(ct);
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var uri = await _network.ValidateAsync(url, settings, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TLAHStudio/1.4");
            using var response = await _httpClientFactory.CreateClient("Tools").SendAsync(request, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            var results = ResultRegex().Matches(html).Cast<Match>().Take(8)
                .Select((match, index) =>
                {
                    var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                    var title = CleanHtml(match.Groups["title"].Value);
                    return $"{index + 1}. {title}\n   {href}";
                });
            var output = string.Join(Environment.NewLine + Environment.NewLine, results);
            return new AgentToolResult(
                response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(output),
                AgentToolSupport.Limit(output, context.MaxOutputChars),
                response.IsSuccessStatusCode
                    ? string.IsNullOrWhiteSpace(output) ? "No search results were parsed." : null
                    : $"Search returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string CleanHtml(string value) =>
        WebUtility.HtmlDecode(TagRegex().Replace(value, string.Empty)).Trim();

    [GeneratedRegex("""<a[^>]+class="result__a"[^>]+href="(?<url>[^"]+)"[^>]*>(?<title>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}

public sealed partial class BrowserReadAgentTool : IAgentTool
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;

    public BrowserReadAgentTool(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.BrowserRead,
        "Fetch an allowlisted public HTTPS page and extract readable text and links without running scripts.",
        new Dictionary<string, object>
        {
            ["url"] = AgentToolSupport.StringProperty("Absolute HTTPS page URL."),
            ["reason"] = AgentToolSupport.StringProperty("Why this page is needed.")
        },
        ["url"]);

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
            var settings = await _platform.GetSettingsAsync(ct);
            var uri = await _network.ValidateAsync(AgentToolSupport.GetString(root, "url"), settings, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TLAHStudio/1.4");
            using var response = await _httpClientFactory.CreateClient("Tools")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return new AgentToolResult(false, string.Empty, $"Unsupported browser content type: {contentType}");

            var html = await response.Content.ReadAsStringAsync(ct);
            html = ScriptStyleRegex().Replace(html, " ");
            var links = LinkRegex().Matches(html).Cast<Match>().Take(40)
                .Select(m =>
                {
                    var text = CleanText(m.Groups["text"].Value);
                    var href = WebUtility.HtmlDecode(m.Groups["url"].Value);
                    return string.IsNullOrWhiteSpace(text) ? href : $"{text}: {href}";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x));
            var textContent = CleanText(TagRegex().Replace(html, " "));
            var output = $"""
                URL: {uri}
                HTTP: {(int)response.StatusCode}

                Text:
                {textContent}

                Links:
                {string.Join(Environment.NewLine, links)}
                """;
            return new AgentToolResult(
                response.IsSuccessStatusCode,
                AgentToolSupport.Limit(output, Math.Min(context.MaxOutputChars, settings.MaxOutputChars)),
                response.IsSuccessStatusCode ? null : $"Browser request returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
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
