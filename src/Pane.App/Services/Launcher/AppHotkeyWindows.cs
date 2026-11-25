using System.Runtime.InteropServices;

namespace Pane.Services.Launcher;

internal static class AppHotkeyWindows
{
    delegate void Found(nint window);
    [DllImport("Pane.Native.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    static extern void PaneFindAppWindows(string identity,int packaged,Found found);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern nint SendMessageTimeoutW(nint window,uint message,nuint w,nint l,uint flags,uint timeout,out nuint result);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);

    internal static Task OpenAsync(IndexedApp app,Func<Task> launch)
    {
        List<nint> windows=[];
        string identity=app.Packaged || string.IsNullOrEmpty(app.Target)?app.Launch:app.Target;
        PaneFindAppWindows(identity,app.Packaged?1:0,windows.Add);
        return SwitchOrLaunch(windows,GetForegroundWindow(),IsIconic,
            w=>SendMessageTimeoutW(w,0x112,0xf120,0,2,200,out _),w=>SetForegroundWindow(w),launch);
    }

    internal static Task SwitchOrLaunch(List<nint> windows,nint foreground,Func<nint,bool> minimized,
        Action<nint> restore,Action<nint> activate,Func<Task> launch)
    {
        if(windows.Count==0)return launch();
        // 枚举顺序保留 Z 序；组内轮换按稳定句柄顺序，避免激活后只在前两窗之间跳。
        nint target=windows[0];
        if(windows.Contains(foreground)){
            windows.Sort();
            target=windows[(windows.IndexOf(foreground)+1)%windows.Count];
        }
        if(minimized(target))restore(target);
        activate(target);
        return Task.CompletedTask;
    }
}
