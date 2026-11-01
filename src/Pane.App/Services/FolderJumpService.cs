using System.Runtime.InteropServices;
using Pane.Contracts;
using Pane.Settings;

namespace Pane.Services;

// 原生边界只传字符串和 HWND，JIT / NativeAOT 共用，不依赖动态 COM 封送。
internal sealed class FolderJumpService : IFolderJump, IDisposable
{
    readonly object gate = new();
    readonly Trigger trigger;
    readonly SemaphoreSlim jumping = new(1);
    nint service;
    bool disposed;
    FolderJumpSettings settings = new();
    long generation;
    string? problem;
    public string? HotkeyProblem => Volatile.Read(ref problem);
    public event EventHandler? ProblemChanged;
    public event EventHandler<FolderJumpRequest>? MenuRequested;

    public FolderJumpService()
    {
        trigger = OnTrigger;
        service = PaneFolderJumpStart(trigger);
    }

    public void Apply(FolderJumpSettings value)
    {
        lock (gate)
        {
            if (disposed) return;
            value = SettingsStore.NormalizeFolderJump(value);
            if (settings == value) return;
            bool keysChanged = settings.Jump != value.Jump || settings.Menu != value.Menu;
            settings = value; generation++;
            if (keysChanged || !value.Enabled) SetHotkeyProblem(0);
            PaneFolderJumpApply(service, value.Enabled ? 1 : 0, Modifiers(value.Jump), value.Jump.VirtualKey,
                Modifiers(value.Menu), value.Menu.VirtualKey, value.AutoJump ? 1 : 0);
        }
    }
    static uint Modifiers(Hotkey key) =>
        ((key.Modifiers & KeyModifiers.Ctrl) != 0 ? 2u : 0) |
        ((key.Modifiers & KeyModifiers.Alt) != 0 ? 1u : 0) |
        ((key.Modifiers & KeyModifiers.Shift) != 0 ? 4u : 0) |
        ((key.Modifiers & KeyModifiers.Win) != 0 ? 8u : 0);

    internal void SetHotkeyProblem(int x)
    {
        // 原生 Apply 同步等待事件线程，此处不能拿 gate 或同步调用界面。
        var value = settings;
        var next = x == 0 ? null : string.Join("、", new[] {
            (x & 1) != 0 ? HotkeyText(value.Jump) : null,
            (x & 2) != 0 ? HotkeyText(value.Menu) : null }.Where(n => n != null)) + " 被其他程序占用";
        if (Interlocked.Exchange(ref problem, next) != next)
            _ = Task.Run(() => ProblemChanged?.Invoke(this, EventArgs.Empty));
    }

    void OnTrigger(nint dialog, int command, int x, int y)
    {
        if (command == 10)
        {
            SetHotkeyProblem(x);
            return;
        }
        var version = Interlocked.Read(ref generation);
        // 不阻塞前台事件线程；离开对话框时仍可立即注销热键。
        _ = Task.Run(async () =>
        {
            try
            {
                if (!Active(version) || GetForegroundWindow() != dialog) return;
                if (command is 1 or 3)
                {
                    var first = Entries(false).FirstOrDefault(e => e.Source == FolderJumpSource.Explorer);
                    if (GetForegroundWindow() != dialog) return;
                    if (first != null) { await JumpCore(dialog, first.Path, CancellationToken.None, command == 3, version); return; }
                    if (command == 3) return;
                }
                lock (gate)
                    if (!disposed && settings.Enabled && generation == version && GetForegroundWindow() == dialog) MenuRequested?.Invoke(this, new(dialog, x, y));
            }
            catch { /* 外部窗口已退出或 Shell 暂不可用时静默放弃。 */ }
        });
    }

    public IReadOnlyList<FolderJumpEntry> Entries() => Entries(null);

    IReadOnlyList<FolderJumpEntry> Entries(bool? includeRecent)
    {
        lock (gate)
        {
            var entries = new List<FolderJumpEntry>();
            if (disposed || !settings.Enabled || service == 0) return entries;
            EntryCallback callback = (path, name, recent) => entries.Add(new(path, name, recent == 0 ? FolderJumpSource.Explorer : FolderJumpSource.Recent));
            PaneFolderJumpEntriesFiltered(service, callback, (includeRecent ?? settings.ShowRecent) ? 1 : 0);
            GC.KeepAlive(callback);
            return entries;
        }
    }

    bool Active(long version) { lock (gate) return !disposed && settings.Enabled && generation == version; }
    public Task JumpAsync(nint dialog, string path, CancellationToken cancellationToken) =>
        JumpCore(dialog, path, cancellationToken, false, Interlocked.Read(ref generation));
    async Task JumpCore(nint dialog, string path, CancellationToken cancellationToken, bool automatic, long version)
    {
        await jumping.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (gate)
                {
                    if (disposed || !settings.Enabled || generation != version || cancellationToken.IsCancellationRequested) return;
                    // 开始改文本以后必须完成恢复，取消不能把目标路径留在文件名框。
                    if (automatic) PaneFolderJumpAutoTo(service, dialog, path); else PaneFolderJumpTo(dialog, path);
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

    // 与设置页 HotkeyBox.Names / KeyName 保持一致，避免后台服务依赖视图。
    static IEnumerable<string> Names(Hotkey h)
    {
        if (h.Modifiers.HasFlag(KeyModifiers.Ctrl)) yield return "Ctrl";
        if (h.Modifiers.HasFlag(KeyModifiers.Alt)) yield return "Alt";
        if (h.Modifiers.HasFlag(KeyModifiers.Shift)) yield return "Shift";
        if (h.Modifiers.HasFlag(KeyModifiers.Win)) yield return "Win";
        yield return KeyName(h.VirtualKey);
    }

    internal static string HotkeyText(Hotkey h) => string.Join("+", Names(h));

    // 不用 Enum.ToString（NativeAOT 下可能拿不到名字），常用键手写
    static string KeyName(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
        _ => $"键 {vk:X2}",
    };
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern void PaneFolderJumpApply(nint service, int enabled, uint jumpMods, uint jumpKey, uint menuMods, uint menuKey, int automatic);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern void PaneFolderJumpEntriesFiltered(nint service, EntryCallback callback, int recent);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern int PaneFolderJumpAutoTo(nint service, nint dialog, string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Trigger(nint dialog, int command, int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate void EntryCallback([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string name, int recent);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern nint PaneFolderJumpStart(Trigger callback);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)] static extern void PaneFolderJumpStop(nint service);
    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern int PaneFolderJumpTo(nint dialog, string path);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
}
