using System.Net;
using System.Diagnostics;
using System.Text.Json;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class BuiltInAgentToolsTests
{
    [Fact]
    public async Task FileToolsWriteReadSearchAndBlockTraversal()
    {
        await using var db = TestDb.Create();
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var platform = new ToolPlatformService(db);
        var context = new AgentToolExecutionContext(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var write = new FileWriteAgentTool(sandbox, platform);
        var read = new FileReadAgentTool(sandbox, platform);
        var search = new FileSearchAgentTool(sandbox, platform);

        var writeResult = await write.ExecuteAsync(
            context,
            """{"path":"notes/test.txt","content":"alpha\nbeta"}""");
        Assert.True(writeResult.Success);
        Assert.Single(writeResult.Artifacts!);

        var readResult = await read.ExecuteAsync(
            context,
            """{"path":"notes/test.txt"}""");
        Assert.True(readResult.Success);
        Assert.Contains("alpha", readResult.Output);

        var searchResult = await search.ExecuteAsync(
            context,
            """{"query":"beta","path":"notes"}""");
        Assert.True(searchResult.Success);
        Assert.Contains("test.txt:2", searchResult.Output);

        var traversal = await read.ExecuteAsync(
            context,
            """{"path":"../outside.txt"}""");
        Assert.False(traversal.Success);
        Assert.Contains("escapes", traversal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistryPublishesOnlyProviderSafeNames()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N")));
        var registry = new AgentToolRegistry([new SandboxExecAgentTool(sandbox)]);

        Assert.All(registry.Definitions, definition =>
            Assert.Matches("^[a-zA-Z0-9_-]+$", definition.Name));
        Assert.True(registry.TryGet("sandbox.exec", out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    public void NetworkPolicyBlocksPrivateIpv4(string address)
    {
        Assert.True(NetworkSecurityService.IsPrivateOrLocal(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task McpStreamableHttpInitializesAndListsTools()
    {
        await using var db = TestDb.Create();
        var chat = new TLAHStudio.Core.Models.Chat { Title = "MCP" };
        db.Set<TLAHStudio.Core.Models.Chat>().Add(chat);
        await db.SaveChangesAsync();
        var platform = new ToolPlatformService(db);
        await platform.SaveMcpServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "test-http",
            TLAHStudio.Core.Models.McpTransportTypes.StreamableHttp,
            string.Empty,
            "[]",
            "https://mcp.example.com/mcp",
            "{}",
            "{}",
            true));

        var handler = new MapHttpMessageHandler(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            var json = reader.ReadToEnd();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
                return MapHttpMessageHandler.Json(HttpStatusCode.Accepted, "{}");
            var method = root.GetProperty("method").GetString();
            var result = method == "tools/list"
                ? """{"tools":[{"name":"echo","description":"Echo input","inputSchema":{"type":"object"}}]}"""
                : """{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"test","version":"1"}}""";
            var response = MapHttpMessageHandler.Json(
                HttpStatusCode.OK,
                $$"""{"jsonrpc":"2.0","id":{{id.GetInt32()}},"result":{{result}}}""");
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
            return response;
        });
        using var http = new HttpClient(handler);
        var service = new McpClientService(
            db,
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(http));

        var tools = await service.ListToolsAsync(chat.Id, "test-http");

        var tool = Assert.Single(tools);
        Assert.Equal("echo", tool.Name);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(handler.Requests, r =>
            r.Headers.TryGetValues("Mcp-Session-Id", out var values) &&
            values.Contains("session-1"));
    }

    [Fact]
    public void McpPythonStdioForcesUtf8WithoutOverridingUserValues()
    {
        var defaults = new ProcessStartInfo { FileName = "python" };
        McpClientService.ApplyStdioCompatibilityEnvironment(defaults);
        Assert.Equal("1", defaults.Environment["PYTHONUTF8"]);
        Assert.Equal("utf-8", defaults.Environment["PYTHONIOENCODING"]);

        var custom = new ProcessStartInfo { FileName = "python.exe" };
        custom.Environment["PYTHONIOENCODING"] = "utf-8:replace";
        McpClientService.ApplyStdioCompatibilityEnvironment(custom);
        Assert.Equal("utf-8:replace", custom.Environment["PYTHONIOENCODING"]);
    }

    [Fact]
    public async Task McpPythonStdioListsToolsWithChineseDescriptions()
    {
        if (!OperatingSystem.IsWindows() || !CommandExists("python"))
            return;

        var root = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.Mcp.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "server.py");
        await File.WriteAllTextAsync(
            script,
            """
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                method = request.get("method")
                if method == "notifications/initialized":
                    continue
                if method == "initialize":
                    result = {"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}
                else:
                    result = {"tools":[{"name":"clock","description":"获取当前时间","inputSchema":{"type":"object"}}]}
                print(json.dumps({"jsonrpc":"2.0","id":request["id"],"result":result}, ensure_ascii=False), flush=True)
            """,
            new System.Text.UTF8Encoding(false));

        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}")));
        var service = new McpClientService(
            db,
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(http));
        var tools = await service.TestServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "python-test",
            TLAHStudio.Core.Models.McpTransportTypes.Stdio,
            "python",
            JsonSerializer.Serialize(new[] { script }),
            string.Empty,
            "{}",
            "{}",
            true));

        var tool = Assert.Single(tools);
        Assert.Equal("clock", tool.Name);
        Assert.Equal("获取当前时间", tool.Description);
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class AllowNetworkSecurityService : INetworkSecurityService
    {
        public Task<Uri> ValidateAsync(
            string url,
            TLAHStudio.Core.Models.ToolPlatformSettings settings,
            CancellationToken ct = default) =>
            Task.FromResult(new Uri(url));
    }
}
