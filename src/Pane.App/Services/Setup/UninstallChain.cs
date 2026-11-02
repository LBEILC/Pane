using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pane.Services.Setup;

// 应用和无资源的安装外壳共享的卸载进程边界。
internal static class UninstallChain
{
    public static string LogPath => Path.Combine(Path.GetTempPath(), "Pane-uninstall.log");
    public static void Log(string step, string result = "ok", Exception? error = null, string? path = null)
    {
        try
        {
            using var mutex = new Mutex(false, @"Local\Pane-Uninstall-Log");
            bool owned;
            try { owned = mutex.WaitOne(2000); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return;
            try
            {
                int nativeCode = error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : 0;
                string line = $"{DateTimeOffset.UtcNow:O}\tpid={Environment.ProcessId}\tstep={step}\tresult={result}\tcode=0x{(error?.HResult ?? 0):X8}\tnative={nativeCode}\texception={error?.GetType().FullName ?? "-"}\tpath={path?.Replace('\r', ' ').Replace('\n', ' ')}\n";
                string text = (File.Exists(LogPath) ? File.ReadAllText(LogPath) : "") + line;
                while (Encoding.UTF8.GetByteCount(text) > 65536)
                { int end = text.IndexOf('\n'); text = end < 0 ? "" : text[(end + 1)..]; }
                File.WriteAllText(LogPath, text, new UTF8Encoding(false));
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static Process Start(ProcessStartInfo start)
    {
        // 先以挂起状态取得 PID，定向交出前台权限后才运行；不向任意进程放行。
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder(Quote(start.FileName));
        foreach (string argument in start.ArgumentList) command.Append(' ').Append(Quote(argument));
        if (!CreateProcessW(start.FileName, command, 0, 0, false, 0x08000004, 0, start.WorkingDirectory, ref startup, out var child))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            bool allowed = AllowSetForegroundWindow(child.Pid);
            Log("foreground-grant", allowed ? "ok" : "denied", allowed ? null : new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var process = Process.GetProcessById((int)child.Pid);
            _ = process.Handle; // 在子进程可能退出前持有查询句柄，保留退出码。
            if (ResumeThread(child.Thread) == uint.MaxValue)
            { var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); process.Dispose(); throw error; }
            Log("process-start", path: start.FileName);
            return process;
        }
        catch { TerminateProcess(child.Process, 1); throw; }
        finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
    }
    // Windows 原生命令行由参数数组编码，不经过 shell。
    static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static void Signal(string? name)
    {
        if (name == null) return;
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException error) { Log("window-signal", "launcher-already-exited", error); }
    }
    public static string ValidateEvent(string name) => name.StartsWith(@"Local\Pane-Uninstall-", StringComparison.Ordinal)
        && Guid.TryParseExact(name[21..], "N", out _) ? name : throw new ArgumentException("Invalid uninstall event");
    public static void Launch(SetupPaths paths, string executable, string? isolated, Action<string, Exception> failure,
        Action<string, string>? copy = null, Func<ProcessStartInfo, Process>? start = null, int timeout = 10000)
    {
        string step = "copy-helper";
        string? helper = null;
        bool launched = false;
        try
        {
            Cleanup(isolated);
            helper = Path.Combine(Path.GetTempPath(), "Pane-Cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
            SetupFiles.Check(executable); (copy ?? ((a, b) => File.Copy(a, b)))(executable, helper);
            Log(step, path: helper);
            string name = @"Local\Pane-Uninstall-" + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
            foreach (string arg in new[] { "--relocate", paths.App, "-1", "--ready-event", name, "--launcher-pid", Environment.ProcessId.ToString() }) info.ArgumentList.Add(arg);
            if (isolated != null) { info.ArgumentList.Add("--self-test-root"); info.ArgumentList.Add(isolated); }
            step = "start-helper";
            using var child = (start ?? Start)(info);
            launched = true;
            if (isolated != null) File.WriteAllLines(Path.Combine(isolated, "helper.txt"), [helper, child.Id.ToString()]);
            step = "wait-window";
            if (!ready.WaitOne(timeout)) throw new TimeoutException();
            Log(step);
        }
        catch (Exception e)
        {
            Log(step, "failed", e);
            if (!launched && helper != null)
            {
                try { File.Delete(helper); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { Log("cleanup-unstarted-helper", "retained", cleanup, helper); }
            }
            failure(step, e);
        }
    }
    public static void Cleanup(string? isolated = null)
    {
        // 新链路的残留由 lease 标识；尝试排他打开，活动事务始终保留。
        // 没有 lease 的旧版残留保留一天，避免干扰正在运行的旧安装器。
        foreach (string path in Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "Pane-*"))
        {
            string name = Path.GetFileName(path);
            string? token = name.StartsWith("Pane-Cleanup-") && name.EndsWith(".exe") ? name[13..^4]
                : name.StartsWith("Pane-Setup-") ? name[11..] : null;
            if (token == null || !Guid.TryParseExact(token, "N", out _)) continue;
            string lease = path + ".lease";
            if (isolated != null && (!File.Exists(lease) || !LeaseMatches(lease, isolated))) continue;
            if (!File.Exists(lease) && (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1) || InUse(path))) continue;
            try
            {
                SetupFiles.Check(path); SetupFiles.Check(lease);
                using (var guard = new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                { if (Directory.Exists(path)) SetupFiles.Delete(path); else File.Delete(path); }
                File.Delete(lease);
                Log("cleanup-stale", path: path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("cleanup-stale", "retained", e, path); }
        }
    }
    static bool InUse(string path)
    {
        bool directory = Directory.Exists(path);
        var processes = Process.GetProcessesByName(directory ? "Pane.App" : Path.GetFileNameWithoutExtension(path));
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    string? exe = process.MainModule?.FileName;
                    return exe != null && (directory ? exe.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) : exe.Equals(path, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
            });
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    static bool LeaseMatches(string path, string isolated)
    {
        try { SetupFiles.Check(path); return File.ReadAllText(path) == isolated; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static FileStream Lease(string path, string? isolated = null)
    {
        var stream = new FileStream(path + ".lease", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(isolated ?? "")); stream.Flush(); return stream;
    }
    public static void WaitLauncher(int pid)
    {
        if (pid <= 0) return;
        try { using var launcher = Process.GetProcessById(pid); if (!launcher.WaitForExit(10000)) throw new TimeoutException("Launcher did not exit"); }
        catch (ArgumentException) { }
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetForegroundWindow(nint window);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public nint Process, Thread; public uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string file, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
