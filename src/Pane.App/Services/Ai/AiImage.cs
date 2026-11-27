using Pane.Contracts;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Pane.Services.Ai;

internal sealed record AiImage(string MediaType, byte[] Bytes)
{
    internal const int MaxBytes = 4 * 1024 * 1024;

    internal static async Task<Dictionary<string, AiImage>?> Prepare(IReadOnlyList<AiChatMessage> messages, CancellationToken ct)
    {
        if (!messages.Any(m => m.Images is { Count: > 0 })) return null;
        return await Task.Run(async () =>
        {
            var images = new Dictionary<string, AiImage>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages)
            {
                if (message.Images is not { Count: > 0 } paths) continue;
                if (message.Role != AiChatRole.User) throw new AiUnsupportedInputException("只有用户消息可以附带图片");
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                        throw new AiUnsupportedInputException("无法读取图片：需要本机图片的完整路径");
                    if (!images.ContainsKey(path)) images.Add(path, await Read(path, ct));
                }
            }
            return images;
        }, ct);
    }

    internal static async Task<AiImage> Read(string path, CancellationToken ct)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var source = file.AsRandomAccessStream();
            BitmapDecoder decoder;
            try { decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct); }
            catch (Exception e) when (e.HResult == unchecked((int)0x88982F8B))
            {
                file.Position = 0;
                var header = new byte[12];
                int length = await file.ReadAtLeastAsync(header, header.Length, false, ct);
                bool webp = length == 12 && header.AsSpan(0,4).SequenceEqual("RIFF"u8) &&
                    header.AsSpan(8,4).SequenceEqual("WEBP"u8);
                if (!webp) throw new AiUnsupportedInputException("无法读取图片：无法识别图片格式", e);
                source.Seek(0);
                try { decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, source).AsTask(ct); }
                catch (Exception missing) when (missing.HResult == unchecked((int)0x88982F8B) ||
                    missing.HResult == unchecked((int)0x80040154))
                { throw new AiUnsupportedInputException("无法读取图片：Windows 未找到可用的 WebP 解码器", missing); }
            }
            var codec = decoder.DecoderInformation.CodecId;
            bool png = codec == BitmapDecoder.PngDecoderId, jpeg = codec == BitmapDecoder.JpegDecoderId;
            if (!png && !jpeg && codec != BitmapDecoder.GifDecoderId && codec != BitmapDecoder.BmpDecoderId &&
                codec != BitmapDecoder.WebpDecoderId)
                throw new AiUnsupportedInputException("无法读取图片：不支持这种图片格式");

            // 解码器本身代表第一帧。先按目标尺寸解码，也验证直传图片的像素可读。
            uint longest = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            uint edge = Math.Min(longest, 2048u);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint Scale(uint side) => Math.Max(1u, (uint)Math.Round((double)side * edge / longest));
                var transform = new BitmapTransform { ScaledWidth = Scale(decoder.PixelWidth),
                    ScaledHeight = Scale(decoder.PixelHeight), InterpolationMode = BitmapInterpolationMode.Fant };
                var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
                var pixels = provider.DetachPixelData();
                if ((png || jpeg) && longest <= 2048 && source.Size <= MaxBytes)
                {
                    file.Position = 0;
                    var original = new byte[(int)source.Size];
                    await file.ReadExactlyAsync(original, ct);
                    return new(png ? "image/png" : "image/jpeg", original);
                }
                bool transparent = false;
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 255) { transparent = true; break; }

                for (int quality = 90; quality >= 50; quality -= 10)
                {
                    using var output = new InMemoryRandomAccessStream();
                    var encoder = transparent
                        ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(ct)
                        : await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
                            new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality / 100, PropertyType.Single) }).AsTask(ct);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, transparent ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore,
                        Scale(decoder.OrientedPixelWidth), Scale(decoder.OrientedPixelHeight), 96, 96, pixels);
                    await encoder.FlushAsync().AsTask(ct);
                    if (output.Size <= MaxBytes)
                    {
                        output.Seek(0);
                        var bytes = new byte[(int)output.Size];
                        using var input = output.AsStreamForRead();
                        await input.ReadExactlyAsync(bytes, ct);
                        return new(transparent ? "image/png" : "image/jpeg", bytes);
                    }
                    if (transparent) break;
                }
                edge = Math.Max(1u, edge * 3 / 4);
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or AiUnsupportedInputException or OutOfMemoryException))
        {
            ct.ThrowIfCancellationRequested();
            var reason = string.IsNullOrWhiteSpace(e.Message) ? $"Windows 解码失败（{e.HResult:X8}）" : e.Message;
            throw new AiUnsupportedInputException("无法读取图片：" + reason, e);
        }
    }

    internal void Write(Utf8JsonWriter w, bool responses, bool anthropic)
    {
        w.WriteStartObject();
        if (anthropic)
        {
            w.WriteString("type", "image");
            w.WriteStartObject("source");
            w.WriteString("type", "base64");
            w.WriteString("media_type", MediaType);
            w.WriteBase64String("data", Bytes);
            w.WriteEndObject();
        }
        else
        {
            w.WriteString("type", responses ? "input_image" : "image_url");
            if (!responses) w.WriteStartObject("image_url");
            w.WriteString(responses ? "image_url" : "url", $"data:{MediaType};base64,{Convert.ToBase64String(Bytes)}");
            if (!responses) w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    internal static void WriteText(Utf8JsonWriter w, string text, bool responses)
    {
        w.WriteStartObject();
        w.WriteString("type", responses ? "input_text" : "text");
        w.WriteString("text", text);
        w.WriteEndObject();
    }
}
