using System.Net;
using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Core.Services.Artifacts;
using TLAHStudio.Core.Services.Tooling;

namespace TLAHStudio.Core.Tests;

public class ToolPlatformV2Tests
{
    [Fact]
    public void LlmToolDefinition_RemainsBackwardCompatibleAndCarriesV2Contract()
    {
        var legacy = new LlmToolDefinition(
            "read",
            "Read source.",
            ObjectSchema(("path", StringSchema())));

        Assert.Equal("core", legacy.Namespace);
        Assert.Equal("general", legacy.Category);
        Assert.False(legacy.Strict);

        var v2 = legacy with
        {
            Namespace = "artifact",
            Category = "document",
            Strict = true,
            Deferred = true,
            InputExamples = [new Dictionary<string, object> { ["path"] = "README.md" }],
            OutputSchema = ObjectSchema(("text", StringSchema())),
            Annotations = new LlmToolAnnotations(ReadOnly: true, ConcurrencySafe: true)
        };

        Assert.True(v2.Strict);
        Assert.True(v2.Deferred);
        Assert.True(v2.Annotations!.ReadOnly);
        Assert.Single(v2.InputExamples!);
    }

    [Fact]
    public void AgentToolResult_SerializesStructuredFailureMetadata()
    {
        var result = new AgentToolResult(
            false,
            string.Empty,
            "request timed out",
            StructuredContent: new { attempt = 2 },
            ErrorCode: "timeout",
            Retryable: true,
            Sources:
            [
                new AgentToolSource(
                    "https://example.com",
                    "Example",
                    "web",
                    DateTime.UnixEpoch,
                    "src-1")
            ],
            DurationMs: 1250,
            Diagnostics: new Dictionary<string, object> { ["status"] = 504 });

        using var json = JsonDocument.Parse(result.ToJson());
        Assert.Equal("timeout", json.RootElement.GetProperty("errorCode").GetString());
        Assert.True(json.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(1250, json.RootElement.GetProperty("durationMs").GetInt64());
        Assert.Equal(2, json.RootElement.GetProperty("structuredContent").GetProperty("attempt").GetInt32());
        Assert.Equal("src-1", json.RootElement.GetProperty("sources")[0].GetProperty("CitationId").GetString());
    }

    [Fact]
    public void StrictSchemaNormalizer_MakesOptionalPropertiesNullableAndRequired()
    {
        var schema = ObjectSchema(
            ("query", StringSchema()),
            ("limit", new Dictionary<string, object> { ["type"] = "integer" }));
        schema["required"] = new[] { "query" };

        var normalized = LlmToolSchema.NormalizeForStrictProvider(schema);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
        var root = json.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["query", "limit"],
            root.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["integer", "null"],
            root.GetProperty("properties").GetProperty("limit").GetProperty("type")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void RuntimeValidator_AcceptsNullForLegacyOptionalField()
    {
        var tool = new SchemaTool(new LlmToolDefinition(
            "search",
            "Search.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = StringSchema(),
                    ["limit"] = new Dictionary<string, object> { ["type"] = "integer" }
                },
                ["required"] = new[] { "query" },
                ["additionalProperties"] = false
            }));

        var validation = ((IAgentTool)tool).ValidateInput("""{"query":"test","limit":null}""");

        Assert.True(validation.Success, validation.Error);
    }

    [Fact]
    public void Selector_ExposesAtMostFifteenAndExplicitSearchResultsWin()
    {
        var selector = CreateSelector();
        var result = selector.Select(new ToolSelectionContext(
            "Please inspect and fix the repository code, then run tests.",
            ExplicitlyLoadedNames: ["document_create"]));

        Assert.InRange(result.Definitions.Count, 1, 15);
        Assert.Contains(result.Definitions, item => item.Name == "document_create");
        Assert.Contains(result.Definitions, item => item.Name == AgentToolNames.CodeRead);
        Assert.Contains(result.Definitions, item => item.Name == AgentToolNames.CodeEdit);
        Assert.Contains(result.DeferredDefinitions, item => item.Deferred);
        Assert.Contains(selector.Search("Word document"), item => item.Name == "document_create");
        Assert.Contains(
            selector.Select(new ToolSelectionContext("多来源交叉验证这条新闻")).Definitions,
            item => item.Name == "research_verify");
        Assert.Equal(
            result.Definitions.Select(item => item.Name),
            selector.Select(new ToolSelectionContext(
                "Please inspect and fix the repository code, then run tests.",
                ExplicitlyLoadedNames: ["document_create"]))
                .Definitions.Select(item => item.Name));
    }

    [Fact]
    public void ToolSearchPromotion_LoadsOnlyRealRegisteredTools()
    {
        var registry = new AgentToolRegistry(
        [
            new FakeTool(AgentToolNames.ToolSearch),
            new FakeTool("document_create")
        ]);
        var result = new AgentToolResult(
            true,
            "legacy output",
            StructuredContent: new[]
            {
                new { name = "document_create" },
                new { name = "invented_tool" }
            });

        var names = ToolCatalogPromotion.ExtractRegisteredNames(result, registry);

        Assert.Equal(["document_create"], names);
    }

    [Fact]
    public void AgentRunState_DeepClonePreservesDeferredToolSelectionIndependently()
    {
        var state = new AgentRunState();
        state.LoadedDeferredToolNames.Add("document_create");

        var clone = state.DeepClone();
        clone.LoadedDeferredToolNames.Add("diagram_create");

        Assert.Contains("document_create", clone.LoadedDeferredToolNames);
        Assert.DoesNotContain("diagram_create", state.LoadedDeferredToolNames);
    }

    [Fact]
    public void Selector_ProgrammaticEvaluation_CoversAtLeastTwoHundredIntentCases()
    {
        var selector = CreateSelector();
        Assert.Equal(51, selector.Catalog.Count);
        var intents = new (string Expected, string Category, string[] Prompts)[]
        {
            (AgentToolNames.CodeRead, "code",
            [
                "inspect repository implementation", "review C# logic", "查阅代码实现", "检查代码逻辑", "understand this C# project",
                "read code implementation details", "分析仓库结构", "look at program logic", "debug code behavior", "代码审查"
            ]),
            (AgentToolNames.WebSearch, "research",
            [
                "search the web", "research current citations", "联网检索", "网络搜索", "find latest news",
                "verify with citations", "研究公开资料", "check the current website", "internet research", "多来源验证"
            ]),
            (AgentToolNames.Git, "git",
            [
                "check git status", "create a commit", "查看分支", "准备发布标签", "inspect git history",
                "push branch", "review commit log", "创建提交", "merge changes", "inspect git changes"
            ]),
            (AgentToolNames.TerminalExec, "terminal",
            [
                "run a PowerShell command", "install a package", "执行终端命令", "运行命令行程序", "use shell",
                "execute a terminal command", "use the package manager", "命令行操作", "invoke PowerShell", "terminal operation"
            ]),
            (AgentToolNames.McpListTools, "mcp",
            [
                "discover mcp tools", "use connector integration", "查找MCP能力", "调用外部插件", "list mcp server tools",
                "external resource connector", "集成服务器", "mcp resource", "plugin capability", "连接器"
            ]),
            (AgentToolNames.TaskCreate, "task",
            [
                "create a background task", "plan a long-running job", "创建后台任务", "多步骤计划", "track todo tasks",
                "long running worker", "任务管理", "update task plan", "background process", "待办事项"
            ]),
            (AgentToolNames.MemoryRead, "memory",
            [
                "read stored memory", "remember preferences", "读取持久记忆", "查看上下文记忆", "load saved context",
                "memory preference", "记住这个偏好", "saved context", "recall earlier facts", "读取历史记忆"
            ]),
            (AgentToolNames.FileRead, "file",
            [
                "read a file", "inspect folder contents", "读取文件", "查看目录", "open attachment",
                "find file path", "download saved file", "文件操作", "list directory", "读取文本"
            ]),
            (AgentToolNames.DocumentCreate, "document",
            [
                "create a Word document", "write a DOCX report", "生成PDF文档", "制作报告", "document processing",
                "export markdown report", "处理合同文档", "generate a PDF", "生成简历文档", "prepare a Word manuscript"
            ]),
            (AgentToolNames.SpreadsheetCreate, "spreadsheet",
            [
                "create an Excel spreadsheet", "prepare an XLSX workbook", "制作表格", "生成电子表格", "write CSV",
                "add formulas to workbook", "制表并计算", "organize an Excel dataset", "spreadsheet processing", "工作簿"
            ]),
            (AgentToolNames.DiagramCreate, "diagram",
            [
                "draw an architecture diagram", "render a chart", "绘制流程图", "生成架构图", "create SVG",
                "plot statistics", "可视化图表", "export PNG diagram", "draw graph", "统计图"
            ])
        };

        var alwaysAvailable = new HashSet<string>(
            [
                AgentToolNames.ToolSearch,
                AgentToolNames.AskUserQuestion,
                AgentToolNames.Skill,
                AgentToolNames.FileSend
            ],
            StringComparer.OrdinalIgnoreCase);
        var evaluatedCategories = intents
            .Select(intent => intent.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var promptPrefixes = new[]
        {
            "Directly complete this request: ",
            "Please handle this workspace request now: "
        };
        var uniquePrompts = new HashSet<string>(StringComparer.Ordinal);
        var uniqueSelectionSets = new HashSet<string>(StringComparer.Ordinal);
        var evaluated = 0;
        foreach (var intent in intents)
        {
            foreach (var basePrompt in intent.Prompts)
            {
                foreach (var prefix in promptPrefixes)
                {
                    var prompt = prefix + basePrompt;
                    Assert.True(uniquePrompts.Add(prompt), $"Duplicate evaluation prompt: {prompt}");
                    var first = selector.Select(new ToolSelectionContext(prompt));
                    var second = selector.Select(new ToolSelectionContext(prompt));
                    Assert.InRange(first.Definitions.Count, 1, 15);
                    Assert.True(
                        first.Definitions.Any(tool => tool.Name == intent.Expected),
                        $"Expected '{intent.Expected}' for '{prompt}', selected: " +
                        string.Join(", ", first.Definitions.Select(tool => tool.Name)));
                    Assert.Equal(
                        first.Definitions.Select(tool => tool.Name),
                        second.Definitions.Select(tool => tool.Name));

                    var routedCategories = first.Definitions
                        .Where(tool => !alwaysAvailable.Contains(tool.Name))
                        .Select(tool => tool.Category)
                        .Where(evaluatedCategories.Contains)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    Assert.True(
                        routedCategories.SequenceEqual(
                            [intent.Category],
                            StringComparer.OrdinalIgnoreCase),
                        $"Expected only category '{intent.Category}' for '{prompt}', selected categories: " +
                        string.Join(", ", routedCategories));
                    uniqueSelectionSets.Add(string.Join(",", first.Definitions.Select(tool => tool.Name)));
                    evaluated++;
                }
            }
        }

        Assert.Equal(220, evaluated);
        Assert.Equal(220, uniquePrompts.Count);
        Assert.True(
            uniqueSelectionSets.Count >= intents.Length,
            $"Expected category-specific selections, observed only {uniqueSelectionSets.Count} unique sets.");
    }

    [Theory]
    [InlineData("Required tool argument '$.path' is missing.", "invalid_arguments", false)]
    [InlineData("Request timed out after 30 seconds.", "timeout", true)]
    [InlineData("HTTP 429 rate limit exceeded.", "rate_limited", true)]
    [InlineData("Connection interrupted.", "network_transient", true)]
    [InlineData("Blocked by safety policy.", "permission_denied", false)]
    [InlineData("File not found.", "not_found", false)]
    public void FailureClassifier_ProducesStableRecoveryMetadata(
        string error,
        string expectedCode,
        bool expectedRetryable)
    {
        var classified = ToolFailureClassifier.Enrich(
            new AgentToolResult(false, string.Empty, error),
            42);

        Assert.Equal(expectedCode, classified.ErrorCode);
        Assert.Equal(expectedRetryable, classified.Retryable);
        Assert.Equal(42, classified.DurationMs);
        Assert.False(string.IsNullOrWhiteSpace(
            ToolFailureClassifier.RecoveryGuidance(classified.ErrorCode, classified.Retryable)));
    }

    [Fact]
    public void SpecializedTools_HaveStableUxAndNonDestructiveMetadata()
    {
        var names = new[]
        {
            AgentToolNames.ResearchVerify,
            AgentToolNames.SpreadsheetCreate,
            AgentToolNames.SpreadsheetInspect,
            AgentToolNames.SpreadsheetUpdate,
            AgentToolNames.DocumentCreate,
            AgentToolNames.DocumentInspect,
            AgentToolNames.DiagramCreate
        };

        foreach (var name in names)
        {
            var metadata = AgentToolMetadata.For(name, requiresApproval: true);
            Assert.False(metadata.IsDestructive);
            Assert.NotEqual(name, AgentToolUx.UserFacingName(name));
            Assert.False(string.IsNullOrWhiteSpace(AgentToolUx.ActivityDescription(name)));
        }
        Assert.True(AgentToolMetadata.For(AgentToolNames.DocumentInspect, true).IsReadOnly);
        Assert.False(AgentToolMetadata.For(AgentToolNames.DocumentCreate, true).IsReadOnly);
    }

    [Fact]
    public void SpecializedTools_AreSafetyClassifiedWithoutWeakeningPathBoundaries()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.ToolPlatformV2",
            Guid.NewGuid().ToString("N"));
        try
        {
            var sandbox = new SandboxCommandService(root);
            var chatId = Guid.NewGuid();

            var research = ToolSafetyKernel.Assess(
                sandbox,
                chatId,
                AgentToolNames.ResearchVerify,
                """{"query":"verify this","create_report":false}""");
            var document = ToolSafetyKernel.Assess(
                sandbox,
                chatId,
                AgentToolNames.DocumentCreate,
                """{"path":"artifacts/report.docx"}""");
            var inspect = ToolSafetyKernel.Assess(
                sandbox,
                chatId,
                AgentToolNames.SpreadsheetInspect,
                """{"path":"artifacts/data.xlsx"}""");
            var outside = ToolSafetyKernel.Assess(
                sandbox,
                chatId,
                AgentToolNames.DocumentCreate,
                """{"path":"C:\\Users\\Public\\report.docx"}""");

            Assert.Equal(ToolSafetyLevels.Medium, research.Level);
            Assert.True(research.IsReadOnly);
            Assert.Equal(ToolSafetyLevels.Medium, document.Level);
            Assert.True(document.IsWriteOperation);
            Assert.False(document.IsBlocked);
            Assert.False(inspect.IsBlocked);
            Assert.True(outside.IsBlocked);
            Assert.True(outside.CanOverrideBlock);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfficialOpenAI_UsesStrictSchemaAndOnlySafeParallelCalls()
    {
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}"""));
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(
            client,
            "key",
            "https://api.openai.com",
            "gpt-test",
            "openai");
        var tools = new[]
        {
            ReadOnlyTool("read"),
            ReadOnlyTool("grep")
        };

        await provider.ChatAsync([new MessagePayload("user", "inspect")], "system", tools: tools);

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.True(json.RootElement.GetProperty("tools")[0]
            .GetProperty("function").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task CompatibleOpenAIEndpoint_OmitsStrictAndParallelExtensions()
    {
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}"""));
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(
            client,
            "key",
            "https://compatible.example.com",
            "model",
            "openai_compat");

        await provider.ChatAsync(
            [new MessagePayload("user", "inspect")],
            "system",
            tools: [ReadOnlyTool("read")]);

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("parallel_tool_calls", out _));
        Assert.False(json.RootElement.GetProperty("tools")[0]
            .GetProperty("function").TryGetProperty("strict", out _));
    }

    [Fact]
    public async Task OfficialAnthropic_UsesStrictExamplesAndSafeParallelChoice()
    {
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"content":[{"type":"text","text":"ok"}]}"""));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(
            client,
            "key",
            "https://api.anthropic.com",
            "claude-test");
        var tool = ReadOnlyTool("read") with
        {
            InputExamples = [new Dictionary<string, object> { ["path"] = "README.md" }]
        };

        await provider.ChatAsync(
            [new MessagePayload("user", "inspect")],
            "system",
            tools: [tool, ReadOnlyTool("grep")]);

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.Equal(
            "README.md",
            json.RootElement.GetProperty("tools")[0]
                .GetProperty("input_examples")[0].GetProperty("path").GetString());
        Assert.False(json.RootElement.GetProperty("tool_choice")
            .GetProperty("disable_parallel_tool_use").GetBoolean());
    }

    [Fact]
    public async Task OfficialProviders_SerializeAllRealSpecialistSchemasStrictly()
    {
        var definitions = RealSpecialistDefinitions();
        Assert.Equal(7, definitions.Count);
        Assert.All(definitions, definition => Assert.True(definition.Strict));

        var openAiHandler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}"""));
        using var openAiClient = new HttpClient(openAiHandler);
        var openAi = new OpenAICompatibleProvider(
            openAiClient,
            "key",
            "https://api.openai.com",
            "gpt-test",
            "openai");

        await openAi.ChatAsync(
            [new MessagePayload("user", "use specialist tools")],
            "system",
            tools: definitions);

        var openAiBody = await Assert.Single(openAiHandler.Requests).Content!.ReadAsStringAsync();
        using (var json = JsonDocument.Parse(openAiBody))
        {
            var wireTools = json.RootElement.GetProperty("tools");
            Assert.Equal(definitions.Count, wireTools.GetArrayLength());
            Assert.False(json.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
            foreach (var wireTool in wireTools.EnumerateArray())
            {
                var function = wireTool.GetProperty("function");
                Assert.True(function.GetProperty("strict").GetBoolean());
                AssertStrictWireSchema(function.GetProperty("parameters"));
            }
        }

        var anthropicHandler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"content":[{"type":"text","text":"ok"}]}"""));
        using var anthropicClient = new HttpClient(anthropicHandler);
        var anthropic = new AnthropicProvider(
            anthropicClient,
            "key",
            "https://api.anthropic.com",
            "claude-test");

        await anthropic.ChatAsync(
            [new MessagePayload("user", "use specialist tools")],
            "system",
            tools: definitions);

        var anthropicBody = await Assert.Single(anthropicHandler.Requests).Content!.ReadAsStringAsync();
        using var anthropicJson = JsonDocument.Parse(anthropicBody);
        var anthropicTools = anthropicJson.RootElement.GetProperty("tools");
        Assert.Equal(definitions.Count, anthropicTools.GetArrayLength());
        Assert.True(anthropicJson.RootElement.GetProperty("tool_choice")
            .GetProperty("disable_parallel_tool_use").GetBoolean());
        foreach (var wireTool in anthropicTools.EnumerateArray())
        {
            Assert.True(wireTool.GetProperty("strict").GetBoolean());
            Assert.True(wireTool.GetProperty("defer_loading").GetBoolean());
            AssertStrictWireSchema(wireTool.GetProperty("input_schema"));
        }
    }

    private static ToolContextSelector CreateSelector()
    {
        var names = new[]
        {
            AgentToolNames.EnterPlanMode, AgentToolNames.ExitPlanMode,
            AgentToolNames.AskUserQuestion, AgentToolNames.Skill, AgentToolNames.ToolSearch,
            AgentToolNames.TodoWrite, AgentToolNames.TaskCreate, AgentToolNames.TaskUpdate,
            AgentToolNames.TaskList, AgentToolNames.TaskOutput, AgentToolNames.TaskStop,
            AgentToolNames.TaskSendMessage, AgentToolNames.ReadPersistedOutput,
            AgentToolNames.SandboxExec, AgentToolNames.TerminalExec,
            AgentToolNames.FileList, AgentToolNames.FileRead, AgentToolNames.FileWrite,
            AgentToolNames.FileSend, AgentToolNames.FileSearch, AgentToolNames.FileInfo,
            AgentToolNames.FileMkdir, AgentToolNames.FileMove, AgentToolNames.FileDelete,
            AgentToolNames.Git, AgentToolNames.HttpRequest, AgentToolNames.WebSearch,
            AgentToolNames.BrowserRead, AgentToolNames.ResearchVerify,
            AgentToolNames.SpreadsheetCreate, AgentToolNames.SpreadsheetInspect,
            AgentToolNames.SpreadsheetUpdate, AgentToolNames.DocumentCreate,
            AgentToolNames.DocumentInspect, AgentToolNames.DiagramCreate,
            AgentToolNames.McpListTools, AgentToolNames.McpListResources,
            AgentToolNames.McpReadResource, AgentToolNames.McpCall,
            AgentToolNames.MemoryRead, AgentToolNames.MemoryWrite,
            AgentToolNames.CodeRead, AgentToolNames.CodeGrep, AgentToolNames.CodeGlob,
            AgentToolNames.CodeEdit, AgentToolNames.CodeMultiEdit, AgentToolNames.CodeDiff,
            AgentToolNames.CodeApplyPatch, AgentToolNames.CodeRollback,
            AgentToolNames.CodeDiagnostics, AgentToolNames.CodeSymbols
        };
        return new ToolContextSelector(new AgentToolRegistry(names.Select(name => new FakeTool(name))));
    }

    private static IReadOnlyList<LlmToolDefinition> RealSpecialistDefinitions()
    {
        IArtifactWorkbenchService artifactWorkbench = null!;
        IAgentTool[] tools =
        [
            new ResearchVerifyAgentTool(null!, null!),
            new SpreadsheetCreateAgentTool(artifactWorkbench),
            new SpreadsheetInspectAgentTool(artifactWorkbench),
            new SpreadsheetUpdateAgentTool(artifactWorkbench),
            new DocumentCreateAgentTool(artifactWorkbench),
            new DocumentInspectAgentTool(artifactWorkbench),
            new DiagramCreateAgentTool(artifactWorkbench)
        ];
        return new ToolContextSelector(new AgentToolRegistry(tools)).Catalog;
    }

    private static void AssertStrictWireSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (SchemaIncludesType(schema, "object"))
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            if (schema.TryGetProperty("properties", out var properties))
            {
                var propertyNames = properties.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var requiredNames = schema.GetProperty("required")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(propertyNames, requiredNames);
                foreach (var property in properties.EnumerateObject())
                    AssertStrictWireSchema(property.Value);
            }
        }

        if (SchemaIncludesType(schema, "array") &&
            schema.TryGetProperty("items", out var items))
            AssertStrictWireSchema(items);
        foreach (var branchName in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!schema.TryGetProperty(branchName, out var branches))
                continue;
            foreach (var branch in branches.EnumerateArray())
                AssertStrictWireSchema(branch);
        }
    }

    private static bool SchemaIncludesType(JsonElement schema, string expected)
    {
        if (!schema.TryGetProperty("type", out var type))
            return false;
        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == expected,
            JsonValueKind.Array => type.EnumerateArray().Any(item => item.GetString() == expected),
            _ => false
        };
    }

    private static LlmToolDefinition ReadOnlyTool(string name) => new(
        name,
        $"Read data with {name}.",
        ObjectSchema(("path", StringSchema())),
        Strict: true,
        Annotations: new LlmToolAnnotations(ReadOnly: true, Idempotent: true, ConcurrencySafe: true));

    private static Dictionary<string, object> ObjectSchema(
        params (string Name, Dictionary<string, object> Schema)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties.ToDictionary(item => item.Name, item => (object)item.Schema),
        ["required"] = Array.Empty<string>(),
        ["additionalProperties"] = false
    };

    private static Dictionary<string, object> StringSchema() => new()
    {
        ["type"] = "string"
    };

    private sealed class FakeTool(string name) : IAgentTool
    {
        public LlmToolDefinition Definition { get; } = new(
            name,
            "Registered capability used for selector evaluation.",
            ObjectSchema());

        public bool RequiresApproval => false;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(true, "ok"));
    }

    private sealed class SchemaTool(LlmToolDefinition definition) : IAgentTool
    {
        public LlmToolDefinition Definition { get; } = definition;
        public bool RequiresApproval => false;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(true, "ok"));
    }
}
