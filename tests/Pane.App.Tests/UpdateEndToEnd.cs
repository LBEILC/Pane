using System.Text.Json;
using Pane.Settings;
using System.Diagnostics;
using Pane.Contracts;
using Pane.Services.Setup;
using Pane.Services.Updates;

internal static class UpdateEndToEnd
{
    public static async Task Run(string oldPackage, string newPackage, string evidence)
    {
        oldPackage = Path.GetFullPath(oldPackage); newPackage = Path.GetFullPath(newPackage);
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-update-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(evidence);
        string log = Path.Combine(root, "update-timeline.log");
        void Log(string text) { File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {text}\n"); Console.WriteLine(text); }
        Process Start(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            return Process.Start(info) ?? throw new IOException("无法启动测试安装器");
        }
        async Task Wait(Process process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); if (process.ExitCode != 0) throw new Exception("安装器退出码 " + process.ExitCode); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
        Process? installer = null;
        try
        {
            using (var old = Start(oldPackage, "--self-test-root", root)) await Wait(old);
            var paths = SetupPaths.Isolated(root).ResolveInstalled(root);
            string previous = SetupFiles.VersionOf(paths.Exe)!;
            Log("旧版隔离安装完成 " + previous);
            string NormalizeSettings() => JsonSerializer.Serialize(JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(paths.Data, "settings.json")), SettingsJson.Default.PaneSettings), SettingsJson.Default.PaneSettings);
            string settings = NormalizeSettings();
            string data = Path.Combine(paths.Data, "test-data.txt"); File.WriteAllText(data, "隔离测试数据");
            await using var server = new UpdateServer { Payload = await File.ReadAllBytesAsync(newPackage), Version = SetupFiles.VersionOf(newPackage)! };
            if (SetupFiles.CompareVersions(server.Version, previous) <= 0) throw new Exception("新包版本必须高于旧包");
            using var updates = new UpdateService(previous, paths.Data, () => true, package =>
            {
                Log("启动自动更新安装器；测试宿主交出控制权");
                installer = Start(package, "--auto-update", "--self-test-root", root);
            }, server.Url);
            UpdateState? last = null;
            updates.Changed += (_, _) => { if (last != updates.State) { last = updates.State; Log("更新状态 " + last); } };
            await updates.CheckAsync(default);
            if (updates.State != UpdateState.Ready) throw new Exception(updates.Error ?? "未就绪");
            updates.InstallAndRestart();
            if (installer == null) throw new Exception("安装器没有启动");
            await Wait(installer);
            string launched = File.ReadAllText(Path.Combine(root, "launched.txt"));
            if (launched != server.Version || SetupFiles.VersionOf(paths.Exe) != server.Version) throw new Exception("未启动新版");
            if (settings != NormalizeSettings() || File.ReadAllText(data) != "隔离测试数据") throw new Exception("设置或数据未保留");
            if (SetupFiles.VersionOf(Path.Combine(paths.Root, "app.old", "Pane.App.exe")) != previous) throw new Exception("旧版未保留");
            if (Directory.GetFiles(root, "*error.txt").Length != 0) throw new Exception("存在安装错误记录");
            Log($"PASS {previous} -> {launched}; settings=unchanged; data=unchanged; realRegistry=0; globalHooks=0; liveProcesses=0");
        }
        finally
        {
            if (installer != null) { if (!installer.HasExited) { installer.Kill(true); await installer.WaitForExitAsync(); } installer.Dispose(); }
            if (File.Exists(log)) File.Copy(log, Path.Combine(evidence, "update-timeline.log"), true);
            foreach (string error in Directory.GetFiles(root, "*error.txt")) File.Copy(error, Path.Combine(evidence, Path.GetFileName(error)), true);
            SetupFiles.Delete(SetupPaths.Isolated(root).Root);
        }
    }
}