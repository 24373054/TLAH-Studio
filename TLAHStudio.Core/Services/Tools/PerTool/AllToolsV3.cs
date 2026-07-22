using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services.Tools.PerTool;

/// <summary>
/// M2.11.0: V3 implementations for all remaining agent tools.
/// Each tool provides per-tool safety classification, effect planning, and rollback support.
/// </summary>

// ── File tools ────────────────────────────────────────────────

public class FileListToolV3 : AgentToolV3Base
{
    private readonly FileListAgentTool _inner;
    public FileListToolV3(ISandboxCommandService sandbox) => _inner = new FileListAgentTool(sandbox);
    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        return await _inner.ExecuteAsync(context, argumentsJson, ct);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        return Task.FromResult(ToolEffectPlan.ReadOnly([args?.GetValueOrDefault("path", ".") ?? "."]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class FileReadToolV3 : AgentToolV3Base
{
    private readonly FileReadAgentTool _inner;
    public FileReadToolV3(ISandboxCommandService sandbox, IToolPlatformService platform) =>
        _inner = new FileReadAgentTool(sandbox, platform);
    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        return await _inner.ExecuteAsync(context, argumentsJson, ct);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var path = args?.GetValueOrDefault("path", "") ?? "";
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(sandbox.GetSandboxRoot(chatId), normalized));
        return Task.FromResult(ToolEffectPlan.ReadOnly([fullPath]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class FileWriteToolV3 : AgentToolV3Base
{
    private readonly FileWriteAgentTool _inner;
    public FileWriteToolV3(ISandboxCommandService sandbox, IToolPlatformService platform)
    {
        _inner = new FileWriteAgentTool(sandbox, platform);
    }
    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        return await _inner.ExecuteAsync(context, argumentsJson, ct);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(sandbox.GetSandboxRoot(chatId), normalized));
        return Task.FromResult(ToolEffectPlan.Write([fullPath], [fullPath]));
    }
    public override Task<ToolRollbackPlan?> CreateRollbackPlanAsync(string argumentsJson, AgentToolResult result, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        return Task.FromResult<ToolRollbackPlan?>(new ToolRollbackPlan(true, "Restore from backup or delete the file.", $"del \"{path}\"", [path]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
}

// ── Shell / Terminal tools ────────────────────────────────────

public class TerminalExecToolV3 : AgentToolV3Base
{
    private readonly IExecutionBackendRouter _router;
    public TerminalExecToolV3(IExecutionBackendRouter router) => _router = router;
    public override LlmToolDefinition Definition => new("terminal_exec", "Execute a shell command in the sandbox.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["command"] = new Dictionary<string, object> { ["type"] = "string" }, ["reason"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "command" } });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var command = doc.RootElement.GetProperty("command").GetString() ?? "";
        var result = await _router.ExecuteAsync(
            new ExecutionRequest(
                context.ChatId,
                command,
                context.TimeoutSeconds,
                context.MaxOutputChars,
                context.EffectivePermissionMode),
            ct: ct);
        var isFailure = CommandSemantics.IsExitCodeFailure(command, result.ExitCode);
        if (result.BlockedReason != null)
            return new AgentToolResult(
                false,
                result.StandardOutput,
                result.BlockedReason,
                OutcomeUncertain: result.OutcomeUncertain,
                MayHaveCommitted: result.MayHaveCommitted);
        return new AgentToolResult(
            !isFailure,
            SecretRedactor.RedactText(result.StandardOutput),
            result.StandardError,
            OutcomeUncertain: result.OutcomeUncertain,
            MayHaveCommitted: result.MayHaveCommitted);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var command = doc.RootElement.GetProperty("command").GetString() ?? "";
        return Task.FromResult(ToolEffectPlan.Command([command], CommandSemantics.IsDestructive(command) ? "high" : "medium"));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
}

// ── Git tool ───────────────────────────────────────────────────

public class GitToolV3 : AgentToolV3Base
{
    // Keep the future V3 registration on the same structured schema and the
    // same process-level execution path as the currently registered Git tool.
    // This is intentionally composition rather than a second Git command
    // implementation: exact approval and Full access must not be intercepted
    // again by the restricted sandbox after central authorization succeeds.
    private readonly GitAgentTool _inner;
    public GitToolV3(ISandboxCommandService sandbox) => _inner = new GitAgentTool(sandbox);

    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct) =>
        _inner.ExecuteAsync(context, argumentsJson, ct);

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var operation = root.TryGetProperty("operation", out var operationValue)
            ? operationValue.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;
        var arguments = root.TryGetProperty("arguments", out var argumentsValue) &&
                        argumentsValue.ValueKind == JsonValueKind.Array
            ? argumentsValue.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
            : [];
        var path = root.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString() ?? "."
            : ".";
        var readOnly = operation is "status" or "diff" or "log";
        var destructive = operation is "reset" or "clean";
        var highRisk = destructive || operation is
            "fetch" or "pull" or "push" or "merge" or "rebase" or
            "cherry-pick" or "revert" or "remote" or "tag";
        var renderedArguments = string.Join(" ", arguments.Select(value => JsonSerializer.Serialize(value)));
        var command = string.IsNullOrWhiteSpace(renderedArguments)
            ? $"git {operation}"
            : $"git {operation} {renderedArguments}";
        return Task.FromResult(new ToolEffectPlan(
            [path],
            readOnly ? [] : [path],
            [],
            [command],
            [],
            [],
            readOnly ? "low" : highRisk ? "high" : "medium",
            destructive,
            false));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
}

// ── HTTP / Web / Browser tools ────────────────────────────────

public class HttpRequestToolV3 : AgentToolV3Base
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"
    };

    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade"
    };

    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    public HttpRequestToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    { _platform = platform; _network = network; _httpClientFactory = httpClientFactory; }

    public override LlmToolDefinition Definition => AgentToolSupport.Definition(
        AgentToolNames.HttpRequest,
        "Make a bounded HTTP request after URL and permission validation.",
        new Dictionary<string, object>
        {
            ["url"] = AgentToolSupport.StringProperty("Absolute HTTP or HTTPS URL."),
            ["method"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = AllowedMethods.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            },
            ["body"] = AgentToolSupport.StringProperty("Optional request body. GET and HEAD requests cannot include a body."),
            ["content_type"] = AgentToolSupport.StringProperty("Request content type. Defaults to application/json."),
            ["headers"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["description"] = "Optional string-valued request headers. Transport-controlled headers are rejected.",
                ["additionalProperties"] = new Dictionary<string, object> { ["type"] = "string" }
            },
            ["credential"] = AgentToolSupport.StringProperty("Optional credential broker entry name."),
            ["auth_scheme"] = AgentToolSupport.StringProperty("Authorization scheme. Defaults to Bearer."),
            ["reason"] = AgentToolSupport.StringProperty("Why the network request is needed.")
        },
        ["url"]);
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var requestDispatched = false;
        var mutatingRequest = false;
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var parseError))
                return new AgentToolResult(false, string.Empty, parseError);

            var url = AgentToolSupport.GetString(root, "url");
            if (string.IsNullOrWhiteSpace(url))
                return new AgentToolResult(false, string.Empty, "The url argument is required.");

            var methodName = AgentToolSupport.GetString(root, "method", "GET").Trim().ToUpperInvariant();
            if (!AllowedMethods.Contains(methodName))
                return new AgentToolResult(false, string.Empty, $"HTTP method is not allowed: {methodName}");
            mutatingRequest = methodName is not ("GET" or "HEAD");

            var body = AgentToolSupport.GetString(root, "body");
            if (!string.IsNullOrEmpty(body) && methodName is "GET" or "HEAD")
                return new AgentToolResult(false, string.Empty, $"HTTP {methodName} requests cannot include a body.");

            var settings = await _platform.GetSettingsAsync(ct);
            if (Encoding.UTF8.GetByteCount(body) > settings.MaxFileBytes)
                return new AgentToolResult(false, string.Empty, "HTTP request body exceeds the configured file-size limit.");

            var uri = await _network.ValidateAsync(
                url,
                settings,
                ct,
                bypassRestrictions: AgentPermissionModes.IsBypass(context.EffectivePermissionMode));
            using var request = new HttpRequestMessage(new HttpMethod(methodName), uri);
            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    AgentToolSupport.GetString(root, "content_type", "application/json"));
            }

            if (root.TryGetProperty("headers", out var headersValue))
            {
                if (headersValue.ValueKind != JsonValueKind.Object)
                    return new AgentToolResult(false, string.Empty, "The headers argument must be an object of string values.");
                foreach (var header in headersValue.EnumerateObject())
                {
                    if (RestrictedHeaders.Contains(header.Name))
                        return new AgentToolResult(false, string.Empty, $"HTTP header is controlled by the transport and cannot be set: {header.Name}");
                    if (header.Value.ValueKind != JsonValueKind.String)
                        return new AgentToolResult(false, string.Empty, $"HTTP header value must be a string: {header.Name}");
                    var value = header.Value.GetString() ?? string.Empty;
                    if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content != null)
                    {
                        request.Content.Headers.Remove(header.Name);
                        if (!request.Content.Headers.TryAddWithoutValidation(header.Name, value))
                            return new AgentToolResult(false, string.Empty, $"Invalid HTTP content header: {header.Name}");
                    }
                    else if (!request.Headers.TryAddWithoutValidation(header.Name, value) &&
                             (request.Content == null || !request.Content.Headers.TryAddWithoutValidation(header.Name, value)))
                    {
                        return new AgentToolResult(false, string.Empty, $"Invalid HTTP header: {header.Name}");
                    }
                }
            }

            var credentialName = AgentToolSupport.GetString(root, "credential");
            string? secret = null;
            if (!string.IsNullOrWhiteSpace(credentialName))
            {
                secret = await _platform.ResolveCredentialAsync(
                    credentialName,
                    AgentToolNames.HttpRequest,
                    uri.IdnHost,
                    ct);
                if (string.IsNullOrWhiteSpace(secret))
                    return new AgentToolResult(false, string.Empty, "Credential is unavailable or not permitted for this tool and domain.");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    AgentToolSupport.GetString(root, "auth_scheme", "Bearer"),
                    secret);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
                Math.Min(context.TimeoutSeconds, settings.MaxRuntimeSeconds),
                1,
                600)));
            requestDispatched = true;
            using var response = await _httpClientFactory.CreateClient("Tools")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var responseBody = methodName == "HEAD"
                ? string.Empty
                : await ReadBoundedAsync(
                    response.Content,
                    Math.Min(context.MaxOutputChars, settings.MaxOutputChars),
                    timeoutCts.Token);
            responseBody = SecretRedactor.RedactText(responseBody, secret);
            var responseHeaders = string.Join(
                Environment.NewLine,
                response.Headers.Concat(response.Content.Headers)
                    .Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));
            var output = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}" +
                         $"URL: {uri}{Environment.NewLine}{Environment.NewLine}" +
                         $"Headers:{Environment.NewLine}{responseHeaders}{Environment.NewLine}{Environment.NewLine}" +
                         $"Body:{Environment.NewLine}{responseBody}";
            return new AgentToolResult(
                response.IsSuccessStatusCode,
                SecretRedactor.RedactText(output, secret),
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

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var url = root.GetProperty("url").GetString() ?? "";
        var uri = new Uri(url);
        var method = root.TryGetProperty("method", out var methodValue)
            ? methodValue.GetString()?.Trim().ToUpperInvariant() ?? "GET"
            : "GET";
        var credential = root.TryGetProperty("credential", out var credentialValue) &&
                         credentialValue.ValueKind == JsonValueKind.String
            ? credentialValue.GetString()
            : null;
        var readOnly = method is "GET" or "HEAD";
        return Task.FromResult(new ToolEffectPlan(
            [],
            [],
            [uri.IdnHost],
            [],
            [],
            string.IsNullOrWhiteSpace(credential) ? [] : [credential],
            readOnly ? "medium" : "high",
            !readOnly,
            false));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.BeforeUse;

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maxChars,
        CancellationToken ct)
    {
        maxChars = Math.Clamp(maxChars, 1, 200_000);
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(4096, maxChars + 1)];
        var output = new StringBuilder(Math.Min(maxChars, 8192));
        while (output.Length <= maxChars)
        {
            var remaining = maxChars + 1 - output.Length;
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                break;
            output.Append(buffer, 0, read);
        }

        return output.Length > maxChars
            ? output.ToString(0, maxChars) + "\n[output truncated]"
            : output.ToString();
    }
}

public class WebSearchToolV3 : AgentToolV3Base
{
    private readonly WebSearchAgentTool _inner;
    public WebSearchToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    {
        _inner = new WebSearchAgentTool(platform, network, httpClientFactory);
    }

    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        return await _inner.ExecuteAsync(context, argumentsJson, ct);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
        => Task.FromResult(ToolEffectPlan.Network(
            [
                "html.duckduckgo.com",
                "api.gdeltproject.org",
                "en.wikipedia.org",
                "zh.wikipedia.org",
                "ja.wikipedia.org",
                "ko.wikipedia.org",
                "de.wikipedia.org",
                "fr.wikipedia.org",
                "lite.duckduckgo.com"
            ],
            "low"));
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class BrowserReadToolV3 : AgentToolV3Base
{
    private readonly BrowserReadAgentTool _inner;
    public BrowserReadToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    {
        _inner = new BrowserReadAgentTool(platform, network, httpClientFactory);
    }

    public override LlmToolDefinition Definition => _inner.Definition;
    public override bool RequiresApproval => _inner.RequiresApproval;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        return await _inner.ExecuteAsync(context, argumentsJson, ct);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        return Task.FromResult(ToolEffectPlan.Network([new Uri(url).Host]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;

}

// ── MCP tools ──────────────────────────────────────────────────

public class McpListToolsToolV3 : AgentToolV3Base
{
    private readonly IMcpClientService _mcp;
    public McpListToolsToolV3(IMcpClientService mcp) => _mcp = mcp;

    public override LlmToolDefinition Definition => new("mcp_list_tools", "List available tools from MCP servers.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["server"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "server" } });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var server = doc.RootElement.TryGetProperty("server", out var s) ? s.GetString() : null;
        var tools = await _mcp.ListToolsAsync(
            context.ChatId,
            server,
            ct,
            permissionMode: context.EffectivePermissionMode);
        var output = string.Join("\n", tools.Select(t => $"  - {t.Server}/{t.Name}: {t.Description}"));
        return new AgentToolResult(true, $"MCP tools:\n{output}");
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
        => Task.FromResult(ToolEffectPlan.ReadOnly(["[MCP server connection]"]));
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class McpCallToolV3 : AgentToolV3Base
{
    private readonly IMcpClientService _mcp;
    public McpCallToolV3(IMcpClientService mcp) => _mcp = mcp;

    public override LlmToolDefinition Definition => new("mcp_call", "Call a tool on an MCP server.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["server"] = new Dictionary<string, object> { ["type"] = "string" }, ["tool"] = new Dictionary<string, object> { ["type"] = "string" }, ["arguments"] = new Dictionary<string, object> { ["type"] = "object" } }, ["required"] = new[] { "server", "tool", "arguments" } });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        try
        {
            var doc = JsonDocument.Parse(argumentsJson);
            var server = doc.RootElement.GetProperty("server").GetString() ?? "";
            var tool = doc.RootElement.GetProperty("tool").GetString() ?? "";
            var args = doc.RootElement.GetProperty("arguments");
            var result = await _mcp.CallToolAsync(
                context.ChatId,
                server,
                tool,
                args,
                ct,
                permissionMode: context.EffectivePermissionMode);
            return new AgentToolResult(true, SecretRedactor.RedactText(result));
        }
        catch (McpOutcomeUncertainException ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                SecretRedactor.RedactText(ex.Message),
                OutcomeUncertain: true,
                MayHaveCommitted: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, SecretRedactor.RedactText(ex.Message));
        }
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var server = doc.RootElement.GetProperty("server").GetString() ?? "";
        var tool = doc.RootElement.GetProperty("tool").GetString() ?? "";
        return Task.FromResult(new ToolEffectPlan([], [], [$"{server}/{tool}"], [], [], [], "medium", true, false));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
}
