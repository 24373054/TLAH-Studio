using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class AgentToolUxTests
{
    [Fact]
    public void ToolUxNormalizesEquivalentJsonInputs()
    {
        Assert.True(AgentToolUx.InputsEquivalent(
            """{"path":"a.txt","reason":"read"}""",
            """
            {
              "path": "a.txt",
              "reason": "read"
            }
            """));

        Assert.False(AgentToolUx.InputsEquivalent(
            """{"path":"a.txt"}""",
            """{"path":"b.txt"}"""));
    }

    [Fact]
    public void ToolUxRendersUseAndTruncatedResult()
    {
        using var db = TestDb.Create();
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.ToolUx.Tests", Guid.NewGuid().ToString("N")));
        var platform = new ToolPlatformService(db);
        IAgentTool tool = new FileReadAgentTool(sandbox, platform);

        var use = tool.RenderToolUse(
            """{"path":"notes/readme.txt","reason":"Inspect the note"}""",
            ToolSafetyAssessment.LowRead("file", "Read-only file access."));

        Assert.Equal("Read file", use.Title);
        Assert.Equal(AgentToolRenderHints.File, use.RenderHint);
        Assert.Equal("notes/readme.txt", use.PrimaryPath);
        Assert.Contains("Inspect the note", use.Subtitle);

        var result = tool.RenderToolResult(new AgentToolResult(
            true,
            "alpha\n[output truncated: persisted to artifacts]\n"));

        Assert.True(result.IsTruncated);
        Assert.Equal("Read file completed", result.Title);
    }
}
