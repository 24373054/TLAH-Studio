using System.Net;
using System.Security.Cryptography;
using System.Text;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.11", "1.0.10", true)]
    [InlineData("1.0.10", "1.0.10", false)]
    [InlineData("1.0.9", "1.0.10", false)]
    [InlineData("1.0.11-beta", "1.0.10", true)]
    public void IsNewer_ComparesSemanticVersionsAndFallbackStrings(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(candidate, current));
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsUpdate_WhenSignedManifestIsValid()
    {
        var (publicKey, privateKey) = UpdateCrypto.GenerateKeyPair();
        const string latestJson = """
        {
          "version": "1.0.11",
          "channel": "stable",
          "installerUrl": "https://updates.example/TLAHStudioSetup-1.0.11.exe",
          "sha256": "abc123",
          "releaseNotes": "Release quality update.",
          "forceUpdate": true,
          "minSupportedVersion": "1.0.11",
          "rolloutPercent": 100,
          "installerSizeBytes": 12345
        }
        """;
        var signature = UpdateCrypto.SignData(latestJson, privateKey);
        using var client = new HttpClient(new MapHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) }
                : MapHttpMessageHandler.Json(HttpStatusCode.OK, latestJson);
        }));
        var service = new UpdateService(
            new StaticHttpClientFactory(client),
            Path.Combine(Path.GetTempPath(), "tlah-test-install"),
            "1.0.10",
            "https://updates.example/latest.json",
            "test-install-id",
            publicKey);

        var result = await service.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.Equal("1.0.11", result.Version);
        Assert.True(result.ForceUpdate);
        Assert.Equal(12345, result.InstallerSizeBytes);
        Assert.Equal("abc123", result.Sha256);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenSignedManifestIsInvalid()
    {
        var (publicKey, _) = UpdateCrypto.GenerateKeyPair();
        using var client = new HttpClient(new MapHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Convert.ToBase64String([1, 2, 3])) }
                : MapHttpMessageHandler.Json(HttpStatusCode.OK, """{"version":"1.0.11","installerUrl":"https://updates.example/setup.exe"}""");
        }));
        var service = new UpdateService(
            new StaticHttpClientFactory(client),
            Path.Combine(Path.GetTempPath(), "tlah-test-install"),
            "1.0.10",
            "https://updates.example/latest.json",
            "test-install-id",
            publicKey);

        var result = await service.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadInstallerAsync_VerifiesSha256BeforeReturningPath()
    {
        var payload = Encoding.UTF8.GetBytes("installer-bytes");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var client = new HttpClient(new MapHttpMessageHandler(_ => MapHttpMessageHandler.Bytes(HttpStatusCode.OK, payload)));
        var service = new UpdateService(
            new StaticHttpClientFactory(client),
            Path.Combine(Path.GetTempPath(), "tlah-test-install"),
            "1.0.10",
            "https://updates.example/latest.json",
            "test-install-id",
            "REPLACE_WITH_YOUR_PUBLIC_KEY");
        var info = new UpdateCheckResult(
            Version: $"test-{Guid.NewGuid():N}",
            Channel: "stable",
            InstallerUrl: "https://updates.example/setup.exe",
            InstallerSizeBytes: payload.Length,
            Sha256: sha256,
            ReleaseNotes: null,
            ForceUpdate: false,
            MinSupportedVersion: null);

        var path = await service.DownloadInstallerAsync(info);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public async Task DownloadInstallerAsync_ReturnsNullAndDeletesFile_WhenSha256DoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("installer-bytes");
        using var client = new HttpClient(new MapHttpMessageHandler(_ => MapHttpMessageHandler.Bytes(HttpStatusCode.OK, payload)));
        var service = new UpdateService(
            new StaticHttpClientFactory(client),
            Path.Combine(Path.GetTempPath(), "tlah-test-install"),
            "1.0.10",
            "https://updates.example/latest.json",
            "test-install-id",
            "REPLACE_WITH_YOUR_PUBLIC_KEY");
        var version = $"test-{Guid.NewGuid():N}";
        var info = new UpdateCheckResult(
            Version: version,
            Channel: "stable",
            InstallerUrl: "https://updates.example/setup.exe",
            InstallerSizeBytes: payload.Length,
            Sha256: new string('0', 64),
            ReleaseNotes: null,
            ForceUpdate: false,
            MinSupportedVersion: null);

        var path = await service.DownloadInstallerAsync(info);
        var expectedPath = Path.Combine(Path.GetTempPath(), $"TLAHStudioSetup-{version}.exe");

        Assert.Null(path);
        Assert.False(File.Exists(expectedPath));
    }
}
