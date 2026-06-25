using System.Net;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class UpdateCryptoTests
{
    [Fact]
    public void SignData_ProducesSignatureThatVerifies()
    {
        var (publicKey, privateKey) = UpdateCrypto.GenerateKeyPair();
        const string json = """{"version":"1.0.11","sha256":"abc"}""";

        var signature = UpdateCrypto.SignData(json, privateKey);

        Assert.True(UpdateCrypto.VerifySignature(json, signature, publicKey));
        Assert.False(UpdateCrypto.VerifySignature(json + "tampered", signature, publicKey));
    }

    [Fact]
    public async Task VerifyLatestJsonAsync_FetchesSigAndVerifiesManifest()
    {
        var (publicKey, privateKey) = UpdateCrypto.GenerateKeyPair();
        const string json = """{"version":"1.0.11","sha256":"abc"}""";
        var signature = UpdateCrypto.SignData(json, privateKey);
        var handler = new MapHttpMessageHandler(request =>
        {
            Assert.Equal("https://updates.example/latest.json.sig", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(signature)
            };
        });
        using var client = new HttpClient(handler);

        var valid = await UpdateCrypto.VerifyLatestJsonAsync(
            client,
            "https://updates.example/latest.json",
            json,
            publicKey,
            CancellationToken.None);

        Assert.True(valid);
    }

    [Fact]
    public async Task VerifyLatestJsonAsync_ReturnsFalse_WhenSignatureIsUnavailable()
    {
        using var client = new HttpClient(new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var valid = await UpdateCrypto.VerifyLatestJsonAsync(
            client,
            "https://updates.example/latest.json",
            "{}",
            UpdateCrypto.PublicKeyBase64,
            CancellationToken.None);

        Assert.False(valid);
    }
}
