using System.Runtime.InteropServices;
using Pane.Contracts;
using Pane.Services.Clipboard;
namespace Pane.Services;

// UI 线程拥有通知窗口。原生线程只 PostMessage，不跨边界执行逐键托管回调。
internal sealed class PanelKeyRouter : IPanelKeyRouter, IDisposable
{
    const uint Notification = 0x8059;
    readonly Native.Proc proc;
    nint window;
    readonly string className = "PanePanelKeys" + Guid.NewGuid().ToString("N");
    readonly IPastePlatform platform;
    nuint generation;
    bool disposed, started;
    static PanelKeyRouter? current;
    public event EventHandler<PanelKeyPress>? Pressed;
    public bool Active => !disposed && started && PanePanelKeysActive() != 0;
    internal static bool HookInstalled => current is { started: true, disposed: false } && PanePanelKeysInstalled() != 0;
    internal PanelKeyRouter(IPastePlatform? platform = null)
    {
        this.platform = platform ?? new WindowsPastePlatform();
        proc = WndProc;
        current = this;
        AppDomain.CurrentDomain.ProcessExit += ProcessExit;
    }
    bool EnsureWindow()
    {
        if (window != 0) return true;
        var wc = new Native.WndClass { size = (uint)Marshal.SizeOf<Native.WndClass>(), proc = proc, name = className, instance = Native.GetModuleHandleW(null) };
        if (Native.RegisterClassExW(ref wc) == 0) return false;
        window = Native.CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, -3, 0, wc.instance, 0);
        if (window == 0) Native.UnregisterClassW(className, wc.instance);
        return window != 0;
    }
    public bool Begin(nint target, bool sendEnabled)
    {
        if (disposed) return false;
        End();
        if (target == 0 || !platform.Exists(target) || platform.Higher(target) == true || !EnsureWindow()) return false;
        started = true;
        return PanePanelKeysBegin(window, Notification, generation, sendEnabled ? 1 : 0) != 0;
    }
    public void End() { generation++; if (started && !disposed) PanePanelKeysEnd(); }
    public void ReplayAndEnd() { if (Active) { PanePanelKeysReplay(); generation++; } }
    nint WndProc(nint h, uint m, nuint w, nint l)
    {
        if (m == Notification)
        {
            if (!disposed && w == generation && Active)
            {
                int value = (int)l, key = (value & 255) - 1;
                if (key >= 0 && key <= (int)PanelKey.Type) Pressed?.Invoke(this, new((PanelKey)key, value >> 8));
            }
            return 0;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }
    public void Dispose()
    {
        if (disposed) return;
        End(); if (started) PanePanelKeysDispose(); disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
        if (window != 0) { Native.DestroyWindow(window); Native.UnregisterClassW(className, Native.GetModuleHandleW(null)); window = 0; }
        Pressed = null; if (current == this) current = null;
    }
    // 现有退出路径没有逐个 StopAsync；退出事件仅释放原生资源，不跨线程销毁 UI 窗口。
    void ProcessExit(object? sender, EventArgs args) { if (started) { PanePanelKeysDispose(); started = false; } }
    [DllImport("Pane.Native.dll")] static extern int PanePanelKeysBegin(nint recipient, uint message, nuint generation, int send);
    [DllImport("Pane.Native.dll")] static extern void PanePanelKeysEnd();
    [DllImport("Pane.Native.dll")] static extern void PanePanelKeysReplay();
    [DllImport("Pane.Native.dll")] static extern int PanePanelKeysActive();
    [DllImport("Pane.Native.dll")] static extern int PanePanelKeysInstalled();
    [DllImport("Pane.Native.dll")] static extern void PanePanelKeysDispose();
}
