using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Pane.Services.Cloud;

internal static class SyncCrypto
{
    internal static string Id(byte[] key, string category, string naturalKey) =>
        CloudKeys.Encode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"pane-id-v1\0{category}\0{naturalKey}"))[..16]);

    internal static byte[] Seal(byte[] key, string aad, byte[] plaintext, byte[]? nonce = null)
    {
        var packet = new byte[29 + plaintext.Length];
        packet[0] = 1;
        (nonce ?? RandomNumberGenerator.GetBytes(12)).CopyTo(packet, 1);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(packet.AsSpan(1,12), plaintext, packet.AsSpan(13,plaintext.Length), packet.AsSpan(13+plaintext.Length), Encoding.UTF8.GetBytes(aad));
        return packet;
    }
    static byte[] Open(byte[] key, string aad, byte[] packet)
    {
        if (packet.Length < 29 || packet[0] != 1) throw new CryptographicException();
        var bytes = new byte[packet.Length - 29];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(packet.AsSpan(1,12), packet.AsSpan(13,bytes.Length), packet.AsSpan(13+bytes.Length), bytes, Encoding.UTF8.GetBytes(aad));
        return bytes;
    }
    internal static byte[] EncryptBlob(byte[] key, string id, byte[] bytes, byte[]? nonce = null) =>
        Seal(key, "pane-blob-v1\0" + id, bytes, nonce);
    internal static byte[] DecryptBlob(byte[] key, string id, byte[] packet) => Open(key, "pane-blob-v1\0" + id, packet);
    internal static string Encrypt(byte[] key, string c, string id, string json)
    {
        using var buffer = new MemoryStream();
        using (var compressor = new DeflateStream(buffer, CompressionLevel.Fastest, true))
            compressor.Write(Encoding.UTF8.GetBytes(json));
        return CloudKeys.Encode(Seal(key, $"pane-rec-v1\0{c}\0{id}", buffer.ToArray()));
    }
    internal static string Decrypt(byte[] key, string c, string id, string data)
    {
        using var buffer = new MemoryStream(Open(key, $"pane-rec-v1\0{c}\0{id}", CloudKeys.Decode(data)));
        using var stream = new DeflateStream(buffer, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
