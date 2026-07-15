using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Data;

namespace TLAHStudio.Core.Tests;

/// <summary>
/// Tests for the AgentRunEngineV2 and related types introduced in M2.7.0.
/// </summary>
public class AgentRunEngineV2Tests : IDisposable
{
    private readonly TlahDbContext _db = TestDb.Create();

    public void Dispose() => _db.Dispose();

    // ── AgentRunState tests ──────────────────────────────────

    [Fact]
    public void AgentRunState_DeepClone_CopiesMessages()
    {
        var state = new AgentRunState
        {
            RunId = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            TurnId = Guid.NewGuid(),
            Messages = [new MessagePayload("user", "hello"), new MessagePayload("assistant", "hi")],
            SequenceNum = 5,
            Status = AgentRunStatuses.Running
        };

        var clone = state.DeepClone();

        Assert.Equal(state.RunId, clone.RunId);
        Assert.Equal(state.ChatId, clone.ChatId);
        Assert.Equal(2, clone.Messages.Count);
        Assert.Equal(5, clone.SequenceNum);
        // Mutating clone should not affect original
        clone.Messages.Add(new MessagePayload("tool", "result"));
        Assert.Equal(2, state.Messages.Count);
        Assert.Equal(3, clone.Messages.Count);
    }

    [Fact]
    public void AgentRunState_DefaultValues()
    {
        var state = new AgentRunState();

        Assert.Equal(AgentRunStatuses.Running, state.Status);
        Assert.Equal(0, state.CurrentStep);
        Assert.Equal(48, state.MaxSteps);
        Assert.Empty(state.Messages);
        Assert.Equal(0, state.SequenceNum);
    }

    // ── AgentRunFrame tests ──────────────────────────────────

    [Fact]
    public void AgentRunFrame_HoldsEvents()
    {
        var events = new List<AgentEvent>
        {
            new() { EventType = "test", SequenceNumber = 1 },
            new() { EventType = "test", SequenceNumber = 2 }
        };

        var frame = new AgentRunFrame(1, AgentRunFrameKinds.ModelRequest, events);

        Assert.Equal(1, frame.StepNumber);
        Assert.Equal(AgentRunFrameKinds.ModelRequest, frame.Kind);
        Assert.Equal(2, frame.Events.Count);
    }

    // ── Event subscription service tests ────────────────────

    [Fact]
    public async Task SubscriptionService_ReplaysHistory()
    {
        var svc = new AgentEventSubscriptionService(_db);
        var runId = Guid.NewGuid();
        var chat = new Chat { Title = "event replay" };
        var turn = new Turn { ChatId = chat.Id };
        _db.Set<Chat>().Add(chat);
        _db.Set<Turn>().Add(turn);
        _db.Set<AgentRun>().Add(new AgentRun
        {
            Id = runId,
            ChatId = chat.Id,
            TurnId = turn.Id,
            Status = AgentRunStatuses.Running,
            UserRequest = "test"
        });

        // Create historical events in DB
        _db.Set<AgentEvent>().Add(new AgentEvent
        {
            AgentRunId = runId,
            SequenceNumber = 1,
            EventType = AgentEventTypes.RunStarted,
            Summary = "Started"
        });
        _db.Set<AgentEvent>().Add(new AgentEvent
        {
            AgentRunId = runId,
            SequenceNumber = 2,
            EventType = AgentEventTypes.ModelRequest,
            Summary = "Request"
        });
        await _db.SaveChangesAsync();

        var reader = svc.Subscribe(runId);
        var events = new List<AgentEvent>();

        while (reader.TryRead(out var evt))
            events.Add(evt);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.RunStarted);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ModelRequest);
    }

    [Fact]
    public void SubscriptionService_PublishAndComplete()
    {
        var svc = new AgentEventSubscriptionService(_db);
        var runId = Guid.NewGuid();
        var reader = svc.Subscribe(runId);
        var secondReader = svc.Subscribe(runId);

        var evt = new AgentEvent
        {
            AgentRunId = runId,
            SequenceNumber = 1,
            EventType = "test_event",
            Summary = "Live event"
        };
        svc.Publish(runId, evt);

        // Read the published event
        Assert.True(reader.TryRead(out var received));
        Assert.Equal("test_event", received.EventType);
        Assert.Equal("Live event", received.Summary);
        Assert.True(secondReader.TryRead(out var secondReceived));
        Assert.Equal("test_event", secondReceived.EventType);

        svc.Complete(runId);
        Assert.False(reader.TryRead(out _));
        Assert.False(secondReader.TryRead(out _));
    }

    [Fact]
    public void SubscriptionService_UnknownRunReturnsEmpty()
    {
        var svc = new AgentEventSubscriptionService(_db);
        var reader = svc.Subscribe(Guid.NewGuid());

        // No historical or live events are available immediately.
        Assert.False(reader.TryRead(out _));
    }

    // ── AgentRunFrameKinds tests ─────────────────────────────

    [Fact]
    public void AgentRunFrameKinds_AllDefined()
    {
        Assert.Equal("model_request", AgentRunFrameKinds.ModelRequest);
        Assert.Equal("model_response", AgentRunFrameKinds.ModelResponse);
        Assert.Equal("tool_batch_planned", AgentRunFrameKinds.ToolBatchPlanned);
        Assert.Equal("tool_executing", AgentRunFrameKinds.ToolExecuting);
        Assert.Equal("tool_progress", AgentRunFrameKinds.ToolProgress);
        Assert.Equal("tool_result", AgentRunFrameKinds.ToolResult);
        Assert.Equal("approval_needed", AgentRunFrameKinds.ApprovalNeeded);
        Assert.Equal("completed", AgentRunFrameKinds.Completed);
    }

    // ── AgentEngineOptions tests ─────────────────────────────

    [Fact]
    public void AgentEngineOptions_DefaultValues()
    {
        var options = new AgentEngineOptions();

        Assert.Equal(48, options.MaxSteps);
        Assert.Equal(120, options.CommandTimeoutSeconds);
        Assert.Equal(12000, options.MaxCommandOutputChars);
        Assert.False(options.AutoApproveTools);
        Assert.Equal(32_000, options.ContextBudgetTokens);
        Assert.Equal(24_000, options.AutoCompactTriggerTokens);
        Assert.Equal(AgentPermissionModes.RequestApproval, options.PermissionMode);
    }

    [Fact]
    public void AgentEngineOptions_CustomValues()
    {
        var options = new AgentEngineOptions(
            MaxSteps: 96,
            CommandTimeoutSeconds: 30,
            MaxCommandOutputChars: 24000,
            AutoApproveTools: true,
            ContextBudgetTokens: 64_000,
            AutoCompactTriggerTokens: 48_000,
            MaxToolResultCharsInContext: 12_000);

        Assert.Equal(96, options.MaxSteps);
        Assert.True(options.AutoApproveTools);
        Assert.Equal(64_000, options.ContextBudgetTokens);
        Assert.Equal(AgentPermissionModes.RequestApproval, options.PermissionMode);
    }

    // ── AgentRunResult tests ─────────────────────────────────

    [Fact]
    public void AgentRunResult_HoldsState()
    {
        var state = new AgentRunState { RunId = Guid.NewGuid(), Status = AgentRunStatuses.Completed };
        var events = new List<AgentEvent> { new() { EventType = "done" } };
        var result = new AgentRunResult(state, "final content", null, events);

        Assert.Equal(AgentRunStatuses.Completed, result.FinalState.Status);
        Assert.Equal("final content", result.AssistantContent);
        Assert.Single(result.Events);
    }
}
