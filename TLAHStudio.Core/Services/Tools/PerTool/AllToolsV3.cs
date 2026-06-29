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
    private readonly ISandboxCommandService _sandbox;
    public FileListToolV3(ISandboxCommandService sandbox) => _sandbox = sandbox;
    public override LlmToolDefinition Definition => new("file_list", "List files in a directory.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["path"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "path" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var path = args?.GetValueOrDefault("path", ".");
        var result = await _sandbox.ExecuteAsync(context.ChatId, $"dir \"{path}\" 2>nul || ls \"{path}\"", new SandboxCommandOptions(10, 8000), ct);
        return new AgentToolResult(result.ExitCode == 0, SecretRedactor.RedactText(result.StandardOutput), result.StandardError);
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
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;
    public FileReadToolV3(ISandboxCommandService sandbox, IToolPlatformService platform) { _sandbox = sandbox; _platform = platform; }
    public override LlmToolDefinition Definition => new("file_read", "Read a file from the sandbox.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["path"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "path" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var path = args?.GetValueOrDefault("path", "");
        var root = _sandbox.GetSandboxRoot(context.ChatId);
        var fullPath = Path.GetFullPath(Path.Combine(root, (path ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        if (!File.Exists(fullPath)) return new AgentToolResult(false, "", $"File not found: {path}");
        var content = await File.ReadAllTextAsync(fullPath, ct);
        return new AgentToolResult(true, SecretRedactor.RedactText(content));
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var root = sandbox.GetSandboxRoot(chatId);
        return Task.FromResult(ToolEffectPlan.ReadOnly([Path.Combine(root, args?.GetValueOrDefault("path", "") ?? "")]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class FileWriteToolV3 : AgentToolV3Base
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;
    public FileWriteToolV3(ISandboxCommandService sandbox, IToolPlatformService platform) { _sandbox = sandbox; _platform = platform; }
    public override LlmToolDefinition Definition => new("file_write", "Write content to a file in the sandbox.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["path"] = new Dictionary<string, object> { ["type"] = "string" }, ["content"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "path", "content" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var content = doc.RootElement.GetProperty("content").GetString() ?? "";
        var root = _sandbox.GetSandboxRoot(context.ChatId);
        var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        await File.WriteAllTextAsync(fullPath, content, ct);
        return new AgentToolResult(true, $"Written: {path} ({content.Length} chars)");
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var root = sandbox.GetSandboxRoot(chatId);
        var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));
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
        var result = await _router.ExecuteAsync(new ExecutionRequest(context.ChatId, command, context.TimeoutSeconds, context.MaxOutputChars), ct: ct);
        var isFailure = CommandSemantics.IsExitCodeFailure(command, result.ExitCode);
        if (result.BlockedReason != null)
            return new AgentToolResult(false, result.StandardOutput, result.BlockedReason);
        return new AgentToolResult(!isFailure, SecretRedactor.RedactText(result.StandardOutput), result.StandardError);
    }

    public override Task<ToolSafetyClassification> ClassifySafetyAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var command = doc.RootElement.GetProperty("command").GetString() ?? "";
        var isDestructive = CommandSemantics.IsDestructive(command);
        var isReadOnly = CommandSemantics.ReadOnlyCommands.Any(rc => command.ToLowerInvariant().Contains(rc.ToLowerInvariant()));
        return Task.FromResult(new ToolSafetyClassification(
            isDestructive ? "high" : isReadOnly ? "low" : "medium",
            "command", isReadOnly, isDestructive, isDestructive, false,
            isDestructive ? $"Destructive command: {command[..Math.Min(command.Length, 80)]}" : $"Execute: {command[..Math.Min(command.Length, 80)]}",
            isDestructive ? "This command can modify or destroy data." : null, null));
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
    private readonly ISandboxCommandService _sandbox;
    public GitToolV3(ISandboxCommandService sandbox) => _sandbox = sandbox;

    public override LlmToolDefinition Definition => new("git", "Run a git command in the workspace.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["command"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "command" } });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var cmd = doc.RootElement.GetProperty("command").GetString() ?? "";
        var result = await _sandbox.ExecuteAsync(context.ChatId, $"git {cmd}", new SandboxCommandOptions(context.TimeoutSeconds, context.MaxOutputChars), ct);
        var isFailure = CommandSemantics.IsExitCodeFailure($"git {cmd}", result.ExitCode);
        return new AgentToolResult(!isFailure, SecretRedactor.RedactText(result.StandardOutput), result.StandardError);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var cmd = doc.RootElement.GetProperty("command").GetString() ?? "";
        var isDestructive = cmd.Contains("push") || cmd.Contains("reset") || cmd.Contains("clean") || cmd.Contains("rm");
        return Task.FromResult(new ToolEffectPlan([], [],
            [], [$"git {cmd}"], [], [], isDestructive ? "high" : "medium", isDestructive, !isDestructive));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
}

// ── HTTP / Web / Browser tools ────────────────────────────────

public class HttpRequestToolV3 : AgentToolV3Base
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    public HttpRequestToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    { _platform = platform; _network = network; _httpClientFactory = httpClientFactory; }

    public override LlmToolDefinition Definition => new("http_request", "Make an HTTP request to a URL.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["url"] = new Dictionary<string, object> { ["type"] = "string" }, ["method"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "GET", "POST", "PUT", "DELETE" } } }, ["required"] = new[] { "url" } });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        var settings = await _platform.GetSettingsAsync(ct);
        var uri = await _network.ValidateAsync(url, settings, ct);
        using var client = _httpClientFactory.CreateClient("Tools");
        var response = await client.GetAsync(uri, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new AgentToolResult(response.IsSuccessStatusCode, body[..Math.Min(body.Length, 8000)]);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        var uri = new Uri(url);
        return Task.FromResult(ToolEffectPlan.Network([uri.Host]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.BeforeUse;
}

public class WebSearchToolV3 : AgentToolV3Base
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    public WebSearchToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    { _platform = platform; _network = network; _httpClientFactory = httpClientFactory; }

    public override LlmToolDefinition Definition => new("web_search", "Search the web using DuckDuckGo HTML.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["query"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "query" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var query = doc.RootElement.GetProperty("query").GetString() ?? "";
        var settings = await _platform.GetSettingsAsync(ct);
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        var uri = await _network.ValidateAsync(url, settings, ct);
        using var client = _httpClientFactory.CreateClient("Tools");
        var response = await client.GetStringAsync(uri, ct);
        return new AgentToolResult(true, response[..Math.Min(response.Length, 6000)]);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
        => Task.FromResult(ToolEffectPlan.Network(["html.duckduckgo.com"], "low"));
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

public class BrowserReadToolV3 : AgentToolV3Base
{
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    public BrowserReadToolV3(IToolPlatformService platform, INetworkSecurityService network, IHttpClientFactory httpClientFactory)
    { _platform = platform; _network = network; _httpClientFactory = httpClientFactory; }

    public override LlmToolDefinition Definition => new("browser_read", "Read a web page and extract its text content.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["url"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "url" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        var settings = await _platform.GetSettingsAsync(ct);
        var uri = await _network.ValidateAsync(url, settings, ct);
        using var client = _httpClientFactory.CreateClient("Tools");
        var html = await client.GetStringAsync(uri, ct);
        var text = StripHtml(html);
        return new AgentToolResult(true, text[..Math.Min(text.Length, 8000)]);
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        return Task.FromResult(ToolEffectPlan.Network([new Uri(url).Host]));
    }
    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }
}

// ── MCP tools ──────────────────────────────────────────────────

public class McpListToolsToolV3 : AgentToolV3Base
{
    private readonly IMcpClientService _mcp;
    public McpListToolsToolV3(IMcpClientService mcp) => _mcp = mcp;

    public override LlmToolDefinition Definition => new("mcp_list_tools", "List available tools from MCP servers.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["server"] = new Dictionary<string, object> { ["type"] = "string" } }, ["required"] = new[] { "server" } });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var server = doc.RootElement.TryGetProperty("server", out var s) ? s.GetString() : null;
        var tools = await _mcp.ListToolsAsync(context.ChatId, server, ct);
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
        var doc = JsonDocument.Parse(argumentsJson);
        var server = doc.RootElement.GetProperty("server").GetString() ?? "";
        var tool = doc.RootElement.GetProperty("tool").GetString() ?? "";
        var args = doc.RootElement.GetProperty("arguments");
        var result = await _mcp.CallToolAsync(context.ChatId, server, tool, args, ct);
        return new AgentToolResult(true, SecretRedactor.RedactText(result));
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
