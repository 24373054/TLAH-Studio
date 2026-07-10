using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

/// <summary>Regression coverage for the 4.9.9 paged conversation path.</summary>
public class Release499Tests
{
    [Fact]
    public async Task GetChatMessagePage_NewestPage_IsChronologicalAndReportsOlderHistory()
    {
        await using var db = TestDb.Create();
        var service = new ChatService(db);
        var chat = await service.CreateChatAsync("Paged chat");
        db.Set<Message>().AddRange(Enumerable.Range(1, 7).Select(sequence => new Message
        {
            ChatId = chat.Id,
            Role = sequence % 2 == 0 ? "assistant" : "user",
            Content = $"message {sequence}",
            SequenceNum = sequence
        }));
        await db.SaveChangesAsync();

        var page = await service.GetChatMessagePageAsync(chat.Id, pageSize: 3);

        Assert.True(page.HasMore);
        Assert.Equal([5, 6, 7], page.Messages.Select(message => message.SequenceNum).ToArray());
    }

    [Fact]
    public async Task GetChatMessagePage_BeforeSequence_ReturnsOlderWindowWithoutDuplicates()
    {
        await using var db = TestDb.Create();
        var service = new ChatService(db);
        var chat = await service.CreateChatAsync("Paged chat");
        db.Set<Message>().AddRange(Enumerable.Range(1, 5).Select(sequence => new Message
        {
            ChatId = chat.Id,
            Content = sequence.ToString(),
            SequenceNum = sequence
        }));
        await db.SaveChangesAsync();

        var page = await service.GetChatMessagePageAsync(chat.Id, beforeSequenceNum: 4, pageSize: 3);

        Assert.False(page.HasMore);
        Assert.Equal([1, 2, 3], page.Messages.Select(message => message.SequenceNum).ToArray());
    }
}
