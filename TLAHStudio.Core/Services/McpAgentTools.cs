using System.Text.Json;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

public sealed class McpListToolsAgentTool : IAgentTool
{
    private readonly IMcpClientService _mcp;

    public McpListToolsAgentTool(IMcpClientService mcp)
    {
        _mcp = mcp;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.McpListTools,
        "List tools exposed by configured and enabled MCP servers for the current project.",
        new Dictionary<string, object>
        {
            ["server"] = AgentToolSupport.StringProperty("Optional MCP server name."),
            ["reason"] = AgentToolSupport.StringProperty("Why MCP tool discovery is needed.")
        });

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
            var tools = await _mcp.ListToolsAsync(
                context.ChatId,
                AgentToolSupport.GetString(root, "server"),
                ct);
            var output = JsonSerializer.Serialize(
                tools.Select(t => new
                {
                    server = t.Server,
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.InputSchema
                }),
                new JsonSerializerOptions { WriteIndented = true });
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class McpCallAgentTool : IAgentTool
{
    private readonly IMcpClientService _mcp;

    public McpCallAgentTool(IMcpClientService mcp)
    {
        _mcp = mcp;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.McpCall,
        "Call a tool on a configured MCP server. Use mcp_list_tools first to inspect its exact input schema.",
        new Dictionary<string, object>
        {
            ["server"] = AgentToolSupport.StringProperty("Configured MCP server name."),
            ["tool"] = AgentToolSupport.StringProperty("MCP tool name."),
            ["arguments"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["description"] = "Arguments matching the MCP tool input schema."
            },
            ["reason"] = AgentToolSupport.StringProperty("Why this MCP tool call is needed.")
        },
        ["server", "tool", "arguments"]);

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
            if (!root.TryGetProperty("arguments", out var arguments) ||
                arguments.ValueKind != JsonValueKind.Object)
                return new AgentToolResult(false, string.Empty, "The arguments object is required.");
            var output = await _mcp.CallToolAsync(
                context.ChatId,
                AgentToolSupport.GetString(root, "server"),
                AgentToolSupport.GetString(root, "tool"),
                arguments.Clone(),
                ct);
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class McpListResourcesAgentTool : IAgentTool
{
    private readonly IMcpClientService _mcp;

    public McpListResourcesAgentTool(IMcpClientService mcp)
    {
        _mcp = mcp;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.McpListResources,
        "List resources exposed by configured and enabled MCP servers for the current project.",
        new Dictionary<string, object>
        {
            ["server"] = AgentToolSupport.StringProperty("Optional MCP server name."),
            ["reason"] = AgentToolSupport.StringProperty("Why MCP resource discovery is needed.")
        });

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
            var resources = await _mcp.ListResourcesAsync(
                context.ChatId,
                AgentToolSupport.GetString(root, "server"),
                ct);
            var output = JsonSerializer.Serialize(
                resources.Select(r => new
                {
                    server = r.Server,
                    uri = r.Uri,
                    name = r.Name,
                    description = r.Description,
                    mimeType = r.MimeType
                }),
                new JsonSerializerOptions { WriteIndented = true });
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class McpReadResourceAgentTool : IAgentTool
{
    private readonly IMcpClientService _mcp;

    public McpReadResourceAgentTool(IMcpClientService mcp)
    {
        _mcp = mcp;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.McpReadResource,
        "Read a resource from a configured MCP server. Use mcp_list_resources first to discover exact URIs.",
        new Dictionary<string, object>
        {
            ["server"] = AgentToolSupport.StringProperty("Configured MCP server name."),
            ["uri"] = AgentToolSupport.StringProperty("Resource URI returned by mcp_list_resources."),
            ["reason"] = AgentToolSupport.StringProperty("Why this MCP resource is needed.")
        },
        ["server", "uri"]);

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
            var output = await _mcp.ReadResourceAsync(
                context.ChatId,
                AgentToolSupport.GetString(root, "server"),
                AgentToolSupport.GetString(root, "uri"),
                ct);
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}
