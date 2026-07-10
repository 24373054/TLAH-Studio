using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class CheckpointStoreTests
{
    [Fact]
    public async Task SaveAndLoad_ProtectsStateAtRestAndReturnsPlainState()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "checkpoint" };
        var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
        var run = new AgentRun { ChatId = chat.Id, TurnId = turn.Id, UserRequest = "resume" };
        db.Set<Chat>().Add(chat);
        db.Set<Turn>().Add(turn);
        db.Set<AgentRun>().Add(run);
        await db.SaveChangesAsync();
        var store = new CheckpointStore(db);
        const string stateJson = """{"token":"sk-checkpointsecret123456"}""";

        await store.SaveAsync(run, 1, stateJson);

        var stored = await db.Set<AgentCheckpoint>().SingleAsync();
        if (OperatingSystem.IsWindows())
        {
            Assert.True(ProtectedSecret.IsProtected(stored.StateJson));
            Assert.DoesNotContain("sk-checkpointsecret123456", stored.StateJson, StringComparison.Ordinal);
        }
        var loaded = await store.GetLatestAsync(run.Id);
        Assert.NotNull(loaded);
        Assert.Equal(stateJson, loaded.StateJson);
    }
}
