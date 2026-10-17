using System.Security.Cryptography;
using System.Text;
using SteamForge.Core.Auth;

namespace SteamForge.Probe;

/// <summary>
/// Proves or disproves the RSA padding/encryption by round-tripping it. A real keypair is
/// generated locally, the password is encrypted with the production code path, then decrypted
/// with the private key. If the PKCS#1 v1.5 block is built or exponentiated wrongly, the
/// decrypted bytes will not match the expected 00 02 [pad] 00 [message] layout — and every
/// Steam probe would return eresult 5 no matter how correct the password is.
/// </summary>
internal static class Program5
{
    public static void Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);

        var modHex = ToHex(p.Modulus!);
        var expHex = ToHex(p.Exponent!);

        Console.WriteLine($"local keypair: {p.Modulus!.Length * 8}-bit, exp={expHex}");
        Console.WriteLine();

        const string password = "Wcwwgrhhehlach1";
        var b64 = SteamRsa.EncryptPassword(password, modHex, expHex);
        var cipher = Convert.FromBase64String(b64);

        Console.WriteLine($"password      : {password}");
        Console.WriteLine($"ciphertext    : {cipher.Length} bytes (modulus is {p.Modulus.Length})");
        Console.WriteLine($"length match  : {cipher.Length == p.Modulus.Length}");
        Console.WriteLine();

        // RSA.Decrypt with Pkcs1 does not return the raw encoded message — it validates the
        // 00 02 [pad] 00 layout, strips it, and throws CryptographicException if malformed.
        // So the proof is: no throw, and the recovered plaintext equals the password.
        byte[] recovered;
        try
        {
            recovered = rsa.Decrypt(cipher, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            Console.WriteLine($"DECRYPT FAILED: {ex.Message}");
            Console.WriteLine("The produced block is not valid PKCS#1 v1.5 — encryption is broken.");
            return;
        }

        var recoveredText = Encoding.UTF8.GetString(recovered);
        Console.WriteLine($"recovered     : '{recoveredText}'  ({recovered.Length} bytes)");
        Console.WriteLine($"round-trips   : {recoveredText == password}");
        Console.WriteLine("(.NET's Pkcs1 decrypt already rejected the block if padding were malformed)");
        Console.WriteLine();

        bool ok = recoveredText == password && recovered.Length == password.Length;
        Console.WriteLine(ok
            ? "RESULT: PKCS#1 v1.5 encryption is correct. A Steam eresult 5 is a real credential verdict."
            : "RESULT: encryption is BROKEN — Steam would reject every password.");

        Console.WriteLine();
        Console.WriteLine("second encrypt of the same password (padding must differ):");
        var b64b = SteamRsa.EncryptPassword(password, modHex, expHex);
        Console.WriteLine($"  distinct ciphertext: {b64 != b64b}");

        Console.WriteLine();
        Console.WriteLine("edge: empty password and max-length password");
        try
        {
            var empty = SteamRsa.EncryptPassword("", modHex, expHex);
            var e2 = rsa.Decrypt(Convert.FromBase64String(empty), RSAEncryptionPadding.Pkcs1);
            Console.WriteLine($"  empty password round-trips: {Encoding.UTF8.GetString(e2)} == '' -> {e2.Length == 0}");
        }
        catch (Exception ex) { Console.WriteLine($"  empty password FAILED: {ex.Message}"); }

        try
        {
            var maxLen = new string('x', p.Modulus.Length - 11);
            var max = SteamRsa.EncryptPassword(maxLen, modHex, expHex);
            var e3 = rsa.Decrypt(Convert.FromBase64String(max), RSAEncryptionPadding.Pkcs1);
            Console.WriteLine($"  max-length ({maxLen.Length} chars) round-trips: {Encoding.UTF8.GetString(e3) == maxLen}");
        }
        catch (Exception ex) { Console.WriteLine($"  max-length FAILED: {ex.Message}"); }

        try
        {
            var over = new string('x', p.Modulus.Length - 10);
            SteamRsa.EncryptPassword(over, modHex, expHex);
            Console.WriteLine("  over-length accepted — should have thrown");
        }
        catch (ArgumentException)
        {
            Console.WriteLine("  over-length rejected: OK");
        }
    }

    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
