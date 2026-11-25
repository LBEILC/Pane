using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class Window119
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr w, int index);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr w, uint flag);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w, IntPtr pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GuiInfo info);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [StructLayout(LayoutKind.Sequential)]
    struct GuiInfo
    {
        public int size, flags;
        public IntPtr active, focus, capture, menu, move, caret;
        public int left, top, right, bottom;
    }

    public static void Pump(int milliseconds)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (elapsed.ElapsedMilliseconds < milliseconds);
    }

    public static string Sample(IntPtr app, IntPtr input, IntPtr visual)
    {
        var z = new List<string>();
        for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, 2))
        {
            if (w == app) z.Add("A");
            if (w == input) z.Add("I");
            if (w == visual) z.Add("V");
        }
        var info = new GuiInfo { size = Marshal.SizeOf(typeof(GuiInfo)) };
        bool got = GetGUIThreadInfo(GetWindowThreadProcessId(input, IntPtr.Zero), ref info);
        return string.Format("fg={0} focus={1} panelFocus={2} top={3}/{4} visible={5}/{6} order={7}",
            GetForegroundWindow(), got ? info.focus.ToString() : "?", got && (info.focus == input || IsChild(input, info.focus)),
            (GetWindowLongW(input,-20)&8)!=0, (GetWindowLongW(visual,-20)&8)!=0,
            IsWindowVisible(input), IsWindowVisible(visual), string.Join(">", z));
    }
}
