using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class PrivacyServiceTests
{
    [Fact]
    public async Task ExportAllDataAsync_ExcludesApiKeysAndRedactsRawPayloads()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chat = new Chat { Title = "Privacy" };
        db.Chats.Add(chat);
        var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
        db.Turns.Add(turn);
        db.Messages.Add(new Message { ChatId = chat.Id, TurnId = turn.Id, Role = "user", Content = "hi", SequenceNum = 1 });
        db.RawRequests.Add(new RawRequest
        {
            TurnId = turn.Id,
            Provider = "openai_compat",
            EndpointUrl = "https://api.example.com/v1/chat/completions",
            RequestJson = """{"api_key":"sk-testsecretvalue123456","messages":[{"role":"user","content":"hi"}]}"""
        });
        var settings = await db.GlobalSettings.FindAsync(1);
        settings!.ApiKey = ProtectedSecret.Protect("sk-testsecretvalue123456");
        await db.SaveChangesAsync();
        var service = new PrivacyService(db);
        var path = Path.Combine(Path.GetTempPath(), $"tlah-export-{Guid.NewGuid():N}.json");

        await service.ExportAllDataAsync(path);
        var exported = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain("sk-testsecretvalue123456", exported);
        Assert.Contains(SecretRedactor.Redacted, exported);
    }

    [Fact]
    public async Task ClearAllDataAsync_RemovesChatsAndApiKeys()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        db.Chats.Add(new Chat { Title = "Clear" });
        var settings = await db.GlobalSettings.FindAsync(1);
        settings!.ApiKey = ProtectedSecret.Protect("sk-testsecretvalue123456");
        await db.SaveChangesAsync();
        var service = new PrivacyService(db);

        await service.ClearAllDataAsync();
        var summary = await service.GetSummaryAsync();
        var reset = await db.GlobalSettings.FindAsync(1);

        Assert.Equal(0, summary.ChatCount);
        Assert.Equal(string.Empty, reset!.ApiKey);
    }
}
