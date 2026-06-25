using TLAHStudio.Core.Helpers;

namespace TLAHStudio.Core.Tests;

public class ApiKeyMaskerTests
{
    [Fact]
    public void Mask_ReturnsEmpty_ForEmptyKey()
    {
        Assert.Equal(string.Empty, ApiKeyMasker.Mask(string.Empty));
    }

    [Fact]
    public void Mask_FullyMasksShortKeys()
    {
        Assert.Equal("********", ApiKeyMasker.Mask("12345678"));
    }

    [Fact]
    public void Mask_KeepsFirstAndLastFourCharacters()
    {
        Assert.Equal("sk-1***********cdef", ApiKeyMasker.Mask("sk-1234567890abcdef"));
    }

    [Theory]
    [InlineData("sk-1********cdef", true)]
    [InlineData("sk-1234567890abcdef", false)]
    [InlineData("", false)]
    public void IsMasked_DetectsMaskedValues(string value, bool expected)
    {
        Assert.Equal(expected, ApiKeyMasker.IsMasked(value));
    }
}
