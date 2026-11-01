using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Pane.Hosting;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        // 只允许隐藏桌面，避免在用户桌面显示或驱动系统对话框。
        var desktop = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, 512, out _) ||
            !desktop.ToString().StartsWith("Pane027-", StringComparison.Ordinal)) { Environment.ExitCode = 2; return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Fixture(args[0]));
    }

    sealed class Fixture(string log) : Form
    {
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var rows = new List<string>();
            try
            {
                bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                rows.Add($"elevated={elevated}");
                string folder = Path.Combine(Path.GetTempPath(), "Pane-folder-035-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    foreach (bool cancel in new[] { false, true })
                    {
                        string title = "Pane folder fixture " + Guid.NewGuid().ToString("N");
                        int ticks = 0;
                        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                        timer.Tick += (_, _) => ticks++;
                        timer.Start();
                        Task<string?> pick = FolderPicker.PickFolderAsync(Handle, folder, title);
                        nint dialog = 0;
                        for (int i = 0; i < 100 && dialog == 0; i++)
                        {
                            await Task.Delay(50);
                            EnumWindows((window, _) =>
                            {
                                GetWindowThreadProcessId(window, out uint pid);
                                var text = new StringBuilder(256);
                                GetWindowTextW(window, text, text.Capacity);
                                if (pid == Environment.ProcessId && text.ToString() == title) dialog = window;
                                return true;
                            }, 0);
                        }
                        if (dialog == 0) throw new Exception("未找到真实选择框");
                        if (GetWindow(dialog, 4) != Handle) throw new Exception("所有者窗口未关联");
                        await Task.Delay(200);
                        PostMessageW(dialog, 0x111, cancel ? 2 : 1, 0);
                        string? result = await pick.WaitAsync(TimeSpan.FromSeconds(10));
                        if (cancel ? result != null : result != folder) throw new Exception("路径或取消返回值不符");
                        if (ticks == 0) throw new Exception("界面线程没有继续处理消息");
                        rows.Add($"PASS {(cancel ? "cancel" : "select")}; dialog=real; owner=matched; uiTicks={ticks}; pathMatched=true");
                    }
                }
                finally { Directory.Delete(folder); }
                rows.Add("PASS folder picker; input=0; clipboard=0");
            }
            catch (Exception error) { rows.Add(error.ToString()); Environment.ExitCode = 1; }
            finally { File.WriteAllLines(log, rows); Close(); }
        }
    }

    delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(nint handle, int kind, StringBuilder value, int length, out int needed);
}
