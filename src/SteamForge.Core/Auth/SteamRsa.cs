using System.Security.Cryptography;

namespace SteamForge.Core.Auth;

/// <summary>
/// Steam password encryption: RSAES-PKCS1-v1_5 over the per-account modulus Steam returns
/// from GetPasswordRSAPublicKey. Steam's key is fetched fresh per account, so the encrypted
/// blob and its padding are unique per attempt — there is no static key to pin.
/// BCL only; no crypto package needed.
/// </summary>
public static class SteamRsa
{
    public static string EncryptPassword(string password, string modulusHex, string exponentHex)
    {
        var n = FromHex(modulusHex);
        var e = FromHex(exponentHex);

        int keyBytes = (int)((n.GetBitLength() + 7) / 8);
        byte[] message = System.Text.Encoding.UTF8.GetBytes(NormalizePassword(password));

        if (message.Length > keyBytes - 11)
            throw new ArgumentException($"Password too long for {keyBytes}-byte Steam modulus.");

        // PKCS#1 v1.5 type 2 padding: 0x00 0x02 [non-zero random pad] 0x00 [message]
        byte[] em = new byte[keyBytes];
        em[0] = 0x00;
        em[1] = 0x02;

        int padLen = keyBytes - message.Length - 3;
        byte[] pad = RandomNumberGenerator.GetBytes(padLen);
        for (int i = 0; i < pad.Length; i++)
        {
            // Zero bytes are not allowed in the pad block; reroll until non-zero.
            while (pad[i] == 0) pad[i] = (byte)RandomNumberGenerator.GetInt32(1, 256);
        }
        Buffer.BlockCopy(pad, 0, em, 2, padLen);
        em[2 + padLen] = 0x00;
        Buffer.BlockCopy(message, 0, em, 3 + padLen, message.Length);

        var m = new System.Numerics.BigInteger(em, isUnsigned: true, isBigEndian: true);
        var c = System.Numerics.BigInteger.ModPow(m, e, n);

        byte[] cipher = c.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (cipher.Length < keyBytes)
        {
            var padded = new byte[keyBytes];
            Buffer.BlockCopy(cipher, 0, padded, keyBytes - cipher.Length, cipher.Length);
            cipher = padded;
        }

        return Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// Steam's web login path historically stripped non-ASCII; the credentials flow tolerates
    /// it too and several public checkers do the same. Keeps unicode passwords from blowing
    /// the modulus length check.
    /// </summary>
    private static string NormalizePassword(string password)
    {
        var sb = new System.Text.StringBuilder(password.Length);
        foreach (char ch in password)
            if (ch <= 0x7F) sb.Append(ch);
        return sb.ToString();
    }

    private static System.Numerics.BigInteger FromHex(string hex)
    {
        hex = hex.Trim();
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }
}
