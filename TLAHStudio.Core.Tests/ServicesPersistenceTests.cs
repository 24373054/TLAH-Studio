using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class ServicesPersistenceTests
{
    [Fact]
    public async Task SettingsService_ProtectsMasksAndClearsGlobalApiKey_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var service = new SettingsService(db);

        await service.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(ApiKey: "sk-1234567890abcdef"));
        var raw = await service.GetGlobalSettingsRawAsync();
        var masked = await service.GetGlobalSettingsMaskedAsync();

        Assert.True(ProtectedSecret.IsProtected(raw.ApiKey));
        Assert.Equal("sk-1***********cdef", masked.ApiKey);
        Assert.True(await service.IsConfiguredAsync());

        await service.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(ApiKey: masked.ApiKey));
        Assert.Equal("sk-1234567890abcdef", ProtectedSecret.Reveal(raw.ApiKey));

        await service.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(ApiKey: string.Empty));
        Assert.False(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task ChatService_HidesDeletedChatsAndKeepsPinnedFirst()
    {
        await using var db = TestDb.Create();
        var service = new ChatService(db);
        var regular = await service.CreateChatAsync("Regular");
        var pinned = await service.CreateChatAsync("Pinned");
        var deleted = await service.CreateChatAsync("Deleted");
        await service.SetPinnedAsync(pinned.Id, true);
        await service.DeleteChatAsync(deleted.Id);

        var chats = await service.ListChatsAsync();

        Assert.Equal([pinned.Id, regular.Id], chats.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task ChatService_DeleteMessagesAfter_RemovesSelectedTail()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var chat = await chatService.CreateChatAsync("Chat");
        var messages = db.Set<Message>();
        var first = new Message { ChatId = chat.Id, Role = "user", Content = "1", SequenceNum = 1 };
        var second = new Message { ChatId = chat.Id, Role = "assistant", Content = "2", SequenceNum = 2 };
        var third = new Message { ChatId = chat.Id, Role = "user", Content = "3", SequenceNum = 3 };
        messages.AddRange(first, second, third);
        await db.SaveChangesAsync();

        await chatService.DeleteMessagesAfterAsync(second.Id, includeSelected: true);
        var remaining = await chatService.GetChatMessagesAsync(chat.Id);

        Assert.Single(remaining);
        Assert.Equal(first.Id, remaining[0].Id);
    }

}
