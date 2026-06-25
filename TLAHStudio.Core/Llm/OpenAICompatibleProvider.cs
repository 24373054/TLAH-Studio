using System.Diagnostics;
using System.Net.Http.Json;
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

    public string ProviderName => "openai_compat";

    public string EndpointUrl => $"{_baseUrl}/v1/chat/completions";

    public OpenAICompatibleProvider(HttpClient http, string apiKey, string baseUrl, string model)
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
            ToolCalls: toolCalls
        );
    }

    private static object BuildMessage(MessagePayload message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = string.IsNullOrWhiteSpace(message.Content) ? null : message.Content,
                tool_calls = message.ToolCalls.Select(t => new
                {
                    id = t.Id,
                    type = "function",
                    function = new { name = t.Name, arguments = t.ArgumentsJson }
                }).ToArray()
            };
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
}
