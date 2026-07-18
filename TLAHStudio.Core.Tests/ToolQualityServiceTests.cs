using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Observability;

namespace TLAHStudio.Core.Tests;

public sealed class ToolQualityServiceTests
{
    [Fact]
    public async Task LoadAsync_ComputesContentFreeExecutionMetrics()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Metrics" };
        var turn = new Turn
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            TurnNumber = 1
        };
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            ChatId = chat.Id,
            TurnId = turn.Id,
            Status = AgentRunStatuses.Completed,
            UserRequest = "secret prompt that must not be selected"
        };
        var step = new AgentStep
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            StepNumber = 1,
            Status = AgentStepStatuses.Completed
        };
        var started = DateTime.UtcNow.AddMilliseconds(-250);
        db.AddRange(
            chat,
            turn,
            run,
            step,
            Invocation(run, step, AgentToolNames.TerminalExec, ToolInvocationStatuses.Completed, started),
            Invocation(run, step, AgentToolNames.TerminalExec, ToolInvocationStatuses.Failed, started),
            Invocation(run, step, AgentToolNames.ToolSearch, ToolInvocationStatuses.Completed, started),
            Invocation(run, step, AgentToolNames.WebSearch, ToolInvocationStatuses.Denied, started));
        await db.SaveChangesAsync();

        var snapshot = await new ToolQualityService(db).LoadAsync(30);

        Assert.Equal(4, snapshot.TotalCalls);
        Assert.Equal(2, snapshot.Completed);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(1, snapshot.Denied);
        Assert.Equal(66.7, snapshot.SuccessRate);
        Assert.Equal(50, snapshot.ShellFallbackRate);
        Assert.Equal(25, snapshot.ToolSearchRate);
        var terminal = Assert.Single(snapshot.Tools, row => row.ToolName == AgentToolNames.TerminalExec);
        Assert.Equal(2, terminal.Calls);
        Assert.Equal(50, terminal.SuccessRate);
        Assert.True(terminal.AverageDurationMs >= 200);
    }

    private static ToolInvocation Invocation(
        AgentRun run,
        AgentStep step,
        string tool,
        string status,
        DateTime started) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentRunId = run.Id,
            AgentStepId = step.Id,
            ToolName = tool,
            Status = status,
            ArgumentsJson = """{"private":"not queried"}""",
            ResultJson = """{"private":"not queried"}""",
            CreatedAt = DateTime.UtcNow,
            StartedAt = started,
            CompletedAt = DateTime.UtcNow
        };
}
