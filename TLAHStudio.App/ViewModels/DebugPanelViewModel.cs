using System.Collections.ObjectModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Manages the Debug Panel: open/close state, active turn, raw request/response data.
/// Maps from DebugPanelContext.tsx + DebugPanel.tsx.
/// </summary>
public partial class DebugPanelViewModel : ObservableObject
{
    private readonly IDebugService _debugService;
    private readonly ILlmService _llmService;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private Guid? _activeTurnId;

    [ObservableProperty]
    private string _selectedTab = "request";

    [ObservableProperty]
    private string _requestJson = string.Empty;

    [ObservableProperty]
    private string _responseJson = string.Empty;

    [ObservableProperty]
    private string _contextJson = string.Empty;

    [ObservableProperty]
    private string _tokensJson = string.Empty;

    [ObservableProperty]
    private string _timingJson = string.Empty;

    [ObservableProperty]
    private string _errorsJson = string.Empty;

    [ObservableProperty]
    private string _historyJson = string.Empty;

    [ObservableProperty]
    private string _compareJson = string.Empty;

    [ObservableProperty]
    private string _diffJson = string.Empty;

    [ObservableProperty]
    private string _costJson = string.Empty;

    [ObservableProperty]
    private string _replayJson = string.Empty;

    [ObservableProperty]
    private string _abJson = string.Empty;

    [ObservableProperty]
    private string _promptText = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _endpointUrl = string.Empty;

    [ObservableProperty]
    private int _httpStatusCode;

    [ObservableProperty]
    private int _latencyMs;

    [ObservableProperty]
    private string _tokenUsage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _replayStatus;

    // Meta items for the info bar
    public ObservableCollection<MetaItem> MetaItems { get; } = new();

    public event EventHandler<Guid>? TurnReplayed;

    public DebugPanelViewModel(IDebugService debugService, ILlmService llmService)
    {
        _debugService = debugService;
        _llmService = llmService;
    }

    [RelayCommand]
    public async Task OpenDebugAsync(Guid turnId)
    {
        ActiveTurnId = turnId;
        IsOpen = true;
        await LoadDataAsync(turnId);
    }

    [RelayCommand]
    public void CloseDebug()
    {
        IsOpen = false;
    }

    [RelayCommand]
    public void ToggleDebug(Guid turnId)
    {
        if (ActiveTurnId == turnId && IsOpen)
            CloseDebug();
        else
            _ = OpenDebugAsync(turnId);
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        SelectedTab = tab;
    }

    [RelayCommand]
    public void CopyJson()
    {
        var json = CurrentPayload();
        if (!string.IsNullOrEmpty(json))
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(FormatJson(json));
            Clipboard.SetContent(dataPackage);
        }
    }

    [RelayCommand]
    public void CopyCurl()
    {
        var curl = BuildCurl();
        if (string.IsNullOrWhiteSpace(curl))
            return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(curl);
        Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    public void CopyPrompt()
    {
        if (string.IsNullOrWhiteSpace(PromptText))
            return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(PromptText);
        Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    public async Task ReplayTurnAsync()
    {
        if (ActiveTurnId == null || IsLoading)
            return;

        IsLoading = true;
        ReplayStatus = "Replaying request...";
        try
        {
            var result = await _llmService.ReplayTurnAsync(ActiveTurnId.Value);
            ReplayStatus = $"Replayed as turn #{result.Turn.TurnNumber}.";
            TurnReplayed?.Invoke(this, result.Turn.Id);
            await LoadDataAsync(result.Turn.Id);
        }
        catch (Exception ex)
        {
            ReplayStatus = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void CopyAbPack()
    {
        if (string.IsNullOrWhiteSpace(AbJson))
            return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(AbJson);
        Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    public async Task ExportDebugBundleAsync()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "TLAH Studio");
        Directory.CreateDirectory(dir);

        var turnLabel = ActiveTurnId?.ToString("N") ?? "turn";
        if (turnLabel.Length > 8)
            turnLabel = turnLabel[..8];
        var path = Path.Combine(dir, $"debug-{turnLabel}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var bundle = new
        {
            ExportedAt = DateTime.UtcNow,
            TurnId = ActiveTurnId,
            Provider,
            EndpointUrl,
            HttpStatusCode,
            LatencyMs,
            TokenUsage,
            Request = ParseObject(RequestJson),
            Response = ParseObject(ResponseJson),
            Context = ParseObject(ContextJson),
            Tokens = ParseObject(TokensJson),
            Timing = ParseObject(TimingJson),
            Errors = ParseObject(ErrorsJson),
            History = ParseObject(HistoryJson),
            Compare = ParseObject(CompareJson),
            Diff = ParseObject(DiffJson),
            Cost = ParseObject(CostJson),
            Replay = ParseObject(ReplayJson),
            AB = ParseObject(AbJson),
            PromptText,
            Curl = BuildCurl()
        };
        await File.WriteAllTextAsync(path, SecretRedactor.RedactJson(JsonSerializer.Serialize(bundle, PrettyJsonOptions)));
    }

    private async Task LoadDataAsync(Guid turnId)
    {
        IsLoading = true;
        ErrorMessage = null;
        SelectedTab = "request";
        RequestJson = string.Empty;
        ResponseJson = string.Empty;
        ContextJson = string.Empty;
        TokensJson = string.Empty;
        TimingJson = string.Empty;
        ErrorsJson = string.Empty;
        HistoryJson = string.Empty;
        CompareJson = string.Empty;
        DiffJson = string.Empty;
        CostJson = string.Empty;
        ReplayJson = string.Empty;
        AbJson = string.Empty;
        PromptText = string.Empty;
        Provider = string.Empty;
        EndpointUrl = string.Empty;
        HttpStatusCode = 0;
        LatencyMs = 0;
        TokenUsage = string.Empty;
        MetaItems.Clear();

        try
        {
            var rawReq = await _debugService.GetRawRequestAsync(turnId);
            var rawResp = await _debugService.GetRawResponseAsync(turnId);

            if (rawReq == null && rawResp == null)
            {
                ErrorMessage = "No raw request or response was recorded for this turn.";
                return;
            }

            if (rawReq != null)
            {
                RequestJson = FormatJson(rawReq.RequestJson);
                Provider = rawReq.Provider;
                EndpointUrl = rawReq.EndpointUrl;
                MetaItems.Add(new MetaItem("Provider", rawReq.Provider));
                MetaItems.Add(new MetaItem("Endpoint", rawReq.EndpointUrl));
            }

            if (rawResp != null)
            {
                ResponseJson = FormatJson(rawResp.ResponseJson);
                HttpStatusCode = rawResp.HttpStatusCode;
                LatencyMs = rawResp.LatencyMs;
                MetaItems.Add(new MetaItem("Status", rawResp.HttpStatusCode.ToString()));
                MetaItems.Add(new MetaItem("Latency", $"{rawResp.LatencyMs}ms"));
                if (!string.IsNullOrEmpty(rawResp.TokenUsageJson))
                {
                    TokenUsage = SummarizeTokenUsage(rawResp.TokenUsageJson);
                    MetaItems.Add(new MetaItem("Tokens", TokenUsage));
                }
            }

            ContextJson = BuildContextJson(RequestJson);
            TokensJson = BuildTokensJson(rawResp?.TokenUsageJson, ResponseJson);
            TimingJson = FormatJson(JsonSerializer.Serialize(new
            {
                ActiveTurnId,
                Provider,
                EndpointUrl,
                HttpStatusCode,
                LatencyMs,
                LoadedAt = DateTime.UtcNow
            }));
            ErrorsJson = BuildErrorsJson(HttpStatusCode, ResponseJson);
            PromptText = BuildPromptText(RequestJson);

            var bundle = await _debugService.GetTurnBundleAsync(turnId);
            var history = bundle == null
                ? Array.Empty<DebugTurnSummary>()
                : await _debugService.ListTurnsAsync(bundle.ChatId);
            HistoryJson = BuildHistoryJson(history);
            CompareJson = BuildCompareJson(turnId, history, RequestJson, ResponseJson);
            DiffJson = await BuildDiffJsonAsync(turnId, history, RequestJson);
            CostJson = BuildCostJson(rawResp?.TokenUsageJson, RequestJson);
            ReplayJson = BuildReplayJson(turnId, RequestJson);
            AbJson = BuildAbJson(RequestJson, Provider, EndpointUrl);
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public string CurrentPayload() => SelectedTab switch
    {
        "response" => ResponseJson,
        "context" => ContextJson,
        "tokens" => TokensJson,
        "timing" => TimingJson,
        "errors" => ErrorsJson,
        "history" => HistoryJson,
        "compare" => CompareJson,
        "diff" => DiffJson,
        "cost" => CostJson,
        "replay" => ReplayJson,
        "a/b" => AbJson,
        "ab" => AbJson,
        _ => RequestJson
    };

    private static string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private static string SummarizeTokenUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage))
                root = usage;

            var parts = new List<string>();
            AddToken(parts, root, "input_tokens", "input");
            AddToken(parts, root, "prompt_tokens", "input");
            AddToken(parts, root, "cache_creation_input_tokens", "cache_create");
            AddToken(parts, root, "cache_read_input_tokens", "cache_read");
            AddToken(parts, root, "output_tokens", "output");
            AddToken(parts, root, "completion_tokens", "output");
            AddToken(parts, root, "total_tokens", "total");

            if (!parts.Any(p => p.StartsWith("total=", StringComparison.Ordinal)))
            {
                var input = ReadInt(root, "input_tokens") ?? ReadInt(root, "prompt_tokens");
                var output = ReadInt(root, "output_tokens") ?? ReadInt(root, "completion_tokens");
                if (input != null || output != null)
                    parts.Add($"total={(input ?? 0) + (output ?? 0)}");
            }

            if (parts.Count > 0)
                return string.Join(", ", parts.Distinct());

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        parts.Add($"{property.Name}={PrimitiveToString(property.Value)}");
                }
            }

            return parts.Count > 0
                ? string.Join(", ", parts)
                : JsonSerializer.Serialize(root, CompactJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private static void AddToken(ICollection<string> parts, JsonElement root, string key, string label)
    {
        var value = ReadInt(root, key);
        if (value != null)
            parts.Add($"{label}={value}");
    }

    private static int? ReadInt(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static string PrimitiveToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.ToString()
    };

    private string BuildCurl()
    {
        if (string.IsNullOrWhiteSpace(EndpointUrl) || string.IsNullOrWhiteSpace(RequestJson))
            return string.Empty;

        var header = string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? "-H \"x-api-key: <API_KEY>\" -H \"anthropic-version: 2023-06-01\""
            : "-H \"Authorization: Bearer <API_KEY>\"";

        return $"curl -X POST \"{EndpointUrl}\" -H \"Content-Type: application/json\" {header} --data-raw '{CompactForShell(RequestJson)}'";
    }

    private static string BuildHistoryJson(IReadOnlyList<DebugTurnSummary> history) =>
        FormatJson(JsonSerializer.Serialize(new
        {
            turns = history.Select(t => new
            {
                t.TurnNumber,
                t.TurnId,
                t.CreatedAt,
                t.Provider,
                t.HttpStatusCode,
                t.LatencyMs,
                tokens = SummarizeTokenUsage(t.TokenUsageJson),
                prompt = t.FirstPromptPreview,
                assistant = t.AssistantPreview
            })
        }, PrettyJsonOptions));

    private static string BuildCompareJson(
        Guid activeTurnId,
        IReadOnlyList<DebugTurnSummary> history,
        string requestJson,
        string responseJson)
    {
        var current = history.FirstOrDefault(t => t.TurnId == activeTurnId);
        var previous = current == null
            ? null
            : history.LastOrDefault(t => t.TurnNumber < current.TurnNumber);

        return FormatJson(JsonSerializer.Serialize(new
        {
            current = current == null ? null : new
            {
                current.TurnNumber,
                current.TurnId,
                current.Provider,
                current.HttpStatusCode,
                current.LatencyMs,
                tokens = SummarizeTokenUsage(current.TokenUsageJson),
                request = RequestStats(requestJson),
                response = ResponseStats(responseJson)
            },
            previous = previous == null ? null : new
            {
                previous.TurnNumber,
                previous.TurnId,
                previous.Provider,
                previous.HttpStatusCode,
                previous.LatencyMs,
                tokens = SummarizeTokenUsage(previous.TokenUsageJson),
                previousPrompt = previous.FirstPromptPreview,
                previousAssistant = previous.AssistantPreview
            }
        }, PrettyJsonOptions));
    }

    private async Task<string> BuildDiffJsonAsync(Guid activeTurnId, IReadOnlyList<DebugTurnSummary> history, string requestJson)
    {
        try
        {
            var current = history.FirstOrDefault(t => t.TurnId == activeTurnId);
            var previous = current == null
                ? null
                : history.LastOrDefault(t => t.TurnNumber < current.TurnNumber);
            if (previous == null)
                return FormatJson("""{"diff":"No previous turn to compare."}""");

            var previousRequest = await _debugService.GetRawRequestAsync(previous.TurnId);
            var currentPrompt = BuildPromptText(requestJson);
            var previousPrompt = BuildPromptText(previousRequest?.RequestJson ?? string.Empty);
            var diffLines = BuildLineDiff(previousPrompt, currentPrompt);
            return FormatJson(JsonSerializer.Serialize(new
            {
                previousTurn = previous.TurnNumber,
                currentTurn = current!.TurnNumber,
                diffLines
            }, PrettyJsonOptions));
        }
        catch (Exception ex)
        {
            return FormatJson(JsonSerializer.Serialize(new { error = ex.Message }, PrettyJsonOptions));
        }
    }

    private static string BuildCostJson(string? tokenUsageJson, string requestJson)
    {
        var tokens = ParseTokenUsage(tokenUsageJson);
        var model = TryReadString(requestJson, "model") ?? "unknown";
        var input = tokens.GetValueOrDefault("input")
            + tokens.GetValueOrDefault("prompt");
        var output = tokens.GetValueOrDefault("output")
            + tokens.GetValueOrDefault("completion");
        var total = tokens.GetValueOrDefault("total");
        if (total == 0)
            total = input + output;

        var estimate = EstimateCost(model, input, output);
        return FormatJson(JsonSerializer.Serialize(new
        {
            model,
            inputTokens = input,
            outputTokens = output,
            totalTokens = total,
            estimatedUsd = estimate,
            note = estimate == null
                ? "No local price table entry for this model. Token counts are still reliable."
                : "Cost is an estimate from the local price table; provider billing may differ."
        }, PrettyJsonOptions));
    }

    private static string BuildReplayJson(Guid turnId, string requestJson) =>
        FormatJson(JsonSerializer.Serialize(new
        {
            turnId,
            action = "Replay this exact recorded prompt payload with the current provider credentials.",
            sanitizedRequest = ParseObject(SecretRedactor.RedactJson(requestJson)),
            safeguards = new[]
            {
                "API keys are not exported into the replay payload.",
                "Raw request and response artifacts are redacted before storage."
            }
        }, PrettyJsonOptions));

    private static string BuildAbJson(string requestJson, string provider, string endpointUrl)
    {
        var model = TryReadString(requestJson, "model") ?? "current-model";
        return FormatJson(JsonSerializer.Serialize(new
        {
            purpose = "Model/provider A/B comparison pack",
            baseline = new
            {
                provider,
                endpointUrl,
                model,
                request = ParseObject(SecretRedactor.RedactJson(requestJson))
            },
            candidate = new
            {
                provider = "set-provider-b",
                endpointUrl = "set-endpoint-b",
                model = "set-model-b",
                request = ParseObject(SecretRedactor.RedactJson(requestJson))
            },
            compare = new[]
            {
                "latencyMs",
                "input/output/total tokens",
                "assistant answer text",
                "error payload"
            }
        }, PrettyJsonOptions));
    }

    private static string BuildContextJson(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            var payload = new Dictionary<string, object?>();
            AddJsonProperty(payload, root, "system");
            AddJsonProperty(payload, root, "messages");
            AddJsonProperty(payload, root, "model");
            AddJsonProperty(payload, root, "max_tokens");
            AddJsonProperty(payload, root, "temperature");
            return JsonSerializer.Serialize(payload, PrettyJsonOptions);
        }
        catch
        {
            return requestJson;
        }
    }

    private static string BuildTokensJson(string? tokenUsageJson, string responseJson)
    {
        if (!string.IsNullOrWhiteSpace(tokenUsageJson))
            return FormatJson(tokenUsageJson);

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
                return FormatJson(usage.GetRawText());
        }
        catch
        {
        }

        return FormatJson("""{"tokens":"No token usage recorded."}""");
    }

    private static string BuildErrorsJson(int statusCode, string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                    return FormatJson(error.GetRawText());
                if (root.TryGetProperty("_error", out var parseError))
                    return FormatJson(JsonSerializer.Serialize(new { statusCode, error = parseError.GetString() }, PrettyJsonOptions));
            }
        }
        catch
        {
        }

        return FormatJson(JsonSerializer.Serialize(new
        {
            ok = statusCode is >= 200 and < 300,
            statusCode,
            message = statusCode is >= 200 and < 300 ? "No error recorded." : "Request failed without a structured error payload."
        }, PrettyJsonOptions));
    }

    private static string BuildPromptText(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("system", out var system))
                sb.AppendLine("[system]").AppendLine(system.GetString() ?? system.GetRawText()).AppendLine();

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in messages.EnumerateArray())
                {
                    var role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "message";
                    var content = msg.TryGetProperty("content", out var contentEl) ? ReadContent(contentEl) : msg.GetRawText();
                    sb.AppendLine($"[{role}]").AppendLine(content).AppendLine();
                }
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return requestJson;
        }
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        return content.GetRawText();
    }

    private static void AddJsonProperty(Dictionary<string, object?> payload, JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            return;

        payload[name] = JsonSerializer.Deserialize<object?>(value.GetRawText());
    }

    private static object? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object?>(json);
        }
        catch
        {
            return json;
        }
    }

    private static string CompactForShell(string json) =>
        JsonSerializer.Serialize(ParseObject(json), CompactJsonOptions)
            .Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static object RequestStats(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            return new
            {
                model = TryReadString(root, "model"),
                temperature = TryReadDouble(root, "temperature"),
                maxTokens = TryReadInt(root, "max_tokens"),
                messageCount = root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
                    ? messages.GetArrayLength()
                    : 0,
                promptCharacters = BuildPromptText(requestJson).Length
            };
        }
        catch
        {
            return new { promptCharacters = requestJson.Length };
        }
    }

    private static object ResponseStats(string responseJson)
    {
        var text = ExtractAssistantText(responseJson);
        return new
        {
            assistantCharacters = text.Length,
            preview = text.Length <= 160 ? text : text[..160] + "..."
        };
    }

    private static string ExtractAssistantText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("content", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                return string.Join("\n", contentArray.EnumerateArray()
                    .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text")
                    .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrEmpty(text)));
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildLineDiff(string previous, string current)
    {
        var oldLines = previous.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var newLines = current.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<string>();
        foreach (var line in newLines)
            result.Add(oldLines.Contains(line) ? "  " + line : "+ " + line);

        var newSet = newLines.ToHashSet(StringComparer.Ordinal);
        foreach (var line in oldLines.Where(line => !newSet.Contains(line)))
            result.Add("- " + line);

        return result.Take(240).ToList();
    }

    private static Dictionary<string, int> ParseTokenUsage(string? tokenUsageJson)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tokenUsageJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(tokenUsageJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage))
                root = usage;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var value))
                    continue;

                var key = property.Name switch
                {
                    "input_tokens" => "input",
                    "prompt_tokens" => "prompt",
                    "output_tokens" => "output",
                    "completion_tokens" => "completion",
                    "total_tokens" => "total",
                    _ => property.Name
                };
                result[key] = value;
            }
        }
        catch
        {
        }

        return result;
    }

    private static double? EstimateCost(string model, int inputTokens, int outputTokens)
    {
        var key = model.ToLowerInvariant();
        (double input, double output)? price = key switch
        {
            var m when m.Contains("gpt-4o-mini", StringComparison.Ordinal) => (0.15, 0.60),
            var m when m.Contains("gpt-4o", StringComparison.Ordinal) => (2.50, 10.00),
            var m when m.Contains("deepseek", StringComparison.Ordinal) => (0.27, 1.10),
            _ => null
        };
        if (price == null)
            return null;

        return Math.Round((inputTokens / 1_000_000d * price.Value.input) +
                          (outputTokens / 1_000_000d * price.Value.output), 6);
    }

    private static string? TryReadString(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadString(doc.RootElement, name);
        }
        catch { return null; }
    }

    private static string? TryReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? TryReadDouble(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>Key-value pair for the debug panel info row.</summary>
public record MetaItem(string Key, string Value);
