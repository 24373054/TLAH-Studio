using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class AgentRuntimeStreamTests
{
    [Fact]
    public async Task AgentEventStreamBatchesEventsAndPreservesSequenceNumbers()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "Runtime stream" };
        var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
        var run = new AgentRun
        {
            ChatId = chat.Id,
            TurnId = turn.Id,
            Status = AgentRunStatuses.Running
        };
        db.Set<Chat>().Add(chat);
        db.Set<Turn>().Add(turn);
        db.Set<AgentRun>().Add(run);
        await db.SaveChangesAsync();
        var stream = new AgentEventStream(db);

        using (stream.BeginRun(run))
        {
            await stream.AppendAsync(new AgentEventAppendRequest(
                run,
                AgentEventTypes.ModelRequest,
                "first"));
            await stream.AppendAsync(new AgentEventAppendRequest(
                run,
                AgentEventTypes.ToolRequest,
                "second"));

            Assert.Equal(0, await db.Set<AgentEvent>().CountAsync());
            Assert.Equal(2, stream.GetMetrics().PendingCount);

            await stream.FlushAsync();
        }

        var events = await db.Set<AgentEvent>()
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();
        Assert.Collection(
            events,
            first => Assert.Equal(1, first.SequenceNumber),
            second => Assert.Equal(2, second.SequenceNumber));
        Assert.Equal(1, stream.GetMetrics().FlushCount);
    }
}
