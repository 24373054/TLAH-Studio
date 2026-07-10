using System.Security.Cryptography;
using System.Text;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Cryptographic utilities for update security.
/// Supports ECDSA P-256 signature verification of latest.json.
/// </summary>
public static class UpdateCrypto
{
    /// <summary>
    /// The embedded public key used to verify latest.json signatures.
    /// This is the ONLY key that can sign valid update manifests.
    /// Generated once and baked into the application at build time.
    ///
    /// TO REPLACE: Generate a new key pair with GenerateKeyPair(),
    /// save the private key securely (NEVER commit it), and update
    /// this constant with the new public key (Base64).
    /// </summary>
    public const string PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyAQ+B5T5Vw1+QcH0Ekcdk/9HG/+joSsb/cW8QIpLDVw5oKO7EqJfXPg0gXEDP5bZfrzQ49s5jo7CMVfxDD62lA==";

    /// <summary>
    /// Generate a new ECDSA P-256 key pair for signing latest.json.
    /// Run this ONCE, save the private key securely, and embed the public key.
    /// </summary>
    public static (string publicKey, string privateKey) GenerateKeyPair()
    {
        // .NET 8 doesn't have built-in Ed25519, so we use ECDsa with P-256
        // which provides equivalent security for update signing.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var privateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        return (publicKey, privateKey);
    }

    /// <summary>
    /// Sign a message using the private key. Returns Base64 signature.
    /// Used server-side to sign latest.json.
    /// </summary>
    public static string SignData(string data, string privateKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var signature = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verify a signature against data using the embedded public key.
    /// Used client-side to verify latest.json authenticity.
    /// </summary>
    public static bool VerifySignature(string data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = Convert.FromBase64String(signatureBase64);
            return ecdsa.VerifyData(dataBytes, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verify latest.json content against its .sig file.
    /// Fetches the .sig file from the URL and verifies.
    /// </summary>
    public static async Task<bool> VerifyLatestJsonAsync(
        HttpClient client,
        string jsonUrl,
        string jsonContent,
        string publicKeyBase64,
        CancellationToken ct,
        string? signatureUrl = null)
    {
        try
        {
            var sigUrl = string.IsNullOrWhiteSpace(signatureUrl)
                ? jsonUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? jsonUrl + ".sig"
                : jsonUrl + "/latest.json.sig"
                : signatureUrl;

            var sigResponse = await client.GetAsync(sigUrl, ct);
            if (!sigResponse.IsSuccessStatusCode)
                return false;

            var signature = await sigResponse.Content.ReadAsStringAsync(ct);
            return VerifySignature(jsonContent, signature.Trim(), publicKeyBase64);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Tool to generate a new key pair. Compile and run this separately:
///   dotnet run --project TLAHStudio.Core
/// Or call UpdateCrypto.GenerateKeyPair() from a script.
/// </summary>
public static class KeyGenTool
{
    public static void Main()
    {
        var (pub, priv) = UpdateCrypto.GenerateKeyPair();
        Console.WriteLine("=== PUBLIC KEY (embed in TLAHStudio.Core/Services/UpdateCrypto.cs) ===");
        Console.WriteLine(pub);
        Console.WriteLine();
        Console.WriteLine("=== PRIVATE KEY (keep secret! Use to sign latest.json) ===");
        Console.WriteLine(priv);
        Console.WriteLine();
        Console.WriteLine("Store the private key securely. It is needed to sign each latest.json.");
        Console.WriteLine("The public key is embedded in the app for verification.");
    }
}
