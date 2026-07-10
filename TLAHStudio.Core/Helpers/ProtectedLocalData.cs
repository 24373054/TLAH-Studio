namespace TLAHStudio.Core.Helpers;

/// <summary>
/// Protects resumable local runtime state with Windows DPAPI while retaining a
/// plain-text fallback for non-Windows test hosts.
/// </summary>
public static class ProtectedLocalData
{
    public static string Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return OperatingSystem.IsWindows()
            ? ProtectedSecret.Protect(value)
            : value;
    }

    public static string Reveal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            return value;
        if (!ProtectedSecret.IsProtected(value))
            return value;

        return ProtectedSecret.Reveal(value);
    }
}
