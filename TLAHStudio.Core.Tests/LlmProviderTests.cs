using System.Net;
using System.Text.Json;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Tests;

public class LlmProviderTests
{
    [Fact]
    public async Task OpenAICompatibleProvider_BuildsExpectedRequestAndExtractsResponse()
    {
        var handler = new MapHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, """
            {
              "choices": [
                { "message": { "content": "你好" } }
              ],
              "usage": {
                "prompt_tokens": 2,
                "completion_tokens": 3,
                "total_tokens": 5
              }
            }
            """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, "sk-test", "https://api.example.com", "model-a");

        var result = await provider.ChatAsync([new MessagePayload("user", "hi")], "system prompt", 0.2, 128);

        Assert.Equal("你好", result.AssistantText);
        Assert.Equal(5, result.TokenUsage!["total_tokens"]);
        var requestJson = JsonSerializer.Serialize(result.RawRequest);
        Assert.Contains("\"model\":\"model-a\"", requestJson);
        Assert.Contains("\"role\":\"system\"", requestJson);
        Assert.Contains("\"content\":\"system prompt\"", requestJson);
    }

    [Fact]
    public async Task AnthropicProvider_BuildsExpectedRequestAndExtractsResponse()
    {
        var handler = new MapHttpMessageHandler(request =>
        {
            Assert.Equal("https://anthropic.example.com/v1/messages", request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("x-api-key", out var apiKeys));
            Assert.Equal("sk-test", Assert.Single(apiKeys));
            Assert.True(request.Headers.TryGetValues("anthropic-version", out var versions));
            Assert.Equal("2023-06-01", Assert.Single(versions));
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, """
            {
              "content": [
                { "type": "text", "text": "第一段" },
                { "type": "text", "text": "第二段" }
              ],
              "usage": {
                "input_tokens": 7,
                "output_tokens": 11
              }
            }
            """);
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client, "sk-test", "https://anthropic.example.com", "claude-test");

        var result = await provider.ChatAsync([new MessagePayload("user", "hi")], "system prompt", 0.2, 128);

        Assert.Equal("第一段\n第二段", result.AssistantText);
        Assert.Equal(18, result.TokenUsage!["total_tokens"]);
        var requestJson = JsonSerializer.Serialize(result.RawRequest);
        Assert.Contains("\"model\":\"claude-test\"", requestJson);
        Assert.Contains("\"system\":\"system prompt\"", requestJson);
    }

    [Fact]
    public async Task OpenAICompatibleProvider_SendsToolsAndExtractsNativeToolCall()
    {
        var handler = new MapHttpMessageHandler(request =>
        {
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, """
            {
              "choices": [{
                "message": {
                  "content": null,
                  "tool_calls": [{
                    "id": "call-42",
                    "type": "function",
                    "function": {
                      "name": "sandbox_exec",
                      "arguments": "{\"command\":\"Get-ChildItem\"}"
                    }
                  }]
                }
              }]
            }
            """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, "sk-test", "https://api.example.com", "model-a");
        var tools = new[]
        {
            new LlmToolDefinition("sandbox_exec", "Run a command.", new Dictionary<string, object>
            {
                ["type"] = "object"
            })
        };

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "list files")],
            "system",
            tools: tools);

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        Assert.Contains("\"tools\"", body);
        Assert.Contains("\"sandbox_exec\"", body);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("call-42", call.Id);
        Assert.Equal("sandbox_exec", call.Name);
        Assert.Contains("Get-ChildItem", call.ArgumentsJson);
    }

    [Fact]
    public async Task AnthropicProvider_ExtractsNativeToolUse()
    {
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "content": [{
            "type": "tool_use",
            "id": "toolu-1",
            "name": "sandbox_exec",
            "input": { "command": "Get-ChildItem" }
          }]
        }
        """));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client, "sk-test", "https://anthropic.example.com", "claude-test");

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "list files")],
            "system",
            tools:
            [
                new LlmToolDefinition("sandbox_exec", "Run a command.", new Dictionary<string, object>
                {
                    ["type"] = "object"
                })
            ]);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("toolu-1", call.Id);
        Assert.Equal("sandbox_exec", call.Name);
        Assert.Contains("Get-ChildItem", call.ArgumentsJson);
    }
}
