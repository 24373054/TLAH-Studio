using TLAHStudio.Core.Helpers;

namespace TLAHStudio.Core.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void RedactJson_RemovesKnownSecretsAndSensitiveFields()
    {
        const string secret = "sk-testsecretvalue123456";
        var json = """
        {
          "api_key": "sk-testsecretvalue123456",
          "messages": [
            { "role": "user", "content": "Bearer abcdefghijklmnopqrstuvwxyz" }
          ],
          "nested": { "authorization": "Bearer abcdefghijklmnopqrstuvwxyz" }
        }
        """;

        var redacted = SecretRedactor.RedactJson(json, secret);

        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", redacted);
        Assert.Contains(SecretRedactor.Redacted, redacted);
    }

    [Fact]
    public void ContainsSecret_DetectsKnownSecretAndBearerToken()
    {
        Assert.True(SecretRedactor.ContainsSecret("Authorization: Bearer abcdefghijklmnopqrstuvwxyz"));
        Assert.True(SecretRedactor.ContainsSecret("value sk-testsecretvalue123456"));
        Assert.False(SecretRedactor.ContainsSecret("ordinary prompt text"));
    }
}
