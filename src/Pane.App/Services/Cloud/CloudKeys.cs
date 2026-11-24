using System.Security.Cryptography;
using System.Text;

namespace Pane.Services.Cloud;

internal static class CloudKeys
{
    internal const int Iterations = 600000;
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    internal static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-','+').Replace('_','/').PadRight((value.Length + 3) / 4 * 4, '='));
    internal static byte[] Master(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    internal static byte[] Expand(byte[] key, string context) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: Encoding.ASCII.GetBytes(context));
    internal static string Check(byte[] key) => Encode(HMACSHA256.HashData(key, "pane-key-check-v1"u8));
    internal static string Wrap(byte[] wrapping, byte[] key, bool recovery = false, byte[]? nonce = null)
    {
        byte[] packet = new byte[60];
        (nonce ?? RandomNumberGenerator.GetBytes(12)).CopyTo(packet, 0);
        using var aes = new AesGcm(wrapping, 16);
        aes.Encrypt(packet.AsSpan(0,12), key, packet.AsSpan(12,32), packet.AsSpan(44), Aad(recovery));
        return Encode(packet);
    }
    static ReadOnlySpan<byte> Aad(bool recovery) => recovery ? "pane-data-key-recovery-v1"u8 : "pane-data-key-v1"u8;
    internal static byte[] Unwrap(byte[] wrapping, string wrapped, bool recovery = false)
    {
        byte[] packet = Decode(wrapped), key = new byte[32];
        if (packet.Length != 60) throw new CryptographicException();
        using var aes = new AesGcm(wrapping, 16);
        aes.Decrypt(packet.AsSpan(0,12), packet.AsSpan(12,32), packet.AsSpan(44), key, Aad(recovery));
        return key;
    }
    internal static string Display(byte[] code)
    {
        var result = new StringBuilder(39);
        for (int i = 0; i < 32; i++)
        {
            if (i > 0 && i % 4 == 0) result.Append('-');
            int value = 0;
            for (int j = i * 5; j < i * 5 + 5; j++)
                value = (value << 1) | ((code[j / 8] >> (7 - j % 8)) & 1);
            result.Append(Alphabet[value]);
        }
        return result.ToString();
    }
    internal static byte[]? Parse(string? display)
    {
        byte[] bytes = new byte[20];
        int bits = 0;
        foreach (char c in display ?? "")
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            int value = Alphabet.IndexOf(char.ToUpperInvariant(c) switch { 'I' or 'L' => '1', 'O' => '0', var x => x });
            if (value < 0 || bits >= 160) return null;
            for (int shift = 4; shift >= 0; shift--, bits++)
                bytes[bits / 8] |= (byte)(((value >> shift) & 1) << (7 - bits % 8));
        }
        return bits == 160 ? bytes : null;
    }
}

