using System.Formats.Nrbf;
using System.IO.Compression;
using System.Runtime.Serialization;

namespace Pane.Gestures;

// 只解码 NRBF 记录，不反序列化类型、不使用归档中的路径写文件。
internal static class WGesturesArchive
{
    const int MaxBlockSize = 32 * 1024 * 1024;

    internal static (byte[] Gestures, byte[]? Config, string Name) Read(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        byte[]? gestures = null, config = null;
        string? gestureName = null;
        try
        {
            while (file.Position < file.Length)
            {
                int indexLength = reader.ReadUInt16();
                using var indexStream = new MemoryStream(ReadBytes(reader, indexLength));
                var index = Decode(indexStream, "NativeMultiFileArchiveLib.RawIndex");
                string name = index.GetString("N") ?? throw new InvalidDataException();
                int length = index.GetInt32("L");
                if (length <= 0 || length > file.Length - file.Position || length > MaxBlockSize)
                    throw new InvalidDataException();
                // 原作保存完整 Windows 路径；这里只用末尾文件名识别需要的条目。
                string shortName = name.Replace('\\', '/').Split('/').Last().ToLowerInvariant();
                if (shortName is not ("config.plist" or "gestures.wg2" or "gestures.wg" or "gestures.json"))
                {
                    file.Position += length;
                    continue;
                }
                using var compressed = new MemoryStream(ReadBytes(reader, length));
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                using var payload = new MemoryStream();
                byte[] buffer = new byte[8192];
                int count;
                while ((count = deflate.Read(buffer)) != 0)
                {
                    if (payload.Length + count > MaxBlockSize) throw new InvalidDataException();
                    payload.Write(buffer, 0, count);
                }
                payload.Position = 0;
                var record = Decode(payload, "NativeMultiFileArchiveLib.ArchiveFile");
                var array = record.GetArrayRecord("<FileData>k__BackingField");
                if (array is not SZArrayRecord<byte> bytes || bytes.Lengths[0] > MaxBlockSize)
                    throw new InvalidDataException();
                byte[] data = bytes.GetArray();
                if (record.GetInt32("<OriginalFileLength>k__BackingField") != data.Length)
                    throw new InvalidDataException();
                if (shortName == "config.plist")
                {
                    if (config is not null) throw new InvalidDataException();
                    config = data;
                }
                else
                {
                    if (gestures is not null) throw new InvalidDataException();
                    gestures = data;
                    gestureName = name;
                }
            }
            if (gestures is null) throw new InvalidDataException();
            return (gestures, config, $"{path} / {gestureName}");
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or SerializationException
            or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException($"不是 WGestures 导出的文件：{path}", e);
        }
    }

    static ClassRecord Decode(MemoryStream stream, string type)
    {
        if (NrbfDecoder.Decode(stream, leaveOpen: true) is not ClassRecord record || record.TypeName.FullName != type
            || stream.Position != stream.Length) throw new InvalidDataException();
        return record;
    }

    static byte[] ReadBytes(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
