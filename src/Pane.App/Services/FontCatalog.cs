using System.Runtime.InteropServices;
using Pane.Contracts;
namespace Pane.Services;

public sealed class FontCatalog : IFontCatalog
{
    readonly object gate = new();
    Task<IReadOnlyList<FontFamilyInfo>>? cached;
    public Task<IReadOnlyList<FontFamilyInfo>> GetFamiliesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return (cached ??= Task.Run(Read)).WaitAsync(cancellationToken);
    }
    // 设置窗口关闭后释放元数据，下次打开重新枚举本机字体。
    public void ClearCache() { lock (gate) cached = null; }
    static IReadOnlyList<FontFamilyInfo> Read()
    {
        nint data = 0;
        try {
            Marshal.ThrowExceptionForHR(PaneFontFamilies(out data));
            return (Marshal.PtrToStringUni(data) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t')).Select(parts => new FontFamilyInfo(parts[0], parts[1], parts[2] == "1"))
                .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f.DisplayName, StringComparer.CurrentCulture).ToArray();
        } finally { Marshal.FreeCoTaskMem(data); }
    }
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int PaneFontFamilies(out nint data);
}
