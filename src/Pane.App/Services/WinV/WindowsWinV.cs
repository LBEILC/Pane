using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pane.Services.WinV;

internal sealed class WindowsWinVRegistry : IWinVRegistry
{
    public const string HotkeysPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string HistoryPath = @"Software\Microsoft\Clipboard";
    public RegistryValue Read(bool history)
    {
        using var key = Registry.CurrentUser.OpenSubKey(history ? HistoryPath : HotkeysPath);
        string name = history ? "EnableClipboardHistory" : "DisabledHotkeys";
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new();
        var kind = key.GetValueKind(name);
        if (kind != (history ? RegistryValueKind.DWord : RegistryValueKind.String)) throw new IOException(name + " 的注册表类型不受支持");
        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        return history ? new(true, Number: (int)value) : new(true, (string)value);
    }
    public void Write(bool history, RegistryValue value)
    {
        string path = history ? HistoryPath : HotkeysPath, name = history ? "EnableClipboardHistory" : "DisabledHotkeys";
        if (!value.Exists)
        { using var existing = Registry.CurrentUser.OpenSubKey(path, true); existing?.DeleteValue(name, false); return; }
        using var key = Registry.CurrentUser.CreateSubKey(path, true);
        key.SetValue(name, history ? (object)value.Number!.Value : value.Text!, history ? RegistryValueKind.DWord : RegistryValueKind.String);
    }
}

// 重启策略和 OS 调用分开：超时、启动兜底均能在假平台上测试。
internal interface IExplorerPlatform
{
    string Identity { get; }
    bool TaskbarPresent { get; }
    Task RequestRestartAsync(CancellationToken timeout);
    void Start();
    Task Delay(CancellationToken token);
}
internal sealed class ExplorerRestart(IExplorerPlatform platform, int attempts = 40) : IWinVExplorer
{
    public string Identity => platform.Identity;
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string before = Identity;
        Exception? failure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await platform.RequestRestartAsync(timeout.Token); }
        catch (Exception e) { failure = e is OperationCanceledException ? new IOException("资源管理器重启超时，请重新登录后再试。", e) : e; }
        // 正常退出请求发出后，恢复桌面不可被取消打断。
        for (int i = 0; i < attempts; i++)
        {
            if (platform.TaskbarPresent && Identity != before)
            { if (failure != null) throw failure; return; }
            await platform.Delay(CancellationToken.None);
        }
        if (!platform.TaskbarPresent)
        {
            platform.Start();
            for (int i = 0; i < attempts; i++)
            { if (platform.TaskbarPresent) { if (failure != null) throw failure; return; } await platform.Delay(CancellationToken.None); }
        }
        throw new IOException("资源管理器未完成重启，请重新登录后再试。", failure);
    }
}
internal sealed class WindowsExplorerPlatform : IExplorerPlatform
{
    public bool TaskbarPresent => FindWindowW("Shell_TrayWnd", null) != 0;
    static Process? Shell()
    {
        var window = FindWindowW("Shell_TrayWnd", null);
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out uint pid);
        return Process.GetProcessById((int)pid);
    }
    public string Identity { get { using var process = Shell(); return process == null ? "" : process.Id + ":" + process.StartTime.ToUniversalTime().Ticks; } }
    public Task Delay(CancellationToken token) => Task.Delay(250, token);
    public void Start()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("无法启动资源管理器");
    }
    public Task RequestRestartAsync(CancellationToken timeout) => Task.Run(() =>
    {
        using var process = Shell();
        if (process == null) return;
        var key = new StringBuilder(33);
        Check(RmStartSession(out uint session, 0, key));
        try
        {
            long time = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            var target = new UniqueProcess { Pid = process.Id, Start = new() { dwLowDateTime = (int)time, dwHighDateTime = (int)(time >> 32) } };
            Check(RmRegisterResources(session, 0, null, 1, [target], 0, null));
            // flags=0：不使用 RmForceShutdown；拒绝退出就报告失败。
            int shutdown;
            using (timeout.Register(() => RmCancelCurrentTask(session)))
            { timeout.ThrowIfCancellationRequested(); shutdown = RmShutdown(session, 0, 0); }
            int restart = RmRestart(session, 0, 0);
            Check(shutdown); Check(restart);
        }
        finally { RmEndSession(session); }
    });
    static void Check(int code) { if (code != 0) throw new IOException("资源管理器操作失败（" + code + "）"); }
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public int Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowW(string name, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[]? names, uint applications, UniqueProcess[] processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint session, uint flags, nint callback);
    [DllImport("rstrtmgr.dll")] static extern int RmRestart(uint session, uint flags, nint callback);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll")] static extern int RmCancelCurrentTask(uint session);
}
internal sealed class NoWinVHotkey : IWinVHotkey
{
    public Task<bool> SetAsync(bool enabled) => Task.FromResult(false);
}
