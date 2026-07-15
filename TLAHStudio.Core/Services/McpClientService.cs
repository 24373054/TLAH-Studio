using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public sealed record McpToolInfo(
    string Server,
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed record McpResourceInfo(
    string Server,
    string Uri,
    string Name,
    string Description,
    string MimeType);

/// <summary>
/// A mutating MCP request crossed the transport boundary, but no trustworthy
/// response was received. Callers must not automatically replay the request.
/// </summary>
public sealed class McpOutcomeUncertainException : InvalidOperationException
{
    public McpOutcomeUncertainException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IMcpClientService
{
    Task<IReadOnlyList<McpToolInfo>> TestServerAsync(
        McpServerConfigDto server,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval);

    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        Guid chatId,
        string? serverName = null,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval);

    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
        Guid chatId,
        string? serverName = null,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval);

    Task<string> ReadResourceAsync(
        Guid chatId,
        string serverName,
        string uri,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval);

    Task<string> CallToolAsync(
        Guid chatId,
        string serverName,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval);
}

public sealed class McpClientService : IMcpClientService
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly string ClientVersion =
        typeof(McpClientService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly Microsoft.EntityFrameworkCore.DbContext _db;
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;

    public McpClientService(
        Microsoft.EntityFrameworkCore.DbContext db,
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        Guid chatId,
        string? serverName = null,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval)
    {
        var servers = await ResolveServersAsync(chatId, serverName, ct);
        var results = new List<McpToolInfo>();
        foreach (var server in servers)
            results.AddRange(await TestServerAsync(server, ct, permissionMode));
        return results;
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
        Guid chatId,
        string? serverName = null,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval)
    {
        var servers = await ResolveServersAsync(chatId, serverName, ct);
        var results = new List<McpResourceInfo>();
        foreach (var server in servers)
            results.AddRange(await ListServerResourcesAsync(server, ct, permissionMode));
        return results;
    }

    public async Task<string> ReadResourceAsync(
        Guid chatId,
        string serverName,
        string uri,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException("MCP resource uri is required.");

        var server = (await ResolveServersAsync(chatId, serverName, ct)).Single();
        var response = await InvokeAsync(server, "resources/read", new { uri }, ct, permissionMode);
        return SecretRedactor.RedactJson(response.GetRawText());
    }

    public async Task<IReadOnlyList<McpToolInfo>> TestServerAsync(
        McpServerConfigDto server,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval)
    {
        var response = await InvokeAsync(server, "tools/list", new { }, ct, permissionMode);
        var results = new List<McpToolInfo>();
        if (!response.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return results;
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var description = tool.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString() ?? string.Empty
                : string.Empty;
            var schema = tool.TryGetProperty("inputSchema", out var schemaValue)
                ? schemaValue.Clone()
                : JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
            results.Add(new McpToolInfo(server.Name, name, description, schema));
        }
        return results;
    }

    private async Task<IReadOnlyList<McpResourceInfo>> ListServerResourcesAsync(
        McpServerConfigDto server,
        CancellationToken ct,
        string permissionMode)
    {
        var response = await InvokeAsync(server, "resources/list", new { }, ct, permissionMode);
        var results = new List<McpResourceInfo>();
        if (!response.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var resource in resources.EnumerateArray())
        {
            var uri = resource.TryGetProperty("uri", out var uriValue)
                ? uriValue.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(uri))
                continue;
            var name = resource.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;
            var description = resource.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString() ?? string.Empty
                : string.Empty;
            var mimeType = resource.TryGetProperty("mimeType", out var mimeValue)
                ? mimeValue.GetString() ?? string.Empty
                : string.Empty;
            results.Add(new McpResourceInfo(server.Name, uri, name, description, mimeType));
        }
        return results;
    }

    public async Task<string> CallToolAsync(
        Guid chatId,
        string serverName,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        string permissionMode = AgentPermissionModes.RequestApproval)
    {
        var server = (await ResolveServersAsync(chatId, serverName, ct)).Single();
        var response = await InvokeAsync(server, "tools/call", new
        {
            name = toolName,
            arguments
        }, ct, permissionMode);
        return SecretRedactor.RedactJson(response.GetRawText());
    }

    private async Task<IReadOnlyList<McpServerConfigDto>> ResolveServersAsync(
        Guid chatId,
        string? serverName,
        CancellationToken ct)
    {
        var projectId = await _db.Set<Chat>()
            .Where(c => c.Id == chatId)
            .Select(c => c.ProjectSpaceId)
            .FirstOrDefaultAsync(ct);
        var servers = (await _platform.ListMcpServersAsync(projectId, ct))
            .Where(s => s.Enabled)
            .Where(s => s.ProjectSpaceId == null || s.ProjectSpaceId == projectId)
            .Where(s => string.IsNullOrWhiteSpace(serverName) ||
                        s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(serverName) && servers.Count != 1)
            throw new InvalidOperationException($"Enabled MCP server not found: {serverName}");
        return servers;
    }

    private Task<JsonElement> InvokeAsync(
        McpServerConfigDto server,
        string method,
        object parameters,
        CancellationToken ct,
        string permissionMode)
    {
        var mayMutate = string.Equals(method, "tools/call", StringComparison.Ordinal);
        return
        server.Transport == McpTransportTypes.StreamableHttp
            ? InvokeHttpAsync(server, method, parameters, ct, permissionMode, mayMutate)
            : InvokeStdioAsync(server, method, parameters, ct, mayMutate);
    }

    private async Task<JsonElement> InvokeStdioAsync(
        McpServerConfigDto server,
        string method,
        object parameters,
        CancellationToken ct,
        bool mayMutate)
    {
        if (string.IsNullOrWhiteSpace(server.Command) ||
            server.Command.IndexOfAny([';', '|', '&', '\r', '\n']) >= 0)
            throw new InvalidOperationException("The MCP STDIO command is missing or contains shell metacharacters.");

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = server.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8
        };
        foreach (var argument in ParseStringArray(server.ArgumentsJson))
            psi.ArgumentList.Add(argument);
        foreach (var pair in await ParseEnvironmentAsync(server.EnvironmentJson, ct))
            psi.Environment[pair.Key] = pair.Value;
        ApplyStdioCompatibilityEnvironment(psi);

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) { throw new InvalidOperationException($"Unable to start MCP server {server.Name}: {ex.Message}", ex); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var settings = await _platform.GetSettingsAsync(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.MaxRuntimeSeconds));
        var operationDispatched = false;
        try
        {
            var nextId = 1;
            await WriteStdioAsync(process, Request(nextId, "initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "TLAH Studio", version = ClientVersion }
            }), timeoutCts.Token);
            _ = await ReadStdioResponseAsync(process, nextId++, timeoutCts.Token);
            await WriteStdioAsync(process, Notification("notifications/initialized", new { }), timeoutCts.Token);
            // Mark before writing: a pipe/flush error cannot prove the server
            // did not receive and execute the complete request.
            operationDispatched = true;
            await WriteStdioAsync(process, Request(nextId, method, parameters), timeoutCts.Token);
            return await ReadStdioResponseAsync(process, nextId, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (operationDispatched && mayMutate)
            {
                throw new McpOutcomeUncertainException(
                    $"MCP tool call on \"{server.Name}\" timed out after dispatch; it may have completed.");
            }
            throw new InvalidOperationException(
                $"MCP server \"{server.Name}\" did not reply within {settings.MaxRuntimeSeconds} seconds. " +
                "Verify the command, arguments, UTF-8 output, and that the process implements MCP over STDIO.");
        }
        catch (IOException ex) when (operationDispatched && mayMutate)
        {
            throw new McpOutcomeUncertainException(
                $"MCP tool call on \"{server.Name}\" lost its STDIO response after dispatch; it may have completed.",
                ex);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private async Task<JsonElement> InvokeHttpAsync(
        McpServerConfigDto server,
        string method,
        object parameters,
        CancellationToken ct,
        string permissionMode,
        bool mayMutate)
    {
        var settings = await _platform.GetSettingsAsync(ct);
        var endpoint = await _network.ValidateAsync(
            server.Endpoint,
            settings,
            ct,
            bypassRestrictions: AgentPermissionModes.IsBypass(permissionMode));
        var headers = await ParseHeadersAsync(server.HeadersJson, endpoint.IdnHost, ct);
        var sessionId = string.Empty;
        var nextId = 1;

        var initialize = await SendHttpAsync(
            endpoint,
            Request(nextId++, "initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "TLAH Studio", version = ClientVersion }
            }),
            headers,
            sessionId,
            settings.MaxRuntimeSeconds,
            ct,
            outcomeUncertainOnTransportFailure: false);
        sessionId = initialize.SessionId;
        await SendHttpAsync(
            endpoint,
            Notification("notifications/initialized", new { }),
            headers,
            sessionId,
            settings.MaxRuntimeSeconds,
            ct,
            outcomeUncertainOnTransportFailure: false);
        var response = await SendHttpAsync(
            endpoint,
            Request(nextId, method, parameters),
            headers,
            sessionId,
            settings.MaxRuntimeSeconds,
            ct,
            outcomeUncertainOnTransportFailure: mayMutate);
        return ExtractResult(response.Payload, nextId);
    }

    private async Task<(JsonElement Payload, string SessionId)> SendHttpAsync(
        Uri endpoint,
        object payload,
        IReadOnlyDictionary<string, string> headers,
        string sessionId,
        int timeoutSeconds,
        CancellationToken ct,
        bool outcomeUncertainOnTransportFailure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        foreach (var pair in headers)
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        HttpResponseMessage response;
        string text;
        try
        {
            response = await _httpClientFactory.CreateClient("Tools").SendAsync(request, timeoutCts.Token);
            text = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (outcomeUncertainOnTransportFailure)
            {
                throw new McpOutcomeUncertainException(
                    $"MCP HTTP tool call to {endpoint} timed out after dispatch; it may have completed.");
            }
            throw new InvalidOperationException(
                $"MCP HTTP endpoint {endpoint} did not reply within {timeoutSeconds} seconds.");
        }
        catch (Exception ex) when (
            outcomeUncertainOnTransportFailure &&
            ex is HttpRequestException or IOException)
        {
            throw new McpOutcomeUncertainException(
                $"MCP HTTP tool call to {endpoint} lost its response after dispatch; it may have completed.",
                ex);
        }
        using (response)
        {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP server returned HTTP {(int)response.StatusCode}: {SecretRedactor.RedactText(text)}");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var json = contentType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? ParseSseJson(text)
            : text;
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException ex) when (outcomeUncertainOnTransportFailure)
        {
            throw new McpOutcomeUncertainException(
                $"MCP HTTP tool call to {endpoint} returned an incomplete response after dispatch; it may have completed.",
                ex);
        }
        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() ?? sessionId
            : sessionId;
        return (element, returnedSession);
        }
    }

    private static async Task WriteStdioAsync(
        Process process,
        object message,
        CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadStdioResponseAsync(
        Process process,
        int requestId,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line == null)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new IOException($"MCP STDIO server exited before replying: {SecretRedactor.RedactText(error)}");
            }
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonElement root;
            try { root = JsonDocument.Parse(line).RootElement.Clone(); }
            catch { continue; }
            if (!root.TryGetProperty("id", out var id) || !TryReadId(id, out var value) || value != requestId)
                continue;
            return ExtractResult(root, requestId);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ParseHeadersAsync(
        string json,
        string domain,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.GetString() ?? string.Empty;
            result[property.Name] = await ResolveCredentialPlaceholderAsync(
                value, AgentToolNames.McpCall, domain, ct);
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>> ParseEnvironmentAsync(
        string json,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.GetString() ?? string.Empty;
            result[property.Name] = await ResolveCredentialPlaceholderAsync(
                value, AgentToolNames.McpCall, "local", ct);
        }
        return result;
    }

    private async Task<string> ResolveCredentialPlaceholderAsync(
        string value,
        string toolName,
        string domain,
        CancellationToken ct)
    {
        const string prefix = "${credential:";
        var resolved = value;
        var start = resolved.IndexOf(prefix, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = resolved.IndexOf('}', start + prefix.Length);
            if (end < 0)
                throw new InvalidOperationException("Credential placeholder is missing its closing brace.");
            var name = resolved[(start + prefix.Length)..end].Trim();
            var secret = await _platform.ResolveCredentialAsync(name, toolName, domain, ct);
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException(
                    $"Credential \"{name}\" is unavailable or not permitted for {toolName} on {domain}.");
            resolved = resolved[..start] + secret + resolved[(end + 1)..];
            start = resolved.IndexOf(prefix, start + secret.Length, StringComparison.Ordinal);
        }
        return resolved;
    }

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray()
            : throw new InvalidOperationException("MCP STDIO arguments must be a JSON array of strings.");
    }

    internal static void ApplyStdioCompatibilityEnvironment(ProcessStartInfo psi)
    {
        var command = Path.GetFileNameWithoutExtension(psi.FileName);
        if (!command.Equals("python", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("python3", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("py", StringComparison.OrdinalIgnoreCase))
            return;

        psi.Environment.TryAdd("PYTHONUTF8", "1");
        psi.Environment.TryAdd("PYTHONIOENCODING", "utf-8");
    }

    private static object Request(int id, string method, object parameters) => new
    {
        jsonrpc = "2.0",
        id,
        method,
        @params = parameters
    };

    private static object Notification(string method, object parameters) => new
    {
        jsonrpc = "2.0",
        method,
        @params = parameters
    };

    private static JsonElement ExtractResult(JsonElement root, int requestId)
    {
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MCP request {requestId} failed: {SecretRedactor.RedactJson(error.GetRawText())}");
        if (!root.TryGetProperty("result", out var result))
            return root.Clone();
        return result.Clone();
    }

    private static bool TryReadId(JsonElement id, out int value)
    {
        if (id.ValueKind == JsonValueKind.Number)
            return id.TryGetInt32(out value);
        return int.TryParse(id.GetString(), out value);
    }

    private static string ParseSseJson(string text)
    {
        var data = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].Trim())
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return data ?? "{}";
    }
}
