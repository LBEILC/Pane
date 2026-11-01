using System.Runtime.InteropServices;
using Pane.Contracts;

namespace Pane.Services;

// 原生边界只传字符串和 HWND，JIT / NativeAOT 共用，不依赖动态 COM 封送。
internal sealed class FolderJumpService : IFolderJump, IDisposable
{
    readonly object gate = new();
    readonly Trigger trigger;
    readonly SemaphoreSlim jumping = new(1);
    nint service;
    bool disposed;
    public event EventHandler<FolderJumpRequest>? MenuRequested;

    public FolderJumpService()
    {
        trigger = OnTrigger;
        service = PaneFolderJumpStart(trigger);
    }

    void OnTrigger(nint dialog, int command, int x, int y)
    {
        // 不阻塞前台事件线程；离开对话框时仍可立即注销热键。
        _ = Task.Run(async () =>
        {
            try
            {
                if (GetForegroundWindow() != dialog) return;
                if (command == 1)
                {
                    var first = Entries().FirstOrDefault(e => e.Source == FolderJumpSource.Explorer);
                    if (GetForegroundWindow() != dialog) return;
                    if (first != null) { await JumpAsync(dialog, first.Path, CancellationToken.None); return; }
                }
                lock (gate)
                    if (!disposed && GetForegroundWindow() == dialog) MenuRequested?.Invoke(this, new(dialog, x, y));
            }
            catch { /* 外部窗口已退出或 Shell 暂不可用时静默放弃。 */ }
        });
    }

    public IReadOnlyList<FolderJumpEntry> Entries()
    {
        lock (gate)
        {
            var entries = new List<FolderJumpEntry>();
            if (disposed || service == 0) return entries;
            EntryCallback callback = (path, name, recent) => entries.Add(new(path, name, recent == 0 ? FolderJumpSource.Explorer : FolderJumpSource.Recent));
            PaneFolderJumpEntries(service, callback);
            GC.KeepAlive(callback);
            return entries;
        }
    }

    public async Task JumpAsync(nint dialog, string path, CancellationToken cancellationToken)
    {
        await jumping.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (gate)
                {
                    if (disposed || cancellationToken.IsCancellationRequested) return;
                    // 开始改文本以后必须完成恢复，取消不能把目标路径留在文件名框。
                    PaneFolderJumpTo(dialog, path);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { jumping.Release(); }
    }

    public void Dispose()
    {
        nint old;
        lock (gate) { if (disposed) return; disposed = true; old = service; service = 0; }
        if (old != 0) PaneFolderJumpStop(old);
        GC.KeepAlive(trigger);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Trigger(nint dialog, int command, int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate void EntryCallback([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string name, int recent);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern nint PaneFolderJumpStart(Trigger callback);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern void PaneFolderJumpStop(nint service);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern void PaneFolderJumpEntries(nint service, EntryCallback callback);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern int PaneFolderJumpTo(nint dialog, string path);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
}
