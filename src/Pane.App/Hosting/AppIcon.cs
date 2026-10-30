// Pane 的程序图标（Assets/pane.ico，由 scripts/convert-icon.ps1 从品牌 SVG 生成）。
// 按系统要的尺寸取对应的那一张，不让系统拿 16px 去放大；进程内只加载一次。
using System.Runtime.InteropServices;
namespace Pane.Hosting;
internal static class AppIcon
{
    static nint big, small;
    static string File => Path.Combine(AppContext.BaseDirectory, "Assets", "pane.ico");

    /// <summary>托盘、窗口标题用的小图标（SM_CXSMICON，按系统缩放）。</summary>
    public static nint Small => small != 0 ? small : small = Load(GetSystemMetrics(49));

    /// <summary>任务栏、Alt+Tab 用的大图标（SM_CXICON）。</summary>
    public static nint Big => big != 0 ? big : big = Load(GetSystemMetrics(11));

    /// <summary>给有任务栏按钮的窗口设上大小两种图标（WM_SETICON）。</summary>
    public static void Apply(nint window)
    {
        SendMessageW(window, 0x80, 1, Big);
        SendMessageW(window, 0x80, 0, Small);
    }

    static nint Load(int size) => LoadImageW(0, File, 1, size, size, 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint LoadImageW(nint h, string file, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint h, uint m, nint w, nint l);
}
