namespace TLAHStudio.Core.Llm;

public sealed record ProviderModelResolution(
    string DisplayModel,
    string WireModel,
    bool LongContextEnabled,
    int ContextBudgetTokens,
    int AutoCompactTriggerTokens);

public static class ProviderModelResolver
{
    private const string DeepSeekLongContextSuffix = "[1m]";

    public static ProviderModelResolution Resolve(
        string provider,
        string? baseUrl,
        string? model,
        bool? longContextOverride = null)
    {
        var displayModel = NormalizeModelForStorage(model);
        var hadSuffix = HasLongContextSuffix(model);
        var longContext = longContextOverride ?? hadSuffix;
        var wireModel = ToWireModel(provider, baseUrl, displayModel);

        var budget = longContext ? 900_000 : 32_000;
        var trigger = longContext ? 720_000 : 24_000;
        return new ProviderModelResolution(displayModel, wireModel, longContext, budget, trigger);
    }

    public static string NormalizeModelForStorage(string? model)
    {
        var value = (model ?? string.Empty).Trim();
        return HasLongContextSuffix(value)
            ? value[..^DeepSeekLongContextSuffix.Length]
            : value;
    }

    public static string ToWireModel(string provider, string? baseUrl, string? model)
    {
        var value = NormalizeModelForStorage(model);
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var providerKey = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (providerKey == "anthropic" &&
            (baseUrl ?? string.Empty).Contains("api.deepseek.com/anthropic", StringComparison.OrdinalIgnoreCase) &&
            HasLongContextSuffix(model))
            return (model ?? value).Trim();

        return value;
    }

    public static bool HasLongContextSuffix(string? model) =>
        (model ?? string.Empty).Trim().EndsWith(DeepSeekLongContextSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeepSeekModel(string? model) =>
        (model ?? string.Empty).Trim().StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase);

    public static bool IsDeepSeekV4Model(string? model)
    {
        var normalized = NormalizeModelForStorage(model);
        return string.Equals(normalized, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase);
    }
}
