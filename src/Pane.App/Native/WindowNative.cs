using System.Runtime.InteropServices;
internal static partial class Native {
    internal delegate nint SubclassProc(nint h,uint m,nuint w,nint l,nuint id,nuint data);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(nint h,SubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint h,SubclassProc proc,nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool UnregisterClassW(string name,nint instance);
    [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo {public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint h);
}
