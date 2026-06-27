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
    public async Task DeepSeekOpenAIProvider_StripsLongContextSuffixFromWireModel()
    {
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "choices": [
            { "message": { "content": "OK" } }
          ]
        }
        """));
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(
            client,
            "sk-test",
            "https://api.deepseek.com",
            "deepseek-v4-pro[1m]",
            "deepseek");

        await provider.ChatAsync([new MessagePayload("user", "hi")], "system");

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        Assert.Contains("\"model\":\"deepseek-v4-pro\"", body);
        Assert.DoesNotContain("deepseek-v4-pro[1m]", body);
    }

    [Fact]
    public async Task DeepSeekOpenAIProvider_AddsReasoningDepthRequestOptions()
    {
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "choices": [
            { "message": { "content": "OK" } }
          ]
        }
        """));
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(
            client,
            "sk-test",
            "https://api.deepseek.com",
            "deepseek-v4-pro",
            "deepseek");

        await provider.ChatAsync(
            [new MessagePayload("user", "hi")],
            "system",
            reasoning: new LlmReasoningOptions(ReasoningDepths.Max));

        var body = await Assert.Single(handler.Requests).Content!.ReadAsStringAsync();
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", body);
        Assert.Contains("\"reasoning_effort\":\"max\"", body);
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
    public async Task OpenAICompatibleProvider_StreamsTextDeltas()
    {
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            data: {"choices":[{"delta":{"content":"你"}}]}

            data: {"choices":[{"delta":{"content":"好"}}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}

            data: [DONE]

            """, System.Text.Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, "sk-test", "https://api.example.com", "model-a");
        var stream = new CollectingStreamProgress();

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "hi")],
            "system",
            stream: stream);

        Assert.Equal("你好", result.AssistantText);
        Assert.Equal(["你", "好"], stream.Deltas);
        Assert.Contains(stream.Updates, u => u.IsFinal);
        Assert.Equal(3, result.TokenUsage!["total_tokens"]);
        var request = Assert.Single(handler.Requests);
        Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"stream\":true", body);
    }

    [Fact]
    public async Task OpenAICompatibleProvider_StreamsReasoningBeforeText()
    {
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            data: {"choices":[{"delta":{"reasoning_content":"想"}}]}

            data: {"choices":[{"delta":{"reasoning_content":"一想"}}]}

            data: {"choices":[{"delta":{"content":"答"}}]}

            data: {"choices":[{"delta":{"content":"案"}}]}

            data: [DONE]

            """, System.Text.Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, "sk-test", "https://api.example.com", "model-a");
        var stream = new CollectingStreamProgress();

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "hi")],
            "system",
            stream: stream);

        Assert.Equal("答案", result.AssistantText);
        Assert.Equal("想一想", result.ReasoningText);
        Assert.Equal(["想", "一想"], stream.ThinkingDeltas);
        Assert.Equal(["答", "案"], stream.TextDeltas);
        Assert.Contains(stream.Updates, u => u.EventType == LlmStreamEventTypes.TextStarted);
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

    [Fact]
    public async Task AnthropicProvider_StreamsTextDeltas()
    {
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"第"}}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"一"}}

            data: {"type":"message_delta","usage":{"output_tokens":2}}

            data: {"type":"message_stop"}

            """, System.Text.Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client, "sk-test", "https://anthropic.example.com", "claude-test");
        var stream = new CollectingStreamProgress();

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "hi")],
            "system",
            stream: stream);

        Assert.Equal("第一", result.AssistantText);
        Assert.Equal(["第", "一"], stream.Deltas);
        Assert.Contains(stream.Updates, u => u.IsFinal);
        Assert.Equal(2, result.TokenUsage!["output_tokens"]);
        var request = Assert.Single(handler.Requests);
        Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"stream\":true", body);
    }

    [Fact]
    public async Task AnthropicProvider_StreamsThinkingBeforeText()
    {
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"先"}}

            data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"想"}}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"回"}}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"答"}}

            data: {"type":"message_stop"}

            """, System.Text.Encoding.UTF8, "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client, "sk-test", "https://anthropic.example.com", "claude-test");
        var stream = new CollectingStreamProgress();

        var result = await provider.ChatAsync(
            [new MessagePayload("user", "hi")],
            "system",
            stream: stream);

        Assert.Equal("回答", result.AssistantText);
        Assert.Equal("先想", result.ReasoningText);
        Assert.Equal(["先", "想"], stream.ThinkingDeltas);
        Assert.Equal(["回", "答"], stream.TextDeltas);
        Assert.Contains(stream.Updates, u => u.EventType == LlmStreamEventTypes.TextStarted);
    }

    private sealed class CollectingStreamProgress : IProgress<LlmStreamUpdate>
    {
        public List<LlmStreamUpdate> Updates { get; } = [];
        public List<string> Deltas { get; } = [];
        public List<string> TextDeltas { get; } = [];
        public List<string> ThinkingDeltas { get; } = [];

        public void Report(LlmStreamUpdate value)
        {
            Updates.Add(value);
            if (!string.IsNullOrEmpty(value.Delta))
            {
                Deltas.Add(value.Delta);
                if (value.EventType == LlmStreamEventTypes.ThinkingDelta)
                    ThinkingDeltas.Add(value.Delta);
                else if (value.EventType == LlmStreamEventTypes.TextDelta)
                    TextDeltas.Add(value.Delta);
            }
        }
    }
}
