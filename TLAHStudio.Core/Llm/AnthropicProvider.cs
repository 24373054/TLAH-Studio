using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TLAHStudio.Core.Llm;

/// <summary>
/// Calls the Anthropic Messages API via raw HttpClient.
/// Maps 1:1 from AnthropicProvider in llm/anthropic_provider.py.
///
/// Uses the dedicated `system` top-level field (not injected into messages),
/// which is Anthropic's native approach.
/// </summary>
public class AnthropicProvider : ILlmProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;

    public string ProviderName => "anthropic";

    public string EndpointUrl => $"{_baseUrl}/v1/messages";

    public AnthropicProvider(HttpClient http, string apiKey, string baseUrl, string model)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<LlmResponse> ChatAsync(
        List<MessagePayload> messages,
        string systemPrompt,
        double temperature = 0.7,
        int maxTokens = 4096,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        IProgress<LlmStreamUpdate>? stream = null,
        CancellationToken ct = default)
    {
        // Build raw request (Anthropic uses separate "system" field)
        var rawRequest = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["system"] = systemPrompt,
            ["messages"] = messages.Select(BuildMessage).ToArray(),
            ["temperature"] = temperature
        };
        if (tools is { Count: > 0 })
        {
            rawRequest["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.InputSchema
            }).ToArray();
        }

        if (stream != null)
        {
            rawRequest["stream"] = true;
            return await ChatStreamAsync(rawRequest, stream, ct);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
        {
            Content = JsonContent.Create(rawRequest)
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct);
        sw.Stop();
        var latencyMs = (int)sw.ElapsedMilliseconds;

        Dictionary<string, object> rawResponse;
        try
        {
            rawResponse = (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct))!
                         ?? new Dictionary<string, object> { ["_error"] = "Failed to parse response JSON" };
        }
        catch
        {
            rawResponse = new Dictionary<string, object>
            {
                ["_error"] = "Failed to parse response JSON",
                ["_body"] = await response.Content.ReadAsStringAsync(ct)
            };
        }

        // Extract assistant text from content blocks
        string assistantText = "";
        string? reasoningText = null;
        Dictionary<string, int>? tokenUsage = null;
        string? error = null;
        var toolCalls = new List<LlmToolCall>();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                if (rawResponse.TryGetValue("content", out var contentObj) &&
                    contentObj is JsonElement contentElement && contentElement.ValueKind == JsonValueKind.Array)
                {
                    var textParts = new List<string>();
                    foreach (var block in contentElement.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var typeEl) &&
                            typeEl.GetString() == "text" &&
                            block.TryGetProperty("text", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                textParts.Add(text);
                        }
                        else if (block.TryGetProperty("type", out typeEl) &&
                                 typeEl.GetString() == "thinking" &&
                                 block.TryGetProperty("thinking", out var thinkingEl))
                        {
                            var thinking = thinkingEl.GetString();
                            if (!string.IsNullOrWhiteSpace(thinking))
                                reasoningText = string.IsNullOrWhiteSpace(reasoningText)
                                    ? thinking
                                    : reasoningText + thinking;
                        }
                        else if (block.TryGetProperty("type", out typeEl) &&
                                 typeEl.GetString() == "tool_use")
                        {
                            var inputJson = block.TryGetProperty("input", out var input)
                                ? input.GetRawText()
                                : "{}";
                            toolCalls.Add(new LlmToolCall(
                                block.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                                block.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                                inputJson));
                        }
                    }
                    assistantText = string.Join("\n", textParts);
                }

                if (rawResponse.TryGetValue("usage", out var usageObj) && usageObj is JsonElement usageElement)
                {
                    int inputTokens = 0, outputTokens = 0;
                    if (usageElement.TryGetProperty("input_tokens", out var it))
                        inputTokens = it.GetInt32();
                    if (usageElement.TryGetProperty("output_tokens", out var ot))
                        outputTokens = ot.GetInt32();
                    tokenUsage = new Dictionary<string, int>
                    {
                        ["input_tokens"] = inputTokens,
                        ["output_tokens"] = outputTokens,
                        ["total_tokens"] = inputTokens + outputTokens
                    };
                }
            }
            catch (Exception e)
            {
                error = $"Failed to extract response: {e.Message}";
                assistantText = $"[Error extracting response: {e.Message}]";
            }
        }
        else
        {
            string errorMsg = response.ReasonPhrase ?? "Unknown error";
            if (rawResponse.TryGetValue("error", out var errorObj) && errorObj is JsonElement errEl &&
                errEl.TryGetProperty("message", out var msgEl))
            {
                errorMsg = msgEl.GetString() ?? errorMsg;
            }
            error = errorMsg;
            assistantText = $"[API Error {(int)response.StatusCode}: {errorMsg}]";
        }

        return new LlmResponse(
            RawRequest: rawRequest,
            RawResponse: rawResponse,
            HttpStatus: (int)response.StatusCode,
            LatencyMs: latencyMs,
            AssistantText: assistantText,
            TokenUsage: tokenUsage,
            Error: error,
            ToolCalls: toolCalls,
            ReasoningText: reasoningText
        );
    }

    private async Task<LlmResponse> ChatStreamAsync(
        Dictionary<string, object> rawRequest,
        IProgress<LlmStreamUpdate> stream,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
        {
            Content = JsonContent.Create(rawRequest)
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Accept.ParseAdd("text/event-stream");

        var sw = Stopwatch.StartNew();
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var latencyMs = (int)sw.ElapsedMilliseconds;
        if (!response.IsSuccessStatusCode)
        {
            sw.Stop();
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var error = ExtractErrorMessage(errorBody, response.ReasonPhrase ?? "Unknown error");
            var rawError = TryParseJsonObject(errorBody) ??
                           new Dictionary<string, object> { ["_body"] = errorBody };
            return new LlmResponse(
                rawRequest,
                rawError,
                (int)response.StatusCode,
                latencyMs,
                $"[API Error {(int)response.StatusCode}: {error}]",
                Error: error);
        }

        var chunks = new List<string>();
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var toolBuilders = new Dictionary<int, AnthropicToolCallBuilder>();
        Dictionary<string, int>? tokenUsage = null;
        var textStarted = false;

        await using var streamBody = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(streamBody);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) ||
                !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                break;

            chunks.Add(data);
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString()
                    : null;

                if (type == "content_block_start" &&
                    root.TryGetProperty("index", out var indexEl) &&
                    indexEl.ValueKind == JsonValueKind.Number &&
                    root.TryGetProperty("content_block", out var block) &&
                    block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("type", out var blockType) &&
                    blockType.GetString() == "tool_use")
                {
                    var index = indexEl.GetInt32();
                    var builder = new AnthropicToolCallBuilder();
                    if (block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        builder.Id = id.GetString();
                    if (block.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        builder.Name = name.GetString();
                    toolBuilders[index] = builder;
                    continue;
                }

                if (type == "content_block_delta" &&
                    root.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("type", out var deltaType) &&
                        deltaType.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var textPart) &&
                        textPart.ValueKind == JsonValueKind.String)
                    {
                        var part = textPart.GetString();
                        if (!string.IsNullOrEmpty(part))
                        {
                            if (!textStarted)
                            {
                                textStarted = true;
                                stream.Report(new LlmStreamUpdate(
                                    string.Empty,
                                    text.ToString(),
                                    LlmStreamEventTypes.TextStarted));
                            }
                            text.Append(part);
                            stream.Report(new LlmStreamUpdate(
                                part,
                                text.ToString(),
                                LlmStreamEventTypes.TextDelta));
                        }
                    }
                    else if (delta.TryGetProperty("type", out deltaType) &&
                             deltaType.GetString() == "thinking_delta" &&
                             delta.TryGetProperty("thinking", out var thinkingPart) &&
                             thinkingPart.ValueKind == JsonValueKind.String)
                    {
                        var part = thinkingPart.GetString();
                        if (!string.IsNullOrEmpty(part))
                        {
                            thinking.Append(part);
                            stream.Report(new LlmStreamUpdate(
                                part,
                                thinking.ToString(),
                                LlmStreamEventTypes.ThinkingDelta));
                        }
                    }
                    else if (delta.TryGetProperty("type", out deltaType) &&
                             deltaType.GetString() == "input_json_delta" &&
                             delta.TryGetProperty("partial_json", out var partial) &&
                             partial.ValueKind == JsonValueKind.String &&
                             root.TryGetProperty("index", out indexEl) &&
                             indexEl.ValueKind == JsonValueKind.Number)
                    {
                        var index = indexEl.GetInt32();
                        if (!toolBuilders.TryGetValue(index, out var builder))
                        {
                            builder = new AnthropicToolCallBuilder();
                            toolBuilders[index] = builder;
                        }
                        builder.InputJson.Append(partial.GetString());
                    }
                }

                if (type == "message_delta" &&
                    root.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                    tokenUsage = ParseUsage(usage, tokenUsage);
            }
            catch
            {
                // Keep malformed stream chunks in rawResponse for diagnostics.
            }
        }
        sw.Stop();

        stream.Report(new LlmStreamUpdate(
            string.Empty,
            text.ToString(),
            LlmStreamEventTypes.TextDelta,
            IsFinal: true));
        var rawResponse = new Dictionary<string, object>
        {
            ["stream"] = true,
            ["chunks"] = chunks,
            ["assistant_text"] = text.ToString()
        };
        if (thinking.Length > 0)
            rawResponse["reasoning_text"] = thinking.ToString();
        if (tokenUsage != null)
            rawResponse["usage"] = tokenUsage;

        var toolCalls = toolBuilders
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.ToToolCall())
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToArray();

        return new LlmResponse(
            rawRequest,
            rawResponse,
            (int)response.StatusCode,
            (int)sw.ElapsedMilliseconds,
            text.ToString(),
            tokenUsage,
            ToolCalls: toolCalls,
            ReasoningText: thinking.Length == 0 ? null : thinking.ToString());
    }

    private static object BuildMessage(MessagePayload message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            var content = new List<object>();
            if (!string.IsNullOrWhiteSpace(message.Content))
                content.Add(new { type = "text", text = message.Content });
            content.AddRange(message.ToolCalls.Select(t => (object)new
            {
                type = "tool_use",
                id = t.Id,
                name = t.Name,
                input = ParseArguments(t.ArgumentsJson)
            }));
            return new { role = "assistant", content = content.ToArray() };
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "tool_result",
                        tool_use_id = message.ToolCallId,
                        content = message.Content
                    }
                }
            };
        }

        return new { role = message.Role, content = message.Content };
    }

    private static object ParseArguments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json)
                   ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    private static Dictionary<string, int> ParseUsage(
        JsonElement usageElement,
        Dictionary<string, int>? previous)
    {
        var tokenUsage = previous == null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(previous);
        if (usageElement.TryGetProperty("input_tokens", out var it) &&
            it.ValueKind == JsonValueKind.Number)
            tokenUsage["input_tokens"] = it.GetInt32();
        if (usageElement.TryGetProperty("output_tokens", out var ot) &&
            ot.ValueKind == JsonValueKind.Number)
            tokenUsage["output_tokens"] = ot.GetInt32();
        if (tokenUsage.TryGetValue("input_tokens", out var input) &&
            tokenUsage.TryGetValue("output_tokens", out var output))
            tokenUsage["total_tokens"] = input + output;
        return tokenUsage;
    }

    private static Dictionary<string, object>? TryParseJsonObject(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractErrorMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message))
                return message.GetString() ?? fallback;
        }
        catch
        {
        }

        return fallback;
    }

    private sealed class AnthropicToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder InputJson { get; } = new();

        public LlmToolCall ToToolCall() => new(
            string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id!,
            Name ?? string.Empty,
            InputJson.Length == 0 ? "{}" : InputJson.ToString());
    }
}
