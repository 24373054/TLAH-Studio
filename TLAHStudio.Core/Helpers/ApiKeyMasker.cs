namespace TLAHStudio.Core.Helpers;

/// <summary>
/// API key masking utilities.
/// Maps 1:1 from _mask_api_key() and _is_masked() in services/settings_service.py.
/// </summary>
public static class ApiKeyMasker
{
    /// <summary>
    /// Mask an API key for display. Shows first 4 and last 4 characters,
    /// with asterisks in between. Short keys are fully masked.
    /// Maps from settings_service.py lines 9-14.
    /// </summary>
    public static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        if (key.Length <= 8)
            return new string('*', key.Length);

        return key[..4] + new string('*', key.Length - 8) + key[^4..];
    }

    /// <summary>
    /// Check if a value looks like a masked API key.
    /// Maps from settings_service.py lines 47-48.
    /// </summary>
    public static bool IsMasked(string value) =>
        !string.IsNullOrEmpty(value) && value.Contains('*');
}
