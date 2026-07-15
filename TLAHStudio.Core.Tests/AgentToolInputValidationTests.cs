using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public sealed class AgentToolInputValidationTests
{
    [Fact]
    public void ValidateInput_EnforcesDeclaredPrimitiveAndContainerTypes()
    {
        var tool = CreateTool(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = new[] { "text", "count", "ratio", "enabled", "settings", "items" },
            ["properties"] = new Dictionary<string, object>
            {
                ["text"] = TypeSchema("string"),
                ["count"] = TypeSchema("integer"),
                ["ratio"] = TypeSchema("number"),
                ["enabled"] = TypeSchema("boolean"),
                ["settings"] = TypeSchema("object"),
                ["items"] = TypeSchema("array")
            }
        });

        var valid = tool.ValidateInput(
            """{"text":"ok","count":2.0,"ratio":0.25,"enabled":true,"settings":{},"items":[]}""");

        Assert.True(valid.Success, valid.Error);

        var invalidCases = new[]
        {
            ("""{"text":9,"count":2,"ratio":0.25,"enabled":true,"settings":{},"items":[]}""", "$.text"),
            ("""{"text":"ok","count":2.5,"ratio":0.25,"enabled":true,"settings":{},"items":[]}""", "$.count"),
            ("""{"text":"ok","count":2,"ratio":"fast","enabled":true,"settings":{},"items":[]}""", "$.ratio"),
            ("""{"text":"ok","count":2,"ratio":0.25,"enabled":"true","settings":{},"items":[]}""", "$.enabled"),
            ("""{"text":"ok","count":2,"ratio":0.25,"enabled":true,"settings":[],"items":[]}""", "$.settings"),
            ("""{"text":"ok","count":2,"ratio":0.25,"enabled":true,"settings":{},"items":{}}""", "$.items")
        };

        foreach (var (json, expectedPath) in invalidCases)
        {
            var result = tool.ValidateInput(json);
            Assert.False(result.Success);
            Assert.Contains(expectedPath, result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateInput_EnforcesEnumValues()
    {
        var tool = CreateTool(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["mode"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "ask", "full" }
                },
                ["level"] = new Dictionary<string, object>
                {
                    ["type"] = "number",
                    ["enum"] = new object[] { 1, 2 }
                }
            }
        });

        Assert.True(tool.ValidateInput("""{"mode":"full","level":1.0}""").Success);

        var invalid = tool.ValidateInput("""{"mode":"auto","level":1}""");
        Assert.False(invalid.Success);
        Assert.Contains("$.mode", invalid.Error, StringComparison.Ordinal);
        Assert.Contains("\"ask\"", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInput_ValidatesNestedRequiredPropertiesAndArrayItems()
    {
        var tool = CreateTool(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["jobs"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "id", "options" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["id"] = TypeSchema("integer"),
                            ["options"] = new Dictionary<string, object>
                            {
                                ["type"] = "object",
                                ["required"] = new[] { "enabled" },
                                ["properties"] = new Dictionary<string, object>
                                {
                                    ["enabled"] = TypeSchema("boolean")
                                }
                            }
                        }
                    }
                }
            }
        });

        Assert.True(tool.ValidateInput("""{"jobs":[{"id":1,"options":{"enabled":true}}]}""").Success);

        var missing = tool.ValidateInput("""{"jobs":[{"id":1,"options":{}}]}""");
        Assert.False(missing.Success);
        Assert.Contains("$.jobs[0].options.enabled", missing.Error, StringComparison.Ordinal);

        var wrongItemType = tool.ValidateInput("""{"jobs":[{"id":"one","options":{"enabled":true}}]}""");
        Assert.False(wrongItemType.Success);
        Assert.Contains("$.jobs[0].id", wrongItemType.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInput_AllowsUnknownFieldsUnlessSchemaExplicitlyForbidsThem()
    {
        var compatibleTool = CreateTool(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["name"] = TypeSchema("string")
            }
        });
        var strictTool = CreateTool(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object>
            {
                ["name"] = TypeSchema("string")
            }
        });

        Assert.True(compatibleTool.ValidateInput("""{"name":"TLAH","futureOption":true}""").Success);

        var rejected = strictTool.ValidateInput("""{"name":"TLAH","futureOption":true}""");
        Assert.False(rejected.Success);
        Assert.Contains("$.futureOption", rejected.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInput_SupportsSchemasDeserializedAsJsonElements()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """
            {
              "type":"object",
              "required":["mode"],
              "properties":{"mode":{"type":"string","enum":["ask","full"]}},
              "additionalProperties":false
            }
            """)!;
        var tool = CreateTool(schema);

        Assert.True(tool.ValidateInput("""{"mode":"ask"}""").Success);
        Assert.False(tool.ValidateInput("""{"mode":false}""").Success);
        Assert.False(tool.ValidateInput("""{"mode":"ask","extra":1}""").Success);
    }

    [Fact]
    public void BuiltInDefinition_DeclaresUniversalReasonMetadataAndRemainsStrict()
    {
        IAgentTool tool = new SchemaTool(AgentToolSupport.Definition(
            "strict_builtin",
            "Strict built-in test tool.",
            new Dictionary<string, object>
            {
                ["name"] = TypeSchema("string")
            },
            ["name"]));

        var withReason = tool.ValidateInput("""{"name":"TLAH","reason":"Explain the operation."}""");
        var withUnknown = tool.ValidateInput("""{"name":"TLAH","futureOption":true}""");

        Assert.True(withReason.Success, withReason.Error);
        Assert.False(withUnknown.Success);
        Assert.Contains("$.futureOption", withUnknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AskUserQuestion_StillAcceptsQuestionAndCollectedAnswerPhases()
    {
        var tool = new AskUserQuestionAgentTool();

        var questions = tool.ValidateInput(
            """
            {"questions":[{"question":"Choose?","header":"Choice","options":[{"label":"A","description":"First"},{"label":"B","description":"Second"}],"multiSelect":false}]}
            """);
        var answers = tool.ValidateInput("""{"answers":{"Choice":"A"}}""");
        var invalidNestedValue = tool.ValidateInput(
            """
            {"questions":[{"question":"Choose?","header":"Choice","options":[{"label":"A","description":"First"},{"label":"B","description":"Second"}],"multiSelect":"no"}]}
            """);

        Assert.True(questions.Success, questions.Error);
        Assert.True(answers.Success, answers.Error);
        Assert.False(invalidNestedValue.Success);
        Assert.Contains("$.questions[0].multiSelect", invalidNestedValue.Error, StringComparison.Ordinal);
    }

    private static Dictionary<string, object> TypeSchema(string type) => new()
    {
        ["type"] = type
    };

    private static IAgentTool CreateTool(Dictionary<string, object> schema) =>
        new SchemaTool(schema);

    private sealed class SchemaTool : IAgentTool
    {
        public SchemaTool(Dictionary<string, object> schema)
        {
            Definition = new LlmToolDefinition("schema_test", "Schema validation test tool.", schema);
        }

        public SchemaTool(LlmToolDefinition definition)
        {
            Definition = definition;
        }

        public LlmToolDefinition Definition { get; }
        public bool RequiresApproval => false;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(true, argumentsJson));
    }
}
