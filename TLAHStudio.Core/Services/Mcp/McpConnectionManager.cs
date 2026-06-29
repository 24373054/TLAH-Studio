using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Mcp;

/// <summary>
/// M2.12.0: MCP connection lifecycle status.
/// </summary>
public enum McpConnectionStatus { Disconnected, Connecting, Connected, Error }

public sealed record McpServerState(
    Guid ServerConfigId, string Name, string Transport,
    McpConnectionStatus Status, DateTime? ConnectedAt, string? LastError,
    int ToolCount, int ReconnectAttempt, bool Enabled);

/// <summary>
/// M2.12.0: MCP connection manager with reconnect and lifecycle management.
/// </summary>
public interface IMcpConnectionManager
{
    Task<McpServerState> GetStatusAsync(Guid serverConfigId, CancellationToken ct = default);
    Task ConnectAsync(Guid serverConfigId, CancellationToken ct = default);
    Task DisconnectAsync(Guid serverConfigId, CancellationToken ct = default);
    Task EnableAsync(Guid serverConfigId, CancellationToken ct = default);
    Task DisableAsync(Guid serverConfigId, CancellationToken ct = default);
    Task<IReadOnlyList<McpServerState>> ListConnectionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LlmToolDefinition>> GetToolsAsync(Guid serverConfigId, CancellationToken ct = default);
}

public class McpConnectionManager : IMcpConnectionManager
{
    private readonly IToolPlatformService _toolPlatform;
    private readonly IMcpClientService _mcpClient;
    private readonly Dictionary<Guid, McpServerState> _states = new();
    private readonly object _lock = new();

    public McpConnectionManager(IToolPlatformService toolPlatform, IMcpClientService mcpClient)
    {
        _toolPlatform = toolPlatform;
        _mcpClient = mcpClient;
    }

    public async Task<McpServerState> GetStatusAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(serverConfigId, out var cached))
                return cached;
        }

        var configs = await _toolPlatform.ListMcpServersAsync(projectSpaceId: null, ct);
        var config = configs.FirstOrDefault(c => c.Id == serverConfigId);
        if (config == null)
            throw new InvalidOperationException($"MCP server not found: {serverConfigId}");

        var state = new McpServerState(serverConfigId, config.Name, config.Transport,
            McpConnectionStatus.Disconnected, null, null, 0, 0, config.Enabled);
        lock (_lock) _states[serverConfigId] = state;
        return state;
    }

    public async Task ConnectAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        var state = await GetStatusAsync(serverConfigId, ct);
        lock (_lock)
        {
            state = state with { Status = McpConnectionStatus.Connecting };
            _states[serverConfigId] = state;
        }

        try
        {
            // Test connection and list tools via MCP client
            var configs = await _toolPlatform.ListMcpServersAsync(projectSpaceId: null, ct);
            var config = configs.FirstOrDefault(c => c.Id == serverConfigId)
                ?? throw new InvalidOperationException($"MCP server not found: {serverConfigId}");

            var tools = await _mcpClient.TestServerAsync(config, ct);
            var connected = new McpServerState(serverConfigId, state.Name, state.Transport,
                McpConnectionStatus.Connected, DateTime.UtcNow, null, tools.Count, 0, state.Enabled);
            lock (_lock) _states[serverConfigId] = connected;
        }
        catch (Exception ex)
        {
            var error = new McpServerState(serverConfigId, state.Name, state.Transport,
                McpConnectionStatus.Error, null, ex.Message, state.ToolCount, state.ReconnectAttempt + 1, state.Enabled);
            lock (_lock) _states[serverConfigId] = error;
            throw;
        }
    }

    public Task DisconnectAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(serverConfigId, out var state))
                _states[serverConfigId] = state with { Status = McpConnectionStatus.Disconnected };
        }
        return Task.CompletedTask;
    }

    public async Task EnableAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        var configs = await _toolPlatform.ListMcpServersAsync(projectSpaceId: null, ct);
        var config = configs.FirstOrDefault(c => c.Id == serverConfigId);
        if (config != null)
            await _toolPlatform.SaveMcpServerAsync(config with { }, ct);
        lock (_lock)
        {
            if (_states.TryGetValue(serverConfigId, out var state))
                _states[serverConfigId] = state with { Enabled = true };
        }
    }

    public async Task DisableAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        await DisconnectAsync(serverConfigId, ct);
        lock (_lock)
        {
            if (_states.TryGetValue(serverConfigId, out var state))
                _states[serverConfigId] = state with { Enabled = false };
        }
    }

    public async Task<IReadOnlyList<McpServerState>> ListConnectionsAsync(CancellationToken ct = default)
    {
        var configs = await _toolPlatform.ListMcpServersAsync(projectSpaceId: null, ct);
        var result = new List<McpServerState>();
        foreach (var config in configs)
        {
            var state = await GetStatusAsync(config.Id, ct);
            result.Add(state);
        }
        return result;
    }

    public async Task<IReadOnlyList<LlmToolDefinition>> GetToolsAsync(Guid serverConfigId, CancellationToken ct = default)
    {
        var state = await GetStatusAsync(serverConfigId, ct);
        if (state.Status != McpConnectionStatus.Connected)
            throw new InvalidOperationException($"MCP server not connected: {state.Name}");

        var configs = await _toolPlatform.ListMcpServersAsync(projectSpaceId: null, ct);
        var config = configs.FirstOrDefault(c => c.Id == serverConfigId)
            ?? throw new InvalidOperationException($"MCP server not found: {serverConfigId}");

        var tools = await _mcpClient.TestServerAsync(config, ct);
        return tools.Select(t => new LlmToolDefinition(
            $"mcp_{state.Name}_{t.Name}", t.Description, new Dictionary<string, object>())).ToList();
    }
}

/// <summary>
/// M2.12.0: Exponential backoff reconnect policy for MCP servers.
/// </summary>
public interface IMcpReconnectPolicy
{
    TimeSpan GetNextDelay(int attemptNumber);
}

public class McpReconnectPolicy : IMcpReconnectPolicy
{
    private static readonly TimeSpan[] Delays = {
        TimeSpan.FromSeconds(1),   TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),   TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),  TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),  TimeSpan.FromSeconds(60)
    };

    public TimeSpan GetNextDelay(int attemptNumber)
    {
        var index = Math.Clamp(attemptNumber - 1, 0, Delays.Length - 1);
        var baseDelay = Delays[index];
        // Add jitter (±25%)
        var jitter = TimeSpan.FromMilliseconds(
            baseDelay.TotalMilliseconds * 0.25 * (Random.Shared.NextDouble() * 2 - 1));
        return baseDelay + jitter;
    }
}

/// <summary>
/// M2.12.0: MCP authentication service with template-based credential resolution.
/// </summary>
public interface IMcpAuthService
{
    Task<Dictionary<string, string>> ResolveHeadersAsync(
        McpServerConfigDto config, IToolPlatformService toolPlatform, CancellationToken ct = default);
}

public class McpAuthService : IMcpAuthService
{
    public async Task<Dictionary<string, string>> ResolveHeadersAsync(
        McpServerConfigDto config, IToolPlatformService toolPlatform, CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(config.HeadersJson))
            return headers;

        try
        {
            var rawHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(config.HeadersJson);
            if (rawHeaders == null) return headers;

            foreach (var (key, template) in rawHeaders)
            {
                var resolved = await ResolveTemplateAsync(template, toolPlatform, ct);
                headers[key] = resolved;
            }
        }
        catch { /* Ignore malformed headers JSON */ }

        return headers;
    }

    private static async Task<string> ResolveTemplateAsync(
        string template, IToolPlatformService toolPlatform, CancellationToken ct)
    {
        // {{credential:name}} → resolve from credential store
        if (template.StartsWith("{{credential:") && template.EndsWith("}}"))
        {
            var name = template["{{credential:".Length..^2];
            return await toolPlatform.ResolveCredentialAsync(name, "mcp", "*", ct) ?? template;
        }
        // {{env:VAR}} → resolve from environment
        if (template.StartsWith("{{env:") && template.EndsWith("}}"))
        {
            var varName = template["{{env:".Length..^2];
            return Environment.GetEnvironmentVariable(varName) ?? template;
        }
        // {{static:value}} → literal
        if (template.StartsWith("{{static:") && template.EndsWith("}}"))
            return template["{{static:".Length..^2];

        return template;
    }
}
