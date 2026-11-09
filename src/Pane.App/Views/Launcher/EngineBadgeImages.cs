// 网页搜索的引擎徽章（设计负责人，issue 43，2026-11-09）：把引擎图标处理成徽章里的图形。
// 照原型 prototype/engine-badge.html 的 B 玻璃做法：图形按实际笔画范围裁切（各站点看起来一样大）；
// 单色图标按主题上色（有品牌色时深色主题下提亮到亮度至少 68%）；自带不透明底的图标（用户自加站点的 favicon）整张铺满、圆角裁切。
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Pane.Views.Launcher;

internal static class EngineBadgeImages
{
    /// <summary>处理好的图形：<see cref="FullBleed"/> 为 true 时整张铺满徽章，否则居中放在玻璃底上。</summary>
    public sealed record Glyph(ImageSource Source, bool FullBleed);

    static readonly Dictionary<string, Glyph?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>界面线程调用；同一个图标 + 主题只处理一次。读不了、全透明时返回 null，界面用首字兜底。</summary>
    public static async Task<Glyph?> LoadAsync(EngineIcon icon, bool dark)
    {
        string key = $"{icon.Path}|{icon.Kind}|{icon.Tint}|{dark}";
        if (cache.TryGetValue(key, out var hit)) return hit;
        Glyph? glyph = null;
        try { glyph = await Build(icon, dark); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        if (cache.Count > 64) cache.Clear();
        cache[key] = glyph;
        return glyph;
    }

    static async Task<Glyph?> Build(EngineIcon icon, bool dark)
    {
        var file = await StorageFile.GetFileFromPathAsync(icon.Path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] px = data.DetachPixelData();

        // 自带底色：内切超椭圆里 90% 以上不透明（和应用图标遮罩的「满幅」同一判断）。只看彩色图标，内置的单色图标都是透明底
        bool full = false;
        if (icon.Kind == EngineIconKind.Color)
        {
            int inside = 0, solid = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double u = (x + 0.5) / (w / 2.0) - 1, v = (y + 0.5) / (h / 2.0) - 1;
                    if (Math.Pow(Math.Abs(u), 5) + Math.Pow(Math.Abs(v), 5) > 0.8) continue;
                    inside++;
                    if (px[(y * w + x) * 4 + 3] > 200) solid++;
                }
            full = inside > 0 && solid > inside * 0.9;
        }

        // 单色上色
        if (icon.Kind == EngineIconKind.Monochrome)
        {
            byte r, g, b; double a;
            if (icon.Tint is { } tint && TryHex(tint, out var c)) { var t = dark ? LiftForDark(c) : c; (r, g, b, a) = (t.R, t.G, t.B, 1.0); }
            else if (dark) (r, g, b, a) = (255, 255, 255, 0.94);
            else (r, g, b, a) = (20, 20, 24, 0.9);
            for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = (byte)(px[i + 3] * a); }
        }

        // 裁到实际笔画范围（满幅的整张保留）
        int x0 = 0, y0 = 0, x1 = w - 1, y1 = h - 1;
        if (!full)
        {
            x0 = w; y0 = h; x1 = -1; y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[(y * w + x) * 4 + 3] > 24) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) return null;
        }
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int s = ((y + y0) * w + x + x0) * 4, d = (y * cw + x) * 4, al = px[s + 3];
                // WriteableBitmap 要预乘透明度
                crop[d] = (byte)(px[s] * al / 255); crop[d + 1] = (byte)(px[s + 1] * al / 255); crop[d + 2] = (byte)(px[s + 2] * al / 255); crop[d + 3] = (byte)al;
            }
        var bitmap = new WriteableBitmap(cw, ch);
        using (var output = bitmap.PixelBuffer.AsStream()) await output.WriteAsync(crop);
        bitmap.Invalidate();
        return new Glyph(bitmap, full);
    }

    static bool TryHex(string s, out Windows.UI.Color color)
    {
        color = default;
        s = s.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>深色主题下提亮品牌色：色相、饱和度不变，亮度至少 68%（百度蓝 #2932E1 → #7076EB）。</summary>
    internal static Windows.UI.Color LiftForDark(Windows.UI.Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double l = (mx + mn) / 2, d = mx - mn, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1)), h = 0;
        if (d != 0) h = mx == r ? ((g - b) / d % 6 + 6) % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        double L = Math.Max(l, 0.68), C = (1 - Math.Abs(2 * L - 1)) * s, X = C * (1 - Math.Abs(h % 2 - 1)), m = L - C / 2;
        var (R, G, B) = h < 1 ? (C, X, 0.0) : h < 2 ? (X, C, 0.0) : h < 3 ? (0.0, C, X) : h < 4 ? (0.0, X, C) : h < 5 ? (X, 0.0, C) : (C, 0.0, X);
        static byte To(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Windows.UI.Color.FromArgb(255, To(R + m), To(G + m), To(B + m));
    }

    /// <summary>首字兜底：拉丁字母取大写首字母，其余取第一个字。</summary>
    public static string Monogram(string name)
    {
        var t = name.Trim();
        if (t.Length == 0) return "?";
        return char.IsAsciiLetter(t[0]) ? char.ToUpperInvariant(t[0]).ToString() : char.IsSurrogate(t[0]) && t.Length > 1 ? t[..2] : t[..1];
    }
}
