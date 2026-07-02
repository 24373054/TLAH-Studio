using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TLAHStudio.Core.Llm;

/// <summary>
/// Calls any OpenAI-compatible chat completions API via raw HttpClient.
/// Maps 1:1 from OpenAICompatibleProvider in llm/openai_compat.py.
///
/// This includes OpenAI, DeepSeek, and any provider implementing
/// the POST /v1/chat/completions contract.
/// </summary>
public class OpenAICompatibleProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _providerName;

    public string ProviderName => _providerName;

    public string EndpointUrl => $"{_baseUrl}/v1/chat/completions";

    public OpenAICompatibleProvider(
        HttpClient http,
        string apiKey,
        string baseUrl,
        string model,
        string providerName = "openai_compat")
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = ProviderModelResolver.ToWireModel(providerName, _baseUrl, model);
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "openai_compat" : providerName;
    }

    public async Task<LlmResponse> ChatAsync(
        List<MessagePayload> messages,
        string systemPrompt,
        double temperature = 0.7,
        int maxTokens = 4096,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        IProgress<LlmStreamUpdate>? stream = null,
        LlmReasoningOptions? reasoning = null,
        CancellationToken ct = default)
    {
        // Build the full messages array with system prompt first.
        var fullMessages = new List<object> { new { role = "system", content = systemPrompt } };
        fullMessages.AddRange(messages.Select(BuildMessage));

        var rawRequest = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = fullMessages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens
        };
        if (tools is { Count: > 0 })
        {
            rawRequest["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToArray();
            rawRequest["tool_choice"] = "auto";
        }
        AddReasoningOptions(rawRequest, reasoning);

        if (stream != null)
        {
            rawRequest["stream"] = true;
            rawRequest["stream_options"] = new { include_usage = true };
            return await ChatStreamAsync(rawRequest, stream, ct);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
        {
            Content = JsonContent.Create(rawRequest)
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

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

        // Extract assistant text from choices[0].message.content
        string assistantText = "";
        string? reasoningText = null;
        Dictionary<string, int>? tokenUsage = null;
        string? error = null;
        var toolCalls = new List<LlmToolCall>();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                if (rawResponse.TryGetValue("choices", out var choicesObj) &&
                    choicesObj is JsonElement choicesElement && choicesElement.ValueKind == JsonValueKind.Array)
                {
                    var choices = choicesElement.EnumerateArray();
                    if (choices.Any())
                    {
                        var first = choices.First();
                        if (first.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content))
                        {
                            assistantText = content.GetString() ?? "";
                        }
                        if (first.TryGetProperty("message", out msg))
                            reasoningText = ExtractReasoning(msg);

                        if (first.TryGetProperty("message", out msg) &&
                            msg.TryGetProperty("tool_calls", out var toolCallsElement) &&
                            toolCallsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var call in toolCallsElement.EnumerateArray())
                            {
                                if (!call.TryGetProperty("function", out var function))
                                    continue;
                                toolCalls.Add(new LlmToolCall(
                                    call.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                                    function.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                                    function.TryGetProperty("arguments", out var arguments) ? arguments.GetString() ?? "{}" : "{}"));
                            }
                        }
                    }
                }

                if (rawResponse.TryGetValue("usage", out var usageObj) && usageObj is JsonElement usageElement)
                {
                    tokenUsage = new Dictionary<string, int>();
                    if (usageElement.TryGetProperty("prompt_tokens", out var pt))
                        tokenUsage["prompt_tokens"] = pt.GetInt32();
                    if (usageElement.TryGetProperty("completion_tokens", out var ct2))
                        tokenUsage["completion_tokens"] = ct2.GetInt32();
                    if (usageElement.TryGetProperty("total_tokens", out var tt))
                        tokenUsage["total_tokens"] = tt.GetInt32();
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

    private void AddReasoningOptions(Dictionary<string, object> rawRequest, LlmReasoningOptions? reasoning)
    {
        var depth = ReasoningDepths.Normalize(reasoning?.Depth);
        if (depth == ReasoningDepths.Auto)
            return;

        if (ProviderModelResolver.IsDeepSeekModel(_model))
        {
            if (depth == ReasoningDepths.Off)
            {
                rawRequest["thinking"] = new { type = "disabled" };
                return;
            }

            rawRequest["thinking"] = new { type = "enabled" };
            rawRequest["reasoning_effort"] = depth == ReasoningDepths.Max
                ? ReasoningDepths.Max
                : ReasoningDepths.High;
        }
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
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
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
        var toolBuilders = new Dictionary<int, OpenAIToolCallBuilder>();
        Dictionary<string, int>? tokenUsage = null;
        var textStarted = false;
        var pendingDelta = new StringBuilder(); // M4.4.5: SSE batching accumulator

        await using var streamBody = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(streamBody);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                break;

            chunks.Add(data);
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    tokenUsage = ParseUsage(usage);

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta) ||
                        delta.ValueKind != JsonValueKind.Object)
                        continue;

                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        var part = content.GetString();
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
                            pendingDelta.Append(part);
                            // M4.4.5: Batch SSE deltas to reduce IProgress callbacks.
                            // Each SSE line is ~1-5 tokens; batching 12+ chars worth
                            // reduces callbacks ~3-5× without adding perceptible latency.
                            if (pendingDelta.Length >= 12)
                            {
                                stream.Report(new LlmStreamUpdate(
                                    pendingDelta.ToString(),
                                    text.ToString(),
                                    LlmStreamEventTypes.TextDelta));
                                pendingDelta.Clear();
                            }
                        }
                    }

                    var reasoningPart = ExtractReasoning(delta);
                    if (!string.IsNullOrEmpty(reasoningPart))
                    {
                        thinking.Append(reasoningPart);
                        stream.Report(new LlmStreamUpdate(
                            reasoningPart,
                            thinking.ToString(),
                            LlmStreamEventTypes.ThinkingDelta));
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                        toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            var index = toolCall.TryGetProperty("index", out var indexEl) &&
                                        indexEl.ValueKind == JsonValueKind.Number
                                ? indexEl.GetInt32()
                                : toolBuilders.Count;
                            if (!toolBuilders.TryGetValue(index, out var builder))
                            {
                                builder = new OpenAIToolCallBuilder();
                                toolBuilders[index] = builder;
                            }

                            if (toolCall.TryGetProperty("id", out var id) &&
                                id.ValueKind == JsonValueKind.String)
                                builder.Id = id.GetString();
                            if (toolCall.TryGetProperty("function", out var function) &&
                                function.ValueKind == JsonValueKind.Object)
                            {
                                if (function.TryGetProperty("name", out var name) &&
                                    name.ValueKind == JsonValueKind.String)
                                    builder.Name = name.GetString();
                                if (function.TryGetProperty("arguments", out var args) &&
                                    args.ValueKind == JsonValueKind.String)
                                    builder.Arguments.Append(args.GetString());
                            }
                        }
                    }
                }
            }
            catch
            {
                // Keep malformed stream chunks in rawResponse; providers occasionally send comments.
            }
        }
        sw.Stop();

        // M4.4.5: Flush any remaining batched deltas before the final event.
        if (pendingDelta.Length > 0)
        {
            stream.Report(new LlmStreamUpdate(
                pendingDelta.ToString(),
                text.ToString(),
                LlmStreamEventTypes.TextDelta));
        }

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

        var toolCallsResult = toolBuilders
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
            ToolCalls: toolCallsResult,
            ReasoningText: thinking.Length == 0 ? null : thinking.ToString());
    }

    private static object BuildMessage(MessagePayload message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            var assistantMessage = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(message.Content) ? null : message.Content,
                ["tool_calls"] = message.ToolCalls.Select(t => new
                {
                    id = t.Id,
                    type = "function",
                    function = new { name = t.Name, arguments = t.ArgumentsJson }
                }).ToArray()
            };
            if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
                assistantMessage["reasoning_content"] = message.ReasoningContent;
            return assistantMessage;
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role = "tool",
                tool_call_id = message.ToolCallId,
                content = message.Content
            };
        }

        return new { role = message.Role, content = message.Content };
    }

    private static string? ExtractReasoning(JsonElement element)
    {
        foreach (var name in new[] { "reasoning_content", "reasoning", "thinking" })
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static Dictionary<string, int> ParseUsage(JsonElement usageElement)
    {
        var tokenUsage = new Dictionary<string, int>();
        if (usageElement.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
            tokenUsage["prompt_tokens"] = pt.GetInt32();
        if (usageElement.TryGetProperty("completion_tokens", out var ct2) && ct2.ValueKind == JsonValueKind.Number)
            tokenUsage["completion_tokens"] = ct2.GetInt32();
        if (usageElement.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
            tokenUsage["total_tokens"] = tt.GetInt32();
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

    private sealed class OpenAIToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public LlmToolCall ToToolCall() => new(
            string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id!,
            Name ?? string.Empty,
            Arguments.Length == 0 ? "{}" : Arguments.ToString());
    }
}
