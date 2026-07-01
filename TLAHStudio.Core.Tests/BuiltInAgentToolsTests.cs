using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Background;

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
    public async Task FileManagementToolsInspectCreateMoveCopyAndDelete()
    {
        await using var db = TestDb.Create();
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.FileManagement.Tests", Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var platform = new ToolPlatformService(db);
        var context = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var mkdir = new FileMkdirAgentTool(sandbox);
        var write = new FileWriteAgentTool(sandbox, platform);
        var info = new FileInfoAgentTool(sandbox);
        var move = new FileMoveAgentTool(sandbox);
        var delete = new FileDeleteAgentTool(sandbox);

        var created = await mkdir.ExecuteAsync(context, """{"path":"notes/archive"}""");
        Assert.True(created.Success, created.Error);
        var written = await write.ExecuteAsync(context, """{"path":"notes/a.txt","content":"alpha\nbeta"}""");
        Assert.True(written.Success, written.Error);

        var inspected = await info.ExecuteAsync(context, """{"path":"notes/a.txt"}""");
        Assert.True(inspected.Success, inspected.Error);
        Assert.Contains("SHA256:", inspected.Output);
        Assert.Contains("Lines: 2", inspected.Output);

        var copied = await move.ExecuteAsync(context, """{"from_path":"notes/a.txt","to_path":"notes/archive/b.txt","mode":"copy"}""");
        Assert.True(copied.Success, copied.Error);
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(context.ChatId), "notes", "archive", "b.txt")));

        var moved = await move.ExecuteAsync(context, """{"from_path":"notes/archive/b.txt","to_path":"notes/c.txt","mode":"move"}""");
        Assert.True(moved.Success, moved.Error);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(context.ChatId), "notes", "archive", "b.txt")));
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(context.ChatId), "notes", "c.txt")));

        var deleted = await delete.ExecuteAsync(context, """{"path":"notes/archive","recursive":true}""");
        Assert.True(deleted.Success, deleted.Error);
        Assert.False(Directory.Exists(Path.Combine(sandbox.GetSandboxRoot(context.ChatId), "notes", "archive")));

        var rootDelete = await delete.ExecuteAsync(context, """{"path":"."}""");
        Assert.False(rootDelete.Success);
        Assert.Contains("root", rootDelete.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileSearchSupportsRegexCaseAndBinarySkipping()
    {
        await using var db = TestDb.Create();
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.FileSearch.Tests", Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var platform = new ToolPlatformService(db);
        var context = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var workspace = sandbox.GetSandboxRoot(context.ChatId);
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "App.cs"), "public class AlphaService {}\npublic void RunBeta() {}\n");
        await File.WriteAllBytesAsync(Path.Combine(workspace, "src", "image.png"), Encoding.UTF8.GetBytes("RunBeta"));
        var search = new FileSearchAgentTool(sandbox, platform);

        var result = await search.ExecuteAsync(
            context,
            """{"query":"Run[A-Z][a-z]+","path":"src","glob":"*","regex":true,"case_sensitive":true,"max_results":10}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("App.cs:2", result.Output);
        Assert.DoesNotContain("image.png", result.Output);
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
    public async Task TaskToolsPersistTodosAndListAcrossServiceInstances()
    {
        await using var db = TestDb.Create();
        var chatId = Guid.NewGuid();
        db.Set<Chat>().Add(new Chat { Id = chatId, Title = "tasks" });
        await db.SaveChangesAsync();
        var context = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var taskService = new AgentTaskService(db);
        var todo = new TodoWriteAgentTool(taskService);
        var list = new TaskListAgentTool(new AgentTaskService(db));

        var result = await todo.ExecuteAsync(context,
            """
            {"todos":[
              {"title":"Stabilize lifecycle","status":"in_progress","priority":"high"},
              {"title":"Verify release","status":"pending","priority":"medium"}
            ]}
            """);

        Assert.True(result.Success, result.Error);
        var listed = await list.ExecuteAsync(context, """{"include_completed":true}""");
        Assert.True(listed.Success, listed.Error);
        Assert.Contains("Stabilize lifecycle", listed.Output);
        Assert.Contains("Verify release", listed.Output);
    }

    [Fact]
    public async Task ReadPersistedOutputReadsOnlyToolResultFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var chatId = Guid.NewGuid();
        var dir = Path.Combine(sandbox.GetSandboxRoot(chatId), ".tlah_context", "tool-results");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "large.txt"), "alpha beta gamma");
        var tool = new ReadPersistedOutputAgentTool(sandbox);
        var context = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12000);

        var ok = await tool.ExecuteAsync(context, """{"path":".tlah_context/tool-results/large.txt"}""");
        Assert.True(ok.Success, ok.Error);
        Assert.Contains("alpha beta", ok.Output);

        var blocked = await tool.ExecuteAsync(context, """{"path":"notes/large.txt"}""");
        Assert.False(blocked.Success);
        Assert.Contains("tool-results", blocked.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolSearchFindsTaskAndPersistedOutputTools()
    {
        var tool = new ToolSearchAgentTool();
        var result = await tool.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000),
            """{"query":"task output persisted"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains(AgentToolNames.TaskOutput, result.Output);
        Assert.Contains(AgentToolNames.ReadPersistedOutput, result.Output);

        var fileResult = await tool.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000),
            """{"query":"delete folder symbol outline"}""");
        Assert.True(fileResult.Success, fileResult.Error);
        Assert.Contains(AgentToolNames.FileDelete, fileResult.Output);
        Assert.Contains(AgentToolNames.CodeSymbols, fileResult.Output);

        var mcpResult = await tool.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000),
            """{"query":"mcp resource"}""");
        Assert.True(mcpResult.Success, mcpResult.Error);
        Assert.Contains(AgentToolNames.McpReadResource, mcpResult.Output);
    }

    [Fact]
    public async Task BackgroundTaskToolsWriteReadMessageAndStopOutput()
    {
        await using var db = TestDb.Create();
        var chatId = Guid.NewGuid();
        db.Set<Chat>().Add(new Chat { Id = chatId, Title = "background" });
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.Tool.Tests", Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var taskService = new AgentTaskService(db);
        var background = new BackgroundTaskService(db);
        var create = new TaskCreateAgentTool(taskService, background, sandbox);
        var output = new TaskOutputAgentTool(background);
        var send = new TaskSendMessageAgentTool(background);
        var stop = new TaskStopAgentTool(background);
        var context = new AgentToolExecutionContext(chatId, Guid.NewGuid(), Guid.NewGuid(), 10, 12000);

        var created = await create.ExecuteAsync(context,
            """{"title":"Survey repo","background":true,"prompt":"look around"}""");
        Assert.True(created.Success, created.Error);
        var task = db.Set<AgentTaskItem>().Single(t => t.ChatId == chatId);

        await Task.Delay(300);
        var read = await output.ExecuteAsync(context, $$"""{"id":"{{task.Id}}"}""");
        Assert.True(read.Success, read.Error);
        Assert.Contains("Background Task", read.Output);

        var message = await send.ExecuteAsync(context, $$"""{"id":"{{task.Id}}","message":"status?"}""");
        Assert.True(message.Success, message.Error);
        var reread = await output.ExecuteAsync(context, $$"""{"id":"{{task.Id}}"}""");
        Assert.Contains("status?", reread.Output);

        var stopped = await stop.ExecuteAsync(context, $$"""{"id":"{{task.Id}}"}""");
        Assert.True(stopped.Success, stopped.Error);
    }

    [Fact]
    public async Task WebSearchParsesDuckDuckGoHtmlResults()
    {
        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        var html = """
            <html><body>
            <div class="result">
              <a class="result__a" href="/l/?uddg=https%3A%2F%2Fgithub.com%2Fmodelcontextprotocol%2Fservers">MCP Servers</a>
              <a class="result__snippet">Reference servers for the Model Context Protocol.</a>
            </div>
            </body></html>
            """;
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });
        var tool = new WebSearchAgentTool(
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(new HttpClient(handler)));

        var result = await tool.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 2000),
            """{"query":"mcp servers","reason":"test"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("MCP Servers", result.Output);
        Assert.Contains("https://github.com/modelcontextprotocol/servers", result.Output);
        Assert.Contains("Reference servers", result.Output);
    }

    [Fact]
    public async Task WebSearchParsesGenericAnchorFallback()
    {
        await using var db = TestDb.Create();
        var platform = new ToolPlatformService(db);
        var html = """
            <html><body>
              <main>
                <a href="https://example.com/tools">Example Tool Catalog</a>
                <p>Useful tools for agent workflows.</p>
              </main>
            </body></html>
            """;
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });
        var tool = new WebSearchAgentTool(
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(new HttpClient(handler)));

        var result = await tool.ExecuteAsync(
            new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 2000),
            """{"query":"agent tools","reason":"test","max_results":3}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Example Tool Catalog", result.Output);
        Assert.Contains("https://example.com/tools", result.Output);
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
            new FileDeleteAgentTool(sandbox),
            new CodeSymbolsAgentTool(sandbox),
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

        Assert.True(registry.TryGet(AgentToolNames.CodeSymbols, out var symbols));
        Assert.True(symbols.Metadata.IsReadOnly);
        Assert.True(symbols.Metadata.IsConcurrencySafe);

        Assert.True(registry.TryGet(AgentToolNames.FileDelete, out var delete));
        Assert.False(delete.Metadata.IsReadOnly);
        Assert.True(delete.Metadata.IsDestructive);

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
    public async Task CodeSymbolsListsDefinitionsAcrossLanguages()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.CodeSymbols.Tests", Guid.NewGuid().ToString("N")));
        var context = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, 12000);
        var root = sandbox.GetSandboxRoot(context.ChatId);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Alpha.cs"), "public class AlphaService\n{\n    public void Run() {}\n}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "app.ts"), "export function renderApp() {}\nconst saveDraft = () => true;\n");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "worker.py"), "class Worker:\n    def run_job(self):\n        pass\n");
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Project\n## Usage\n");
        var symbols = new CodeSymbolsAgentTool(sandbox);

        var result = await symbols.ExecuteAsync(context, """{"path":".","max_results":20}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Alpha.cs:1: class AlphaService", result.Output);
        Assert.Contains("app.ts:1: function renderApp", result.Output);
        Assert.Contains("worker.py:1: class Worker", result.Output);
        Assert.Contains("README.md:1: heading1 Project", result.Output);
    }

    [Fact]
    public async Task WorkspaceCodeToolsProtectHashesEncodingPatchAndRollback()
    {
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.CodeReliability.Tests", Guid.NewGuid().ToString("N")));
        var context = new AgentToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 30, 20000);
        var root = sandbox.GetSandboxRoot(context.ChatId);
        var sourcePath = Path.Combine(root, "src", "encoding.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "line1\r\nline2\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var read = new CodeReadAgentTool(sandbox);
        var edit = new CodeEditAgentTool(sandbox);
        var patch = new CodeApplyPatchAgentTool(sandbox);
        var rollback = new CodeRollbackAgentTool(sandbox);
        var readResult = await read.ExecuteAsync(context, """{"path":"src/encoding.txt"}""");
        Assert.True(readResult.Success, readResult.Error);
        Assert.Contains("SHA256:", readResult.Output);
        Assert.Contains("Encoding: utf-8 with BOM", readResult.Output);
        Assert.Contains("Newline: CRLF", readResult.Output);

        var staleHash = Sha256(sourcePath);
        await File.AppendAllTextAsync(sourcePath, "outside\r\n", Encoding.UTF8);
        var conflict = await edit.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                path = "src/encoding.txt",
                old_text = "line2",
                new_text = "line3",
                expected_sha256 = staleHash,
                reason = "Verify conflict detection."
            }));
        Assert.False(conflict.Success);
        Assert.Contains("File changed", conflict.Error);

        var currentHash = Sha256(sourcePath);
        var okEdit = await edit.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                path = "src/encoding.txt",
                old_text = "line2",
                new_text = "line3",
                expected_sha256 = currentHash,
                reason = "Verify encoding preservation."
            }));
        Assert.True(okEdit.Success, okEdit.Error);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());
        Assert.Contains("line3\r\noutside\r\n", Encoding.UTF8.GetString(bytes[3..]));

        var patchPath = Path.Combine(root, "src", "patch.txt");
        await File.WriteAllTextAsync(patchPath, "alpha\nbeta\n", new UTF8Encoding(false));
        var patchText = string.Join('\n',
            "diff --git a/src/patch.txt b/src/patch.txt",
            "--- a/src/patch.txt",
            "+++ b/src/patch.txt",
            "@@ -1,2 +1,2 @@",
            " alpha",
            "-beta",
            "+gamma",
            string.Empty);
        var patchHash = Sha256(patchPath);
        var preview = await patch.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                patch = patchText,
                preview_only = true,
                expected_sha256_by_path = new Dictionary<string, string> { ["src/patch.txt"] = patchHash },
                reason = "Preview patch."
            }));
        Assert.True(preview.Success, preview.Error);
        Assert.Contains("Patch check passed", preview.Output);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(patchPath));

        var applied = await patch.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                patch = patchText,
                expected_sha256_by_path = new Dictionary<string, string> { ["src/patch.txt"] = patchHash },
                reason = "Apply patch."
            }));
        Assert.True(applied.Success, applied.Error);
        Assert.Contains("gamma", await File.ReadAllTextAsync(patchPath));
        var backupId = applied.Output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- src/patch.txt:", StringComparison.Ordinal))
            .Select(line => line.Split(':', 2)[1].Trim())
            .First();

        var rollbackPreview = await rollback.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                path = "src/patch.txt",
                backup_id = backupId,
                preview_only = true,
                expected_sha256 = Sha256(patchPath),
                reason = "Preview rollback."
            }));
        Assert.True(rollbackPreview.Success, rollbackPreview.Error);
        Assert.Contains("Rollback preview", rollbackPreview.Output);
        Assert.Contains("- gamma", rollbackPreview.Output);

        var rollbackResult = await rollback.ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                path = "src/patch.txt",
                backup_id = backupId,
                expected_sha256 = Sha256(patchPath),
                reason = "Restore patch."
            }));
        Assert.True(rollbackResult.Success, rollbackResult.Error);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(patchPath));
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
        Assert.Equal("tool", result.Messages[1].Role);
        Assert.Equal("call-1", result.Messages[1].ToolCallId);
        Assert.Equal("user", result.Messages[2].Role);
        Assert.Contains("[tool result]", result.Messages[2].Content);
        Assert.Equal("user", result.Messages[3].Role);
        Assert.Contains("tool_name_normalized", result.Issues.Select(i => i.Code));
        Assert.Contains("missing_tool_result_repaired", result.Issues.Select(i => i.Code));
        Assert.Contains("orphan_tool_result", result.Issues.Select(i => i.Code));
    }

    [Fact]
    public void ToolProtocolGuardRepairsAssistantToolCallsMissingSomeResults()
    {
        var messages = new List<MessagePayload>
        {
            new("assistant", "", ToolCalls:
            [
                new LlmToolCall("call-1", AgentToolNames.WebSearch, """{"query":"one"}"""),
                new LlmToolCall("call-2", AgentToolNames.WebSearch, """{"query":"two"}""")
            ]),
            new("tool", """{"success":true,"output":"one","error":null}""", "call-1"),
            new("assistant", "continuing")
        };

        var result = ToolProtocolGuard.RepairForProvider(messages, []);

        Assert.False(result.IsRejected);
        Assert.Equal("assistant", result.Messages[0].Role);
        Assert.Equal("tool", result.Messages[1].Role);
        Assert.Equal("call-1", result.Messages[1].ToolCallId);
        Assert.Equal("tool", result.Messages[2].Role);
        Assert.Equal("call-2", result.Messages[2].ToolCallId);
        Assert.Equal("assistant", result.Messages[3].Role);
        Assert.Contains("missing_tool_result_repaired", result.Issues.Select(i => i.Code));
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

        var delete = ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.FileDelete,
            """{"path":"notes.txt"}""");
        Assert.Equal(ToolSafetyLevels.High, delete.Level);
        Assert.True(delete.IsWriteOperation);
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
    public async Task McpStreamableHttpListsAndReadsResources()
    {
        await using var db = TestDb.Create();
        var chat = new TLAHStudio.Core.Models.Chat { Title = "MCP resources" };
        db.Set<TLAHStudio.Core.Models.Chat>().Add(chat);
        await db.SaveChangesAsync();
        var platform = new ToolPlatformService(db);
        await platform.SaveMcpServerAsync(new McpServerConfigDto(
            Guid.Empty,
            null,
            "docs",
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
            var result = method switch
            {
                "resources/list" => """{"resources":[{"uri":"file:///guide.md","name":"Guide","description":"Project guide","mimeType":"text/markdown"}]}""",
                "resources/read" => """{"contents":[{"uri":"file:///guide.md","mimeType":"text/markdown","text":"hello from resource"}]}""",
                _ => """{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"docs","version":"1"}}"""
            };
            var response = MapHttpMessageHandler.Json(
                HttpStatusCode.OK,
                $$"""{"jsonrpc":"2.0","id":{{id.GetInt32()}},"result":{{result}}}""");
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-2");
            return response;
        });
        using var http = new HttpClient(handler);
        var service = new McpClientService(
            db,
            platform,
            new AllowNetworkSecurityService(),
            new StaticHttpClientFactory(http));

        var resources = await service.ListResourcesAsync(chat.Id, "docs");
        var resource = Assert.Single(resources);
        Assert.Equal("file:///guide.md", resource.Uri);

        var text = await service.ReadResourceAsync(chat.Id, "docs", "file:///guide.md");
        Assert.Contains("hello from resource", text);
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

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
