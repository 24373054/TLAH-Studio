using System.Reflection;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.Background;

namespace TLAHStudio.Core.Tests;

public class BackgroundTaskServiceTests
{
    [Fact]
    public async Task UpdateRecordAsync_UsesIndependentContextForCompletion()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "background persistence" };
        db.Set<Chat>().Add(chat);
        var record = new BackgroundTaskRecord
        {
            ChatId = chat.Id,
            Description = "persist",
            Status = "running"
        };
        db.Set<BackgroundTaskRecord>().Add(record);
        await db.SaveChangesAsync();
        var service = new BackgroundTaskService(db);
        var completed = new BackgroundTask(
            record.Id,
            chat.Id,
            record.Description,
            "completed",
            record.StartedAt,
            DateTime.UtcNow,
            "done",
            null);
        var updateMethod = typeof(BackgroundTaskService)
            .GetMethod("UpdateRecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)updateMethod.Invoke(service, [completed])!;

        db.ChangeTracker.Clear();
        var reloaded = await db.Set<BackgroundTaskRecord>().FindAsync(record.Id);
        Assert.Equal("completed", reloaded!.Status);
    }

    [Fact]
    public async Task CompletedTask_RefreshesPersistedStatusAndReleasesRuntimeEntry()
    {
        await using var db = TestDb.Create();
        var chat = new Chat { Title = "background completion" };
        db.Set<Chat>().Add(chat);
        await db.SaveChangesAsync();
        var service = new BackgroundTaskService(db);

        var created = await service.CreateAsync(chat.Id, "finish", _ => Task.CompletedTask);
        BackgroundTask? persisted = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20);
            persisted = await service.GetAsync(created.Id);
            if (persisted?.Status == "completed")
                break;
        }

        Assert.NotNull(persisted);
        Assert.Equal("completed", persisted.Status);
        var running = typeof(BackgroundTaskService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var countProperty = running.GetType().GetProperty("Count")!;
        var runningCount = (int)countProperty.GetValue(running)!;
        for (var attempt = 0; attempt < 20 && runningCount != 0; attempt++)
        {
            await Task.Delay(10);
            runningCount = (int)countProperty.GetValue(running)!;
        }
        Assert.Equal(0, runningCount);
    }
}
