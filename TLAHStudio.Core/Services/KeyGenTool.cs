using System.Security.Cryptography;
using System.Text;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Standalone key generation and signing tool.
/// Run: dotnet run --project TLAHStudio.Core -- --keygen
/// Run: dotnet run --project TLAHStudio.Core -- --sign latest.json privateKey
/// </summary>
public static class KeyGenMain
{
    public static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--keygen" || args[0] == "keygen")
        {
            GenerateKeys();
        }
        else if (args[0] == "--sign" || args[0] == "sign")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: --sign <latest.json path> <privateKeyBase64>");
                return;
            }
            SignFile(args[1], args[2]);
        }
        else if (args[0] == "--verify" || args[0] == "verify")
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: --verify <latest.json path> <signature> <publicKeyBase64>");
                return;
            }
            VerifyFile(args[1], args[2], args[3]);
        }
        else
        {
            Console.Error.WriteLine("Usage: --keygen | --sign <file> <privKey> | --verify <file> <sig> <pubKey>");
        }
    }

    private static void GenerateKeys()
    {
        var (pub, priv) = UpdateCrypto.GenerateKeyPair();
        Console.WriteLine("=== PUBLIC KEY ===");
        Console.WriteLine("Embed this in TLAHStudio.Core/Services/UpdateCrypto.cs as PublicKeyBase64:");
        Console.WriteLine();
        Console.WriteLine(pub);
        Console.WriteLine();
        Console.WriteLine("=== PRIVATE KEY ===");
        Console.WriteLine("Keep this SECRET. Use it to sign latest.json on the server:");
        Console.WriteLine();
        Console.WriteLine(priv);
    }

    private static void SignFile(string filePath, string privateKeyBase64)
    {
        var data = File.ReadAllText(filePath);
        var signature = UpdateCrypto.SignData(data, privateKeyBase64);

        // Write .sig file next to the original
        var sigPath = filePath + ".sig";
        File.WriteAllText(sigPath, signature);

        Console.WriteLine($"Signed: {filePath}");
        Console.WriteLine($"Signature: {sigPath}");
        Console.WriteLine($"Signature (Base64): {signature}");
    }

    private static void VerifyFile(string filePath, string signatureBase64, string publicKeyBase64)
    {
        var data = File.ReadAllText(filePath);
        var valid = UpdateCrypto.VerifySignature(data, signatureBase64, publicKeyBase64);
        Console.WriteLine(valid ? "VERIFIED: Signature is valid." : "INVALID: Signature does NOT match.");
    }
}
