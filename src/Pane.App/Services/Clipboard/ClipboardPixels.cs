using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Pane.Services.Clipboard;

// 和系统剪贴板使用同一 WIC 解码器。按行散列直通 BGRA，不把 PNG 的元数据或压缩方式计入指纹。
internal static class ClipboardPixels
{
    const string Wic = "windowscodecs.dll";
    [DllImport("shlwapi.dll")] static extern nint SHCreateMemStream(byte[] data, uint size);
    [DllImport(Wic)] static extern int WICCreateImagingFactory_Proxy(uint version, out nint factory);
    [DllImport(Wic)] static extern int IWICImagingFactory_CreateDecoderFromStream_Proxy(nint factory, nint stream, nint vendor, uint options, out nint decoder);
    [DllImport(Wic)] static extern int IWICBitmapDecoder_GetFrame_Proxy(nint decoder, uint frame, out nint source);
    [DllImport(Wic)] static extern int WICConvertBitmapSource(in Guid format, nint source, out nint bitmap);
    [DllImport(Wic)] static extern int IWICBitmapSource_GetSize_Proxy(nint bitmap, out int width, out int height);
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Rect(int X, int Y, int Width, int Height);
    [DllImport(Wic)] static extern int IWICBitmapSource_CopyPixels_Proxy(nint bitmap, in Rect area, int stride, int size, [Out] byte[] pixels);

    internal static string? Hash(byte[] data)
    {
        nint stream = SHCreateMemStream(data, (uint)data.Length);
        nint factory = 0, decoder = 0, source = 0, bitmap = 0;
        try
        {
            Guid bgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
            if (stream == 0 || WICCreateImagingFactory_Proxy(0x0236, out factory) < 0 ||
                IWICImagingFactory_CreateDecoderFromStream_Proxy(factory, stream, 0, 0, out decoder) < 0 ||
                IWICBitmapDecoder_GetFrame_Proxy(decoder, 0, out source) < 0 ||
                WICConvertBitmapSource(bgra, source, out bitmap) < 0 ||
                IWICBitmapSource_GetSize_Proxy(bitmap, out int width, out int height) < 0 ||
                width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4) return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> dimensions = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(dimensions, width);
            BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
            hash.AppendData(dimensions);
            int stride = width * 4, rows = Math.Min(height, Math.Max(1, 65536 / stride));
            var pixels = new byte[stride * rows];
            for (int y = 0; y < height; y += rows)
            {
                var area = new Rect(0, y, width, Math.Min(rows, height - y));
                int size = stride * area.Height;
                if (IWICBitmapSource_CopyPixels_Proxy(bitmap, area, stride, size, pixels) < 0) return null;
                hash.AppendData(pixels, 0, size);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            if (bitmap != 0) Marshal.Release(bitmap);
            if (source != 0) Marshal.Release(source);
            if (decoder != 0) Marshal.Release(decoder);
            if (factory != 0) Marshal.Release(factory);
            if (stream != 0) Marshal.Release(stream);
        }
    }
}
