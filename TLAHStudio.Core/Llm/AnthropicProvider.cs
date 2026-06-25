using System.Diagnostics;
using System.Net.Http.Json;
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
            ToolCalls: toolCalls
        );
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
}
