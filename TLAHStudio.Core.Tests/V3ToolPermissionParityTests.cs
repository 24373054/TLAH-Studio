using System.Net;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Memory;
using TLAHStudio.Core.Services.Tools.PerTool;

namespace TLAHStudio.Core.Tests;

public sealed class V3ToolPermissionParityTests
{
    [Fact]
    public async Task FileV3_ExactApprovalAndFullModeCanUseHostPathsWithoutSecondarySandboxBlock()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        var hostDirectory = Path.Combine(temp.Path, "host");
        Directory.CreateDirectory(hostDirectory);
        var existingFile = Path.Combine(hostDirectory, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "visible");

        var list = new FileListToolV3(sandbox);
        var listArguments = JsonSerializer.Serialize(new { path = hostDirectory });
        var ordinaryList = await list.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval),
            listArguments,
            CancellationToken.None);
        var approvedList = await list.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, explicitlyApproved: true),
            listArguments,
            CancellationToken.None);
        var fullList = await list.ExecuteAsync(
            Context(AgentPermissionModes.BypassPermissions),
            listArguments,
            CancellationToken.None);

        Assert.False(ordinaryList.Success);
        Assert.Contains("Full access", ordinaryList.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(approvedList.Success, approvedList.Error);
        Assert.Contains("existing.txt", approvedList.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(fullList.Success, fullList.Error);

        var write = new FileWriteToolV3(sandbox, platform);
        var hostOutput = Path.Combine(hostDirectory, "approved.txt");
        var writeArguments = JsonSerializer.Serialize(new { path = hostOutput, content = "approved" });
        var ordinaryWrite = await write.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval),
            writeArguments,
            CancellationToken.None);

        Assert.False(ordinaryWrite.Success);
        Assert.False(File.Exists(hostOutput));

        var approvedWrite = await write.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, explicitlyApproved: true),
            writeArguments,
            CancellationToken.None);

        Assert.True(approvedWrite.Success, approvedWrite.Error);
        Assert.Equal("approved", await File.ReadAllTextAsync(hostOutput));
    }

    [Fact]
    public async Task CodeEditV3_ExactApprovalCanEditHostPathButOrdinaryAskCannot()
    {
        using var temp = new TemporaryDirectory();
        var hostFile = Path.Combine(temp.Path, "host-edit.txt");
        await File.WriteAllTextAsync(hostFile, "before");
        var tool = new CodeEditToolV3();
        var arguments = JsonSerializer.Serialize(new
        {
            path = hostFile,
            old_string = "before",
            new_string = "after"
        });

        var ordinary = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval),
            arguments,
            CancellationToken.None);
        var approved = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, explicitlyApproved: true),
            arguments,
            CancellationToken.None);

        Assert.False(ordinary.Success);
        Assert.Contains("exact approved", ordinary.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(approved.Success, approved.Error);
        Assert.Equal("after", await File.ReadAllTextAsync(hostFile));
    }

    [Fact]
    public async Task V3ApprovalMetadata_MatchesRegisteredToolContracts()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.OK, "{}")));
        var network = new RecordingNetworkSecurityService();
        var clients = new StaticHttpClientFactory(http);

        Assert.True(new FileListToolV3(sandbox).RequiresApproval);
        Assert.True(new FileReadToolV3(sandbox, platform).RequiresApproval);
        Assert.True(new FileWriteToolV3(sandbox, platform).RequiresApproval);
        Assert.True(new WebSearchToolV3(platform, network, clients).RequiresApproval);
        Assert.True(new BrowserReadToolV3(platform, network, clients).RequiresApproval);
        Assert.True(new CodeEditToolV3().RequiresApproval);
        Assert.False(new CodeReadToolV3().RequiresApproval);
        Assert.True(new MemoryWriteToolV3(null!).RequiresApproval);
    }

    [Fact]
    public async Task V3Lifecycle_PreservesBypassImmuneAndImmutableSafetyMetadata()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        var tool = new GitToolV3(sandbox);
        var runner = new DefaultToolLifecycleRunner(new AgentToolRegistry([tool]), sandbox);
        var chatId = Guid.NewGuid();

        var sensitive = await runner.PreviewAsync(
            chatId,
            tool.Definition.Name,
            """{"operation":"rebase","arguments":["main"]}""");
        var catastrophic = await runner.PreviewAsync(
            chatId,
            tool.Definition.Name,
            """{"operation":"status","arguments":["; rm -rf /"]}""");

        Assert.True(sensitive.Safety.BypassImmune);
        var autoDecision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.AutoApprove,
            sensitive.Safety,
            policy: null,
            sensitive.Metadata.RequiresApproval,
            sensitive.Metadata.RequiresUserInteraction,
            sensitive.Metadata.IsDestructive,
            isPlanMode: false,
            autoApproveTools: true);
        Assert.True(autoDecision.RequiresApproval);

        Assert.True(catastrophic.Safety.IsBlocked);
        Assert.False(catastrophic.Safety.CanOverrideBlock);
        var fullDecision = ToolAuthorizationPolicy.Evaluate(
            AgentPermissionModes.BypassPermissions,
            catastrophic.Safety,
            policy: null,
            catastrophic.Metadata.RequiresApproval,
            catastrophic.Metadata.RequiresUserInteraction,
            catastrophic.Metadata.IsDestructive,
            isPlanMode: false,
            autoApproveTools: false);
        Assert.True(fullDecision.IsBlocked);
    }

    [Fact]
    public async Task V3Policy_PlanAllowsSafeInspectionButPausesWritesAndExactApprovalOverridesHostRestriction()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        await using var db = TestDb.Create();
        var list = new FileListToolV3(sandbox);
        var write = new FileWriteToolV3(sandbox, new ToolPlatformService(db));
        var runner = new DefaultToolLifecycleRunner(
            new AgentToolRegistry([list, write]),
            sandbox);
        var chatId = Guid.NewGuid();

        var safeRead = await runner.PreviewAsync(
            chatId,
            list.Definition.Name,
            """{"path":"."}""");
        var writePreview = await runner.PreviewAsync(
            chatId,
            write.Definition.Name,
            """{"path":"planned.txt","content":"planned"}""");
        var hostRead = await runner.PreviewAsync(
            chatId,
            list.Definition.Name,
            JsonSerializer.Serialize(new { path = temp.Path }));

        var planReadDecision = Decide(
            AgentPermissionModes.Plan,
            safeRead,
            explicitlyApproved: false);
        var planWriteDecision = Decide(
            AgentPermissionModes.Plan,
            writePreview,
            explicitlyApproved: false);
        var exactHostDecision = Decide(
            AgentPermissionModes.RequestApproval,
            hostRead,
            explicitlyApproved: true);

        Assert.False(planReadDecision.IsBlocked);
        Assert.False(planReadDecision.RequiresApproval);
        Assert.True(planWriteDecision.RequiresApproval);
        Assert.False(exactHostDecision.IsBlocked);
        Assert.False(exactHostDecision.RequiresApproval);
        Assert.Equal("explicit_user_approval", exactHostDecision.ReasonCode);
    }

    [Fact]
    public void HttpV3Schema_DescribesTheRequestThatExecutionActuallySends()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, "{}")));
        using var db = TestDb.Create();
        var tool = new HttpRequestToolV3(
            new ToolPlatformService(db),
            new RecordingNetworkSecurityService(),
            new StaticHttpClientFactory(http));

        var properties = Assert.IsType<Dictionary<string, object>>(
            tool.Definition.InputSchema["properties"]);
        Assert.Contains("method", properties.Keys);
        Assert.Contains("body", properties.Keys);
        Assert.Contains("headers", properties.Keys);
        Assert.Contains("content_type", properties.Keys);
        Assert.Contains("credential", properties.Keys);
        var method = Assert.IsType<Dictionary<string, object>>(properties["method"]);
        var methods = Assert.IsType<string[]>(method["enum"]);
        Assert.Contains("HEAD", methods);
        Assert.Contains("PATCH", methods);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false, true)]
    [InlineData(AgentPermissionModes.RequestApproval, true, true)]
    [InlineData(AgentPermissionModes.RequestApproval, false, false)]
    public async Task HttpV3_SendsMethodBodyAndHeadersWithEffectivePermission(
        string permissionMode,
        bool explicitlyApproved,
        bool expectedNetworkBypass)
    {
        await using var db = TestDb.Create();
        var network = new RecordingNetworkSecurityService();
        string? actualMethod = null;
        string? actualBody = null;
        string? actualHeader = null;
        string? actualContentType = null;
        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            actualMethod = request.Method.Method;
            actualBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            actualHeader = request.Headers.GetValues("X-TLAH-Test").Single();
            actualContentType = request.Content.Headers.ContentType?.MediaType;
            return JsonResponse(HttpStatusCode.Created, "{\"saved\":true}");
        }));
        var tool = new HttpRequestToolV3(
            new ToolPlatformService(db),
            network,
            new StaticHttpClientFactory(http));

        var result = await tool.ExecuteAsync(
            Context(permissionMode, explicitlyApproved),
            """
            {
              "url": "https://example.test/items/42",
              "method": "PATCH",
              "body": "{\"name\":\"TLAH\"}",
              "content_type": "application/json",
              "headers": {
                "X-TLAH-Test": "v3",
                "Content-Type": "application/merge-patch+json"
              }
            }
            """,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("PATCH", actualMethod);
        Assert.Equal("{\"name\":\"TLAH\"}", actualBody);
        Assert.Equal("v3", actualHeader);
        Assert.Equal("application/merge-patch+json", actualContentType);
        Assert.Equal(expectedNetworkBypass, network.LastBypassRestrictions);
        Assert.Contains("HTTP 201", result.Output);
        Assert.Contains("{\"saved\":true}", result.Output);
    }

    [Fact]
    public async Task HttpV3_RejectsTransportHeadersAndInvalidMethodsBeforeSending()
    {
        await using var db = TestDb.Create();
        var requestCount = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return JsonResponse(HttpStatusCode.OK, "{}");
        }));
        var tool = new HttpRequestToolV3(
            new ToolPlatformService(db),
            new RecordingNetworkSecurityService(),
            new StaticHttpClientFactory(http));

        var badMethod = await tool.ExecuteAsync(
            Context(AgentPermissionModes.BypassPermissions),
            """{"url":"https://example.test","method":"TRACE"}""",
            CancellationToken.None);
        var badHeader = await tool.ExecuteAsync(
            Context(AgentPermissionModes.BypassPermissions),
            """{"url":"https://example.test","headers":{"Host":"elsewhere.test"}}""",
            CancellationToken.None);

        Assert.False(badMethod.Success);
        Assert.Contains("not allowed", badMethod.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(badHeader.Success);
        Assert.Contains("controlled by the transport", badHeader.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public void GitV3_UsesStructuredTypedSchemaAndKernelClassification()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        var tool = new GitToolV3(sandbox);
        var properties = Assert.IsType<Dictionary<string, object>>(
            tool.Definition.InputSchema["properties"]);

        Assert.Contains("operation", properties.Keys);
        Assert.Contains("arguments", properties.Keys);
        Assert.Contains("path", properties.Keys);
        Assert.DoesNotContain("command", properties.Keys);
        var operationSchema = Assert.IsType<Dictionary<string, object>>(properties["operation"]);
        var operations = Assert.IsType<string[]>(operationSchema["enum"]);
        Assert.Contains("rebase", operations);

        var status = ToolSafetyKernel.Assess(
            sandbox,
            Guid.NewGuid(),
            tool.Definition.Name,
            """{"operation":"status","arguments":[]}""");
        var injection = ToolSafetyKernel.Assess(
            sandbox,
            Guid.NewGuid(),
            tool.Definition.Name,
            """{"operation":"status","arguments":["; rm -rf /"]}""");
        var integrating = ToolSafetyKernel.Assess(
            sandbox,
            Guid.NewGuid(),
            tool.Definition.Name,
            """{"operation":"rebase","arguments":["main"]}""");

        Assert.Equal(ToolSafetyLevels.Low, status.Level);
        Assert.True(status.IsReadOnly);
        Assert.True(injection.IsBlocked);
        Assert.False(injection.CanOverrideBlock);
        Assert.Equal(ToolSafetyLevels.High, integrating.Level);
        Assert.True(integrating.BypassImmune);
    }

    [Fact]
    public async Task GitV3_ExactApprovalCanUseHostRepositoryWithoutSandboxRejection()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        var hostRepository = Path.Combine(temp.Path, "host-repository");
        Directory.CreateDirectory(hostRepository);
        var tool = new GitToolV3(sandbox);
        var arguments = JsonSerializer.Serialize(new { operation = "init", path = hostRepository });

        var ordinary = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, explicitlyApproved: false),
            arguments,
            CancellationToken.None);
        var approved = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, explicitlyApproved: true),
            arguments,
            CancellationToken.None);

        Assert.False(ordinary.Success);
        Assert.Contains("Absolute paths", ordinary.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(approved.Success, approved.Error);
        Assert.True(Directory.Exists(Path.Combine(hostRepository, ".git")));
    }

    [Fact]
    public async Task GitV3_FullModeStillRejectsShellMetacharacterInjection()
    {
        using var temp = new TemporaryDirectory();
        var sandbox = new SandboxCommandService(Path.Combine(temp.Path, "sandbox"));
        var tool = new GitToolV3(sandbox);

        var result = await tool.ExecuteAsync(
            Context(AgentPermissionModes.BypassPermissions),
            """{"operation":"status","arguments":["--short; Remove-Item -Recurse C:\\\\"]}""",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("metacharacters", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentToolExecutionContext Context(
        string permissionMode,
        bool explicitlyApproved = false) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            30,
            12_000,
            permissionMode,
            explicitlyApproved);

    private static ToolAuthorizationDecision Decide(
        string mode,
        ToolLifecyclePreview preview,
        bool explicitlyApproved) =>
        ToolAuthorizationPolicy.Evaluate(
            mode,
            preview.Safety,
            policy: null,
            preview.Metadata.RequiresApproval,
            preview.Metadata.RequiresUserInteraction,
            preview.Metadata.IsDestructive,
            isPlanMode: mode == AgentPermissionModes.Plan,
            autoApproveTools: AgentPermissionModes.IsAutoApprove(mode),
            explicitlyApproved);

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

        public Task<Uri> ValidateAsync(
            string url,
            ToolPlatformSettings settings,
            CancellationToken ct = default,
            bool bypassRestrictions = false)
        {
            LastBypassRestrictions = bypassRestrictions;
            return Task.FromResult(new Uri(url));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TLAHStudio.V3Tool.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
