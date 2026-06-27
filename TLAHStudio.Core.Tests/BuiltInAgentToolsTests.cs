using System.Net;
using System.Diagnostics;
using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
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
        var send = new FileSendAgentTool(sandbox);

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

        var sendResult = await send.ExecuteAsync(
            context,
            """{"path":"notes/test.txt","caption":"Download this."}""");
        Assert.True(sendResult.Success);
        var artifact = Assert.Single(sendResult.Artifacts!);
        Assert.Equal("notes" + Path.DirectorySeparatorChar + "test.txt", artifact.RelativePath);
        Assert.Equal("text/plain", artifact.ContentType);

        var traversal = await read.ExecuteAsync(
            context,
            """{"path":"../outside.txt"}""");
        Assert.False(traversal.Success);
        Assert.Contains("escapes", traversal.Error, StringComparison.OrdinalIgnoreCase);

        var sendTraversal = await send.ExecuteAsync(
            context,
            """{"path":"../outside.txt"}""");
        Assert.False(sendTraversal.Success);
        Assert.Contains("escapes", sendTraversal.Error, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ToolPlatformV2MetadataClassifiesReadAndWriteTools()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N")));
        using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        var registry = new AgentToolRegistry(
        [
            new FileListAgentTool(sandbox),
            new FileReadAgentTool(sandbox, platform),
            new FileWriteAgentTool(sandbox, platform),
            new SandboxExecAgentTool(sandbox)
        ]);

        Assert.True(registry.TryGet(AgentToolNames.FileRead, out var read));
        Assert.True(read.Metadata.IsReadOnly);
        Assert.True(read.Metadata.IsConcurrencySafe);
        Assert.Equal(AgentToolRenderHints.File, read.Metadata.RenderHint);

        Assert.True(registry.TryGet(AgentToolNames.FileWrite, out var write));
        Assert.False(write.Metadata.IsReadOnly);
        Assert.False(write.Metadata.IsConcurrencySafe);
        Assert.Equal(AgentToolResultPersistenceModes.Artifact, write.Metadata.ResultPersistence);

        Assert.True(registry.TryGet(AgentToolNames.SandboxExec, out var shell));
        Assert.True(shell.Metadata.IsDestructive);
        Assert.Equal(AgentToolRenderHints.Terminal, shell.Metadata.RenderHint);
    }

    [Fact]
    public void ToolExecutionSchedulerBatchesReadOnlyToolsConcurrentlyAndWritesSerially()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N")));
        using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        var registry = new AgentToolRegistry(
        [
            new FileListAgentTool(sandbox),
            new FileReadAgentTool(sandbox, platform),
            new FileWriteAgentTool(sandbox, platform)
        ]);
        var scheduler = new ToolExecutionScheduler(registry, sandbox);

        var batches = scheduler.PlanBatches(
        [
            new ToolExecutionPlanItem(AgentToolNames.FileList, "{}"),
            new ToolExecutionPlanItem(AgentToolNames.FileRead, """{"path":"a.txt"}"""),
            new ToolExecutionPlanItem(AgentToolNames.FileWrite, """{"path":"a.txt","content":"x"}"""),
            new ToolExecutionPlanItem(AgentToolNames.FileRead, """{"path":"a.txt"}""")
        ]);

        Assert.Equal(3, batches.Count);
        Assert.True(batches[0].Concurrent);
        Assert.Equal(2, batches[0].Items.Count);
        Assert.False(batches[1].Concurrent);
        Assert.Equal(AgentToolNames.FileWrite, batches[1].Items[0].ToolName);
        Assert.True(batches[2].Concurrent);
    }

    [Fact]
    public void AgentContextManagerCompactsMiddleMessagesAndTrimsToolResults()
    {
        var manager = new AgentContextManager();
        var messages = Enumerable.Range(0, 40)
            .Select(i => new MessagePayload(i % 2 == 0 ? "user" : "assistant", new string('x', 1200) + $" {i}"))
            .ToList();
        messages.Add(new MessagePayload("tool", new string('z', 8000), "call-1"));

        var prepared = manager.Prepare(
            messages,
            new AgentContextOptions(
                ContextBudgetTokens: 2000,
                AutoCompactTriggerTokens: 1000,
                PreserveHeadMessages: 2,
                PreserveTailMessages: 4,
                MaxToolResultCharsInContext: 400),
            forceCompact: false);

        Assert.True(prepared.WasCompacted);
        Assert.True(prepared.EstimatedTokensAfter < prepared.EstimatedTokensBefore);
        Assert.Contains(prepared.Messages, m => m.Content.Contains("context summary boundary", StringComparison.Ordinal));
        Assert.Contains("tool result preview truncated", prepared.Messages.Last().Content);
    }

    [Fact]
    public async Task ToolResultPersistencePersistsLargeToolOutput()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Context.Tests", Guid.NewGuid().ToString("N")));
        var chatId = Guid.NewGuid();
        var invocation = new ToolInvocation
        {
            Id = Guid.NewGuid(),
            ToolName = AgentToolNames.TerminalExec
        };
        var service = new ToolResultPersistenceService();

        var persisted = await service.PersistForContextAsync(
            sandbox,
            chatId,
            invocation,
            new AgentToolResult(true, new string('a', 5000)),
            500);

        Assert.True(persisted.Persisted);
        Assert.NotNull(persisted.PersistedArtifact);
        Assert.Contains("[persisted-output:", persisted.ContextResult.Output);
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chatId), persisted.PersistedPath!)));
    }

    [Fact]
    public async Task MemoryToolsReadAndWriteProjectMemory()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "Memory" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var memory = new ProjectMemoryService(db);
        var context = new AgentToolExecutionContext(chat.Id, Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var write = new MemoryWriteAgentTool(memory);
        var read = new MemoryReadAgentTool(memory);

        var writeResult = await write.ExecuteAsync(
            context,
            """{"content":"# Project Memory\n\n- Prefer concise release notes.","append":false,"reason":"Remember a stable preference."}""");
        Assert.True(writeResult.Success);

        var readResult = await read.ExecuteAsync(context, """{"reason":"Verify memory."}""");
        Assert.True(readResult.Success);
        Assert.Contains("Prefer concise release notes", readResult.Output);
    }

    [Fact]
    public async Task WorkspaceCodeToolsEditReadGrepAndRollback()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.CodeTools.Tests", Guid.NewGuid().ToString("N")));
        var context = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var edit = new CodeEditAgentTool(sandbox);
        var read = new CodeReadAgentTool(sandbox);
        var grep = new CodeGrepAgentTool(sandbox);
        var rollback = new CodeRollbackAgentTool(sandbox);

        var create = await edit.ExecuteAsync(
            context,
            """{"path":"src/hello.cs","new_text":"class Hello { }\n","create_if_missing":true,"reason":"Create test file."}""");
        Assert.True(create.Success, create.Error);
        Assert.Contains("Backup:", create.Output);

        var readResult = await read.ExecuteAsync(context, """{"path":"src/hello.cs","start_line":1,"line_count":5}""");
        Assert.True(readResult.Success);
        Assert.Contains("class Hello", readResult.Output);

        var grepResult = await grep.ExecuteAsync(context, """{"query":"Hello","path":"src"}""");
        Assert.True(grepResult.Success);
        Assert.Contains("hello.cs:1", grepResult.Output);

        var backupId = create.Output
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.StartsWith("Backup:", StringComparison.Ordinal))
            .Replace("Backup:", string.Empty, StringComparison.Ordinal)
            .Trim();
        var rollbackResult = await rollback.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = "src/hello.cs", backup_id = backupId, reason = "Undo test edit." }));
        Assert.True(rollbackResult.Success, rollbackResult.Error);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(context.ChatId), "src", "hello.cs")));
    }

    [Fact]
    public void ToolProtocolGuardRepairsLegacyNamesAndOrphanToolResults()
    {
        var messages = new List<MessagePayload>
        {
            new("assistant", "", ToolCalls:
            [
                new LlmToolCall("call-1", AgentToolNames.LegacySandboxExec, """{"command":"Get-ChildItem"}""")
            ]),
            new("tool", "orphaned result", "missing-call"),
            new("system", "inline system")
        };

        var result = ToolProtocolGuard.RepairForProvider(messages, []);

        Assert.False(result.IsRejected);
        Assert.Equal(AgentToolNames.SandboxExec, result.Messages[0].ToolCalls![0].Name);
        Assert.Equal("user", result.Messages[1].Role);
        Assert.Contains("[tool result]", result.Messages[1].Content);
        Assert.Equal("user", result.Messages[2].Role);
        Assert.Contains("tool_name_normalized", result.Issues.Select(i => i.Code));
        Assert.Contains("orphan_tool_result", result.Issues.Select(i => i.Code));
    }

    [Fact]
    public async Task ToolSafetyKernelClassifiesFileWriteAndBlocksEscapingPaths()
    {
        await using var db = TestDb.Create();
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Safety.Tests", Guid.NewGuid().ToString("N")));
        var chatId = Guid.NewGuid();
        var root = sandbox.GetSandboxRoot(chatId);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "old");

        var safe = ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.FileWrite,
            """{"path":"notes.txt","content":"new"}""");
        Assert.Equal(ToolSafetyLevels.Medium, safe.Level);
        Assert.True(safe.IsWriteOperation);
        using (var preview = JsonDocument.Parse(safe.PreviewJson))
        {
            var diff = preview.RootElement.GetProperty("diff").GetString() ?? string.Empty;
            Assert.Contains("- old", diff);
            Assert.Contains("+ new", diff);
        }

        var blocked = ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.FileWrite,
            """{"path":"../outside.txt","content":"bad"}""");
        Assert.True(blocked.IsBlocked);
        Assert.Equal(ToolSafetyLevels.Blocked, blocked.Level);
    }

    [Fact]
    public void ToolSafetyKernelRequiresApprovalForDangerousTerminalCommands()
    {
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Safety.Tests", Guid.NewGuid().ToString("N")));
        var assessment = ToolSafetyKernel.Assess(
            sandbox,
            Guid.NewGuid(),
            AgentToolNames.TerminalExec,
            """{"command":"git reset --hard","reason":"Reset state."}""");

        Assert.Equal(ToolSafetyLevels.High, assessment.Level);
        Assert.True(assessment.RequiresExplicitApproval);
        Assert.False(assessment.IsBlocked);
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
