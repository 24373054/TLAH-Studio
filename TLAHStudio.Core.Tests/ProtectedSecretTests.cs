using TLAHStudio.Core.Helpers;

namespace TLAHStudio.Core.Tests;

public class ProtectedSecretTests
{
    [Fact]
    public void ProtectAndReveal_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var protectedValue = ProtectedSecret.Protect("sk-test-secret");

        Assert.True(ProtectedSecret.IsProtected(protectedValue));
        Assert.Equal("sk-test-secret", ProtectedSecret.Reveal(protectedValue));
    }

    [Fact]
    public void Protect_DoesNotDoubleProtectExistingValue_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var protectedValue = ProtectedSecret.Protect("sk-test-secret");

        Assert.Equal(protectedValue, ProtectedSecret.Protect(protectedValue));
    }

    [Fact]
    public void Reveal_ReturnsEmpty_ForInvalidProtectedPayload_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(string.Empty, ProtectedSecret.Reveal("dpapi:v1:not-base64"));
    }
}
