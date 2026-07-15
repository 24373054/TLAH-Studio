using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Background;
using TLAHStudio.Core.Services.Tools.PerTool;

namespace TLAHStudio.Core.Tests;

public sealed class ExternalPermissionParityTests
{
    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false, true)]
    [InlineData(AgentPermissionModes.RequestApproval, true, true)]
    [InlineData(AgentPermissionModes.RequestApproval, false, false)]
    [InlineData(AgentPermissionModes.AutoApprove, false, false)]
    public async Task McpHttpAgentTool_UsesEffectivePermissionModeForNetworkValidation(
        string permissionMode,
        bool explicitlyApproved,
        bool expectedBypass)
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "MCP permission parity" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();

        var platform = new ToolPlatformService(db);
        await platform.SaveMcpServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "private-mcp",
            McpTransportTypes.StreamableHttp,
            string.Empty,
            "[]",
            "http://127.0.0.1:31337/mcp",
            "{}",
            "{}",
            true));
        var network = new RecordingNetworkSecurityService();
        using var http = new HttpClient(new StubHttpMessageHandler(McpResponse));
        var service = new McpClientService(
            db,
            platform,
            network,
            new StaticHttpClientFactory(http));
        var tool = new McpListToolsAgentTool(service);
        var context = new AgentToolExecutionContext(
            chat.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            permissionMode,
            explicitlyApproved);

        var result = await tool.ExecuteAsync(context, """{"server":"private-mcp"}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedBypass, network.LastBypassRestrictions);
        Assert.Equal(1, network.ValidationCount);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, true)]
    [InlineData(AgentPermissionModes.RequestApproval, false)]
    [InlineData(AgentPermissionModes.AutoApprove, false)]
    public async Task RemoteBackend_ForwardsPermissionModeToValidationAndPayload(
        string permissionMode,
        bool expectedBypass)
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
            "http://127.0.0.1:31338/execute",
            string.Empty));
        var network = new RecordingNetworkSecurityService();
        string? requestBody = null;
        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK,
                """{"workingDirectory":"/workspace","exitCode":0,"timedOut":false,"stdout":"ok","stderr":""}""");
        }));
        var sandbox = new SandboxCommandService(Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.ExternalPermission.Tests",
            Guid.NewGuid().ToString("N")));
        var router = new ExecutionBackendRouter(
            sandbox,
            platform,
            network,
            new StaticHttpClientFactory(http));

        var result = await router.ExecuteAsync(
            new ExecutionRequest(Guid.NewGuid(), "Get-Date", 10, 8_000, permissionMode),
            ToolExecutionBackends.Remote);

        Assert.True(result.Success, result.BlockedReason);
        Assert.Equal(expectedBypass, network.LastBypassRestrictions);
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal(
            AgentPermissionModes.Normalize(permissionMode),
            payload.RootElement.GetProperty("permissionMode").GetString());
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false, AgentPermissionModes.BypassPermissions)]
    [InlineData(AgentPermissionModes.RequestApproval, true, AgentPermissionModes.BypassPermissions)]
    [InlineData(AgentPermissionModes.RequestApproval, false, AgentPermissionModes.RequestApproval)]
    [InlineData(AgentPermissionModes.AutoApprove, false, AgentPermissionModes.AutoApprove)]
    public async Task BackgroundCommand_ForwardsEffectivePermissionModeToBackend(
        string permissionMode,
        bool explicitlyApproved,
        string expectedMode)
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "background permission parity" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var sandbox = new SandboxCommandService(Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.BackgroundPermission.Tests",
            Guid.NewGuid().ToString("N")));
        var backend = new RecordingExecutionBackendRouter(sandbox.GetSandboxRoot(chat.Id));
        var tool = new TaskCreateAgentTool(
            new AgentTaskService(db),
            new BackgroundTaskService(db),
            sandbox,
            backend);
        var context = new AgentToolExecutionContext(
            chat.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            permissionMode,
            explicitlyApproved);

        var result = await tool.ExecuteAsync(
            context,
            """{"title":"background command","background":true,"command":"Get-Date"}""");
        var request = await backend.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedMode, request.PermissionMode);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false, AgentPermissionModes.BypassPermissions)]
    [InlineData(AgentPermissionModes.RequestApproval, true, AgentPermissionModes.BypassPermissions)]
    public async Task BackgroundCommand_UsesWorkerScopeForRouterLifetimeAndPermissionMode(
        string permissionMode,
        bool explicitlyApproved,
        string expectedMode)
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "background worker scope" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var sandbox = new SandboxCommandService(Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.BackgroundScope.Tests",
            Guid.NewGuid().ToString("N")));
        var capturedRouter = new ThrowingExecutionBackendRouter();
        var scopedRouter = new BlockingExecutionBackendRouter(sandbox.GetSandboxRoot(chat.Id));
        var scopeFactory = new SingleRouterScopeFactory(scopedRouter);
        var tool = new TaskCreateAgentTool(
            new AgentTaskService(db),
            new BackgroundTaskService(db),
            sandbox,
            capturedRouter,
            scopeFactory);
        var context = new AgentToolExecutionContext(
            chat.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            permissionMode,
            explicitlyApproved);

        var result = await tool.ExecuteAsync(
            context,
            """{"title":"scoped background command","background":true,"command":"Get-Date"}""");
        var request = await scopedRouter.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedMode, request.PermissionMode);
        Assert.Equal(0, capturedRouter.ExecutionCount);
        Assert.False(scopeFactory.Disposed.Task.IsCompleted);

        scopedRouter.Release.TrySetResult();
        await scopeFactory.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scopeFactory.ScopeCount);
    }

    [Fact]
    public async Task BackgroundCommand_FullModeStillRejectsImmutableCatastrophicCommand()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "background hard boundary" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var sandbox = new SandboxCommandService(Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.BackgroundSafety.Tests",
            Guid.NewGuid().ToString("N")));
        var backend = new RecordingExecutionBackendRouter(sandbox.GetSandboxRoot(chat.Id));
        var tool = new TaskCreateAgentTool(
            new AgentTaskService(db),
            new BackgroundTaskService(db),
            sandbox,
            backend);
        var context = new AgentToolExecutionContext(
            chat.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            AgentPermissionModes.BypassPermissions);

        var result = await tool.ExecuteAsync(
            context,
            """{"title":"unsafe","background":true,"command":"rm -rf /"}""");

        Assert.False(result.Success);
        Assert.False(backend.Request.Task.IsCompleted);
        Assert.Empty(db.Set<AgentTaskItem>());
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false, AgentPermissionModes.BypassPermissions)]
    [InlineData(AgentPermissionModes.RequestApproval, true, AgentPermissionModes.BypassPermissions)]
    [InlineData(AgentPermissionModes.AutoApprove, false, AgentPermissionModes.AutoApprove)]
    public async Task McpV3_ForwardsEffectivePermissionMode(
        string permissionMode,
        bool explicitlyApproved,
        string expectedMode)
    {
        var mcp = new RecordingMcpClientService();
        var tool = new McpCallToolV3(mcp);
        var context = new AgentToolExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            12_000,
            permissionMode,
            explicitlyApproved);

        var result = await tool.ExecuteAsync(
            context,
            """{"server":"demo","tool":"echo","arguments":{"value":"ok"}}""",
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expectedMode, mcp.LastPermissionMode);
    }

    private static HttpResponseMessage McpResponse(HttpRequestMessage request)
    {
        var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id))
            return JsonResponse(HttpStatusCode.Accepted, "{}");
        var method = root.GetProperty("method").GetString();
        var result = method == "tools/list"
            ? """{"tools":[{"name":"echo","description":"Echo","inputSchema":{"type":"object"}}]}"""
            : """{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"test","version":"1"}}""";
        return JsonResponse(
            HttpStatusCode.OK,
            $$"""{"jsonrpc":"2.0","id":{{id.GetInt32()}},"result":{{result}}}""");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingNetworkSecurityService : INetworkSecurityService
    {
        public bool LastBypassRestrictions { get; private set; }
        public int ValidationCount { get; private set; }

        public Task<Uri> ValidateAsync(
            string url,
            ToolPlatformSettings settings,
            CancellationToken ct = default,
            bool bypassRestrictions = false)
        {
            LastBypassRestrictions = bypassRestrictions;
            ValidationCount++;
            return Task.FromResult(new Uri(url));
        }
    }

    private sealed class RecordingExecutionBackendRouter(string workingDirectory) : IExecutionBackendRouter
    {
        public TaskCompletionSource<ExecutionRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            string? backend = null,
            CancellationToken ct = default)
        {
            Request.TrySetResult(request);
            return Task.FromResult(new ExecutionResult(
                backend ?? ToolExecutionBackends.RestrictedLocal,
                workingDirectory,
                0,
                false,
                TimeSpan.Zero,
                "ok",
                string.Empty));
        }

        public Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(
                new Dictionary<string, bool> { [ToolExecutionBackends.RestrictedLocal] = true });
    }

    private sealed class ThrowingExecutionBackendRouter : IExecutionBackendRouter
    {
        public int ExecutionCount { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            string? backend = null,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            throw new InvalidOperationException("The foreground-scoped router must not be captured by the worker.");
        }

        public Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
    }

    private sealed class BlockingExecutionBackendRouter(string workingDirectory) : IExecutionBackendRouter
    {
        public TaskCompletionSource<ExecutionRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExecutionResult> ExecuteAsync(
            ExecutionRequest request,
            string? backend = null,
            CancellationToken ct = default)
        {
            Request.TrySetResult(request);
            await Release.Task.WaitAsync(ct);
            return new ExecutionResult(
                backend ?? ToolExecutionBackends.RestrictedLocal,
                workingDirectory,
                0,
                false,
                TimeSpan.Zero,
                "ok",
                string.Empty);
        }

        public Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(
                new Dictionary<string, bool> { [ToolExecutionBackends.RestrictedLocal] = true });
    }

    private sealed class SingleRouterScopeFactory(IExecutionBackendRouter router) : IServiceScopeFactory
    {
        public int ScopeCount { get; private set; }
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope()
        {
            ScopeCount++;
            return new RouterScope(router, Disposed);
        }
    }

    private sealed class RouterScope(
        IExecutionBackendRouter router,
        TaskCompletionSource disposed) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new RouterServiceProvider(router);

        public void Dispose() => disposed.TrySetResult();

        public ValueTask DisposeAsync()
        {
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RouterServiceProvider(IExecutionBackendRouter router) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IExecutionBackendRouter) ? router : null;
    }

    private sealed class RecordingMcpClientService : IMcpClientService
    {
        public string? LastPermissionMode { get; private set; }

        public Task<IReadOnlyList<McpToolInfo>> TestServerAsync(
            McpServerConfigDto server,
            CancellationToken ct = default,
            string permissionMode = AgentPermissionModes.RequestApproval) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
            Guid chatId,
            string? serverName = null,
            CancellationToken ct = default,
            string permissionMode = AgentPermissionModes.RequestApproval) =>
            Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
            Guid chatId,
            string? serverName = null,
            CancellationToken ct = default,
            string permissionMode = AgentPermissionModes.RequestApproval) =>
            Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(
            Guid chatId,
            string serverName,
            string uri,
            CancellationToken ct = default,
            string permissionMode = AgentPermissionModes.RequestApproval) =>
            Task.FromResult("ok");

        public Task<string> CallToolAsync(
            Guid chatId,
            string serverName,
            string toolName,
            JsonElement arguments,
            CancellationToken ct = default,
            string permissionMode = AgentPermissionModes.RequestApproval)
        {
            LastPermissionMode = permissionMode;
            return Task.FromResult("ok");
        }
    }
}
