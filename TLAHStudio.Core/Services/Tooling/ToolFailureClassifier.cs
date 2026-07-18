namespace TLAHStudio.Core.Services.Tooling;

public static class ToolFailureClassifier
{
    public static AgentToolResult Enrich(AgentToolResult result, long? durationMs = null)
    {
        if (result.Success)
            return result with
            {
                DurationMs = result.DurationMs ?? durationMs,
                ErrorCode = null,
                Retryable = false
            };

        var error = result.Error ?? result.Warning ?? string.Empty;
        var (code, retryable) = Classify(error, result.OutcomeUncertain);
        return result with
        {
            DurationMs = result.DurationMs ?? durationMs,
            ErrorCode = string.IsNullOrWhiteSpace(result.ErrorCode) ? code : result.ErrorCode,
            Retryable = result.Retryable || retryable
        };
    }

    public static (string Code, bool Retryable) Classify(
        string? error,
        bool outcomeUncertain = false)
    {
        if (outcomeUncertain)
            return ("unknown_outcome", false);

        var value = (error ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            return ("execution_failed", false);
        if (ContainsAny(value, "invalid tool argument", "required tool argument", "invalid json",
                "must be a json object", "not allowed by the schema", "must be one of"))
            return ("invalid_arguments", false);
        if (ContainsAny(value, "timed out", "timeout", "time limit"))
            return ("timeout", true);
        if (ContainsAny(value, "429", "rate limit", "too many requests", "throttl"))
            return ("rate_limited", true);
        if (ContainsAny(value, "503", "502", "504", "temporarily unavailable",
                "connection", "network", "dns", "could not be reached", "interrupted"))
            return ("network_transient", true);
        if (ContainsAny(value, "401", "403", "permission", "approval", "access denied",
                "blocked by safety", "not permitted", "unauthorized", "forbidden"))
            return ("permission_denied", false);
        if (ContainsAny(value, "not found", "does not exist", "no such file", "404"))
            return ("not_found", false);
        if (ContainsAny(value, "conflict", "already exists", "stale", "changed since"))
            return ("conflict", false);
        if (ContainsAny(value, "cancelled", "canceled", "operation was aborted"))
            return ("cancelled", false);
        if (ContainsAny(value, "unsupported", "not available", "unavailable"))
            return ("unsupported", false);
        return ("execution_failed", false);
    }

    public static string RecoveryGuidance(string? code, bool retryable) => code switch
    {
        "invalid_arguments" =>
            "Re-read the selected tool schema and issue one corrected call. Do not retry unchanged arguments.",
        "timeout" or "rate_limited" or "network_transient" when retryable =>
            "One retry is allowed after reducing the request or switching to a fallback source. Do not loop on the same failing call.",
        "permission_denied" =>
            "Do not attempt to bypass policy. Use the approval flow or choose an operation allowed by the active permission mode.",
        "not_found" =>
            "Verify the path, URL, resource name, or identifier with a read-only discovery tool before retrying.",
        "conflict" =>
            "Refresh the current state and construct a new operation against the latest version.",
        "unsupported" =>
            "Use tool_search or mcp_list_tools to discover an available alternative.",
        "unknown_outcome" =>
            "Do not replay the operation. Ask the user to verify external state first.",
        _ =>
            "Inspect the error and choose a materially different tool, argument set, or smaller step."
    };

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
}
