using System.Runtime.InteropServices;

internal static partial class Native
{
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint h,nuint id,uint ms,nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint h,nuint id);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(nint h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint h,int id);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint h);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent,nint child);
    [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint h,nint region,bool redraw);
    [DllImport("gdi32.dll")] internal static extern nint CreateRoundRectRgn(int l,int t,int r,int b,int ew,int eh);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] internal static extern bool SystemParametersInfoW(uint action,uint param,out Rect value,uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WndClass
    {
        public uint size, style; public Proc proc; public int clsExtra, winExtra;
        public nint instance, icon, cursor, background; public string? menu, name; public nint smallIcon;
    }
    internal delegate nint Proc(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WndClass c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint h, out Rect r);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint h);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint h, int a, ref int v, int s);

    // 直接调用公开的 ICompositorDesktopInterop ABI，避免增加互操作包。
    internal static unsafe Windows.UI.Composition.Desktop.DesktopWindowTarget Target(Windows.UI.Composition.Compositor c, nint hwnd)
    {
        var obj = (WinRT.IWinRTObject)c;
        using var interop = obj.NativeObject.As(new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"));
        nint result = 0;
        var vtable = *(nint**)interop.ThisPtr;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)vtable[3])(interop.ThisPtr, hwnd, 0, &result);
        Marshal.ThrowExceptionForHR(hr);
        try { return WinRT.MarshalInterface<Windows.UI.Composition.Desktop.DesktopWindowTarget>.FromAbi(result); }
        finally { Marshal.Release(result); }
    }
}
