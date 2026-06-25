using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace TLAHStudio.Core.Helpers;

[SupportedOSPlatform("windows")]
public static class ProtectedSecret
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TLAHStudio.ApiKey.v1");

    public static bool IsProtected(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (IsProtected(value))
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Reveal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (!IsProtected(value))
            return value;

        try
        {
            var bytes = Convert.FromBase64String(value[Prefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
