// M4.9.4: Standalone signer — PowerShell 7's .NET method binder fails to
// resolve ECDsa.ImportPkcs8PrivateKey (same class of bug as SHA256.HashData).
// Compiling this via csc/dotnet-build and invoking the exe bypasses the binder.
using System;
using System.IO;
using System.Security.Cryptography;

class SignLatest
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: sign-latest <latest.json> <private_key.txt>"); return 1; }
        string jsonPath = args[0], keyPath = args[1];
        if (!File.Exists(jsonPath)) { Console.Error.WriteLine("json not found: " + jsonPath); return 1; }
        if (!File.Exists(keyPath)) { Console.Error.WriteLine("key not found: " + keyPath); return 1; }

        byte[] data = File.ReadAllBytes(jsonPath);
        string keyB64 = File.ReadAllText(keyPath).Trim();
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(keyB64), out _);
        byte[] sig = ecdsa.SignData(data, HashAlgorithmName.SHA256);
        File.WriteAllText(jsonPath + ".sig", Convert.ToBase64String(sig));
        Console.WriteLine("Signed: " + jsonPath);
        Console.WriteLine("Signature: " + jsonPath + ".sig");
        return 0;
    }
}
