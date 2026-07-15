using System.Net;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Tools.PerTool;

namespace TLAHStudio.Core.Tests;

public sealed class OutcomeUncertaintyTests
{
    [Fact]
    public void AgentToolResult_SerializesReplayFenceMetadata()
    {
        var result = new AgentToolResult(
            false,
            string.Empty,
            "response lost",
            OutcomeUncertain: true,
            MayHaveCommitted: true);

        using var json = JsonDocument.Parse(result.ToJson());

        Assert.True(json.RootElement.GetProperty("outcomeUncertain").GetBoolean());
        Assert.True(json.RootElement.GetProperty("mayHaveCommitted").GetBoolean());
    }

    [Fact]
    public async Task SandboxTimeout_IsMarkedAsPotentiallyCommitted()
    {
        var sandbox = NewSandbox();

        var result = await sandbox.ExecuteAsync(
            Guid.NewGuid(),
            "Start-Sleep -Seconds 5",
            new SandboxCommandOptions(1, 2_000));

        Assert.True(result.TimedOut);
        Assert.True(result.OutcomeUncertain);
        Assert.True(result.MayHaveCommitted);
    }

    [Fact]
    public async Task TerminalTools_PropagateBackendReplayFenceMetadata()
    {
        var router = new UncertainExecutionBackendRouter();
        var context = Context();
        var arguments = """{"command":"Set-Content marker.txt done"}""";

        var legacy = await new TerminalExecAgentTool(router)
            .ExecuteAsync(context, arguments);
        var v3 = await new TerminalExecToolV3(router)
            .ExecuteAsync(context, arguments, CancellationToken.None);

        AssertUncertain(legacy);
        AssertUncertain(v3);
    }

    [Fact]
    public async Task RemoteTransportFailure_IsMarkedAsPotentiallyCommitted()
    {
        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        await platform.UpdateSettingsAsync(new ToolPlatformSettingsUpdate(
            ToolExecutionBackends.Remote,
            string.Empty,
            30,
            20_000,
            10 * 1024 * 1024,
            512,
            8,
            string.Empty,
            string.Empty,
            "https://remote.example.test/execute",
            string.Empty));
        using var client = new HttpClient(new ThrowingHttpHandler());
        var router = new ExecutionBackendRouter(
            NewSandbox(),
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(client));

        var result = await router.ExecuteAsync(
            new ExecutionRequest(Guid.NewGuid(), "Set-Content marker.txt done", 10, 2_000),
            ToolExecutionBackends.Remote);

        Assert.False(result.Success);
        Assert.True(result.OutcomeUncertain);
        Assert.True(result.MayHaveCommitted);
        Assert.Contains("transport", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitTimeoutFactory_CreatesNonReplayableFailure()
    {
        var result = GitAgentTool.OutcomeUncertainFailure("timed out");

        AssertUncertain(result);
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    public async Task HttpTransportFailure_OnlyFencesMutatingRequests(
        string method,
        bool expectedUncertain)
    {
        await using var db = TestDb.Create();
        using var legacyClient = new HttpClient(new ThrowingHttpHandler());
        using var v3Client = new HttpClient(new ThrowingHttpHandler());
        var platform = new ToolPlatformService(db);
        var network = new AllowNetworkSecurityService();
        var arguments = JsonSerializer.Serialize(new
        {
            url = "https://api.example.test/items/1",
            method,
            body = method is "GET" or "HEAD" ? string.Empty : "{}"
        });

        var legacy = await new HttpRequestAgentTool(
                platform,
                network,
                new StaticHttpClientFactory(legacyClient))
            .ExecuteAsync(Context(), arguments);
        var v3 = await new HttpRequestToolV3(
                platform,
                network,
                new StaticHttpClientFactory(v3Client))
            .ExecuteAsync(Context(), arguments, CancellationToken.None);

        Assert.Equal(expectedUncertain, legacy.OutcomeUncertain);
        Assert.Equal(expectedUncertain, legacy.MayHaveCommitted);
        Assert.Equal(expectedUncertain, v3.OutcomeUncertain);
        Assert.Equal(expectedUncertain, v3.MayHaveCommitted);
    }

    [Fact]
    public async Task McpToolCallTransportFailure_IsNonReplayableInLegacyAndV3()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "MCP uncertain outcome" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var platform = new ToolPlatformService(db);
        await platform.SaveMcpServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "uncertain-mcp",
            McpTransportTypes.StreamableHttp,
            string.Empty,
            "[]",
            "https://mcp.example.test/rpc",
            "{}",
            "{}",
            true));
        using var client = new HttpClient(new McpToolCallDisconnectHandler());
        var service = new McpClientService(
            db,
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(client));
        var context = Context(chat.Id);
        const string arguments =
            """{"server":"uncertain-mcp","tool":"write","arguments":{"value":"x"}}""";

        var legacy = await new McpCallAgentTool(service)
            .ExecuteAsync(context, arguments);
        var v3 = await new McpCallToolV3(service)
            .ExecuteAsync(context, arguments, CancellationToken.None);

        AssertUncertain(legacy);
        AssertUncertain(v3);
    }

    private static void AssertUncertain(AgentToolResult result)
    {
        Assert.False(result.Success);
        Assert.True(result.OutcomeUncertain);
        Assert.True(result.MayHaveCommitted);
    }

    private static AgentToolExecutionContext Context(Guid? chatId = null) =>
        new(
            chatId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            AgentPermissionModes.BypassPermissions);

    private static SandboxCommandService NewSandbox() =>
        new(Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.OutcomeUncertainty.Tests",
            Guid.NewGuid().ToString("N")));

    private sealed class UncertainExecutionBackendRouter : IExecutionBackendRouter
    {
        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            string? backend = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ExecutionResult(
                backend ?? ToolExecutionBackends.RestrictedLocal,
                "C:\\workspace",
                -1,
                true,
                TimeSpan.FromSeconds(request.TimeoutSeconds),
                string.Empty,
                string.Empty,
                "Execution timed out.",
                OutcomeUncertain: true,
                MayHaveCommitted: true));

        public Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(
                new Dictionary<string, bool>());
    }

    private sealed class AllowNetworkSecurityService : INetworkSecurityService
    {
        public Task<Uri> ValidateAsync(
            string url,
            ToolPlatformSettings settings,
            CancellationToken ct = default,
            bool bypassRestrictions = false) =>
            Task.FromResult(new Uri(url));
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connection dropped after dispatch"));
    }

    private sealed class McpToolCallDisconnectHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "tools/call")
                throw new HttpRequestException("MCP response disconnected");
            if (!root.TryGetProperty("id", out var id))
                return Json(HttpStatusCode.Accepted, "{}");
            return Json(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id.GetInt32(),
                    result = new
                    {
                        protocolVersion = "2025-11-25",
                        capabilities = new { },
                        serverInfo = new { name = "test", version = "1" }
                    }
                }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
