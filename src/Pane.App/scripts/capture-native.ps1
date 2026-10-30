$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Observe009 {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern uint GetWindowLong(IntPtr h,int n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; public Point(int x,int y) { X=x;Y=y; } }
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
 [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] public static extern int DwmInt(IntPtr h,int a,out int v,int s);
 [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] public static extern int DwmRect(IntPtr h,int a,out Rect v,int s);
 public static IntPtr[] Windows(int pid) { var list=new List<IntPtr>(); EnumWindows((h,p)=>{uint id;GetWindowThreadProcessId(h,out id);if(id==pid)list.Add(h);return true;},IntPtr.Zero);return list.ToArray(); }
 public static string Class(IntPtr h) {var b=new StringBuilder(512);GetClassName(h,b,b.Capacity);return b.ToString();}
 public static string Title(IntPtr h) {var b=new StringBuilder(1024);GetWindowText(h,b,b.Capacity);return b.ToString();}
}
'@
$dpiSet = [Observe009]::SetProcessDpiAwarenessContext([IntPtr](-4))
if (!$dpiSet) { throw '无法设置截图/观察进程 DPI 感知 v2；停止以避免坐标误差。' }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Z {
 [DllImport("dwmapi.dll")] public static extern int DwmFlush();
 [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h,out uint affinity);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint command);
 [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent,IntPtr child);
}
"@
