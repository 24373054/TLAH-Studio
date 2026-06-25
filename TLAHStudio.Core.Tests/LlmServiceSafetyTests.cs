using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class LlmServiceSafetyTests
{
    [Fact]
    public async Task SendMessageAsync_DoesNotPersistApiKeyInRawDebugPayload()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-testsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Safety");
        var handler = new MapHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer sk-testsecretvalue123456", request.Headers.Authorization!.ToString());
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, """
            {
              "choices": [
                { "message": { "content": "ok" } }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """);
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.SendMessageAsync(chat.Id, "hello");

        Assert.DoesNotContain("sk-testsecretvalue123456", result.RawRequest.RequestJson);
        Assert.DoesNotContain("sk-testsecretvalue123456", result.RawResponse.ResponseJson);
        Assert.DoesNotContain("authorization", result.RawRequest.RequestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_NormalizesToolHistoryBeforeProviderCall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "anthropic",
            ApiKey: "sk-toolhistorysecret123456",
            BaseUrl: "https://api.example.com",
            Model: "claude-test"));
        var chat = await chatService.CreateChatAsync("Tool History");
        db.Set<Message>().Add(new Message
        {
            ChatId = chat.Id,
            Role = "tool",
            Content = "Sandbox result #1\nSTDOUT:\nok",
            SequenceNum = 1
        });
        await db.SaveChangesAsync();

        var handler = new MapHttpMessageHandler(request =>
        {
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, """
            {
              "content": [
                { "type": "text", "text": "continued" }
              ],
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """);
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client));

        await service.SendMessageAsync(chat.Id, "Continue.");
        var capturedRequestJson = await handler.Requests[0].Content!.ReadAsStringAsync();

        Assert.DoesNotContain("\"role\":\"tool\"", capturedRequestJson, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(capturedRequestJson);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Contains("[tool]", messages[0].GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
