using Pane.Contracts;
using Pane.Services.WinV;
using Pane.Services.Setup;
using Pane.Settings;
using System.Text.Json;

internal static class WinVTests
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("030: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "Pane-018-030-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var original in new[] { new RegistryValue(), new(true, ""), new(true, "EX"), new(true, "EV"), new(true, "v") })
            {
                string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var registry = new FakeRegistry { Hotkeys = original };
                var explorer = new FakeExplorer(); var key = new FakeHotkey();
                var service = new WinVReplacement(dir, registry, explorer, key);
                int events = 0; service.Changed += (_, _) => events++;
                registry.BeforeWrite = () => Check(File.Exists(Path.Combine(dir, "winv-original.json")), "写系统前备份存在");
                await service.InitializeAsync();
                Check(service.State == WinVState.Off && registry.Writes == 0 && key.Calls == 0, "默认关闭无副作用");
                await service.EnableAsync(false, default);
                Check(service.State == WinVState.Pending && explorer.Restarts == 0 && key.Calls == 0, "下次登录不注册不重启");
                Check(registry.Hotkeys.Text!.Contains('V', StringComparison.OrdinalIgnoreCase), "追加 V 或保留已有 V");
                await service.SetSystemHistoryDisabledAsync(true, default);
                Check(service.SystemHistoryDisabled && registry.History == new RegistryValue(true, Number: 0), "只关闭历史");
                var keys = registry.Hotkeys;
                await service.SetSystemHistoryDisabledAsync(false, default);
                Check(!service.SystemHistoryDisabled && !registry.History.Exists && registry.Hotkeys == keys, "只恢复历史不改热键");
                await service.DisableAsync(false, default);
                Check(service.State == WinVState.Off && registry.Hotkeys == original && !registry.History.Exists, "未生效关闭直接 Off 且原样恢复");
                Check(events == 5, "变更事件");
                key.Result = true;
                await service.EnableAsync(true, default);
                Check(service.State == WinVState.On && explorer.Restarts == 1, "注册成功才 On");
                registry.History = new(true, Number: 1);
                await service.SetSystemHistoryDisabledAsync(true, default);
                await service.DisableAsync(false, default);
                Check(service.State == WinVState.RestorePending && !key.Enabled && registry.Hotkeys == original && registry.History.Number == 1, "生效后关闭并恢复历史");
                var resumed = new WinVReplacement(dir, registry, explorer, key);
                await resumed.InitializeAsync();
                Check(resumed.State == WinVState.RestorePending, "同一桌面启动保留恢复等待");
                explorer.Identity = "login-2";
                resumed = new(dir, registry, explorer, key); await resumed.InitializeAsync();
                Check(resumed.State == WinVState.Off, "登录后恢复 Off");
            }
            {
                var registry = new FakeRegistry { Hotkeys = new(true, "EX") };
                var explorer = new FakeExplorer(); var key = new FakeHotkey(); string dir = Path.Combine(root, "conflict");
                var service = new WinVReplacement(dir, registry, explorer, key);
                await service.EnableAsync(true, default);
                Check(service.State == WinVState.Pending && service.Error != null, "注册冲突不误报成功");
                registry.Hotkeys = new(true, "EXVQ");
                await service.EnableAsync(false, default);
                Check(registry.Hotkeys.Text == "EXVQ", "重试保留用户新增字符");
                await service.DisableAsync(false, default);
                Check(registry.Hotkeys.Text == "EXQ" && service.State == WinVState.RestorePending, "注册冲突后关闭仍需恢复且保留其他字符");
                await service.DisableAsync(true, default);
                Check(service.State == WinVState.Off && service.Error == null, "恢复重试清错误");
                await service.EnableAsync(false, default);
                key.Result = true; explorer.Identity = "login";
                service = new(dir, registry, explorer, key); await service.InitializeAsync();
                Check(service.State == WinVState.On && key.Enabled, "启动按实际注册恢复 On");
                service.RegistrationChanged(false);
                Check(service.State == WinVState.Pending, "暂停热键不保留虚假 On");
                service.RegistrationChanged(true);
                Check(service.State == WinVState.On, "恢复注册更新 On");
                registry.Fail = true;
                await service.DisableAsync(false, default);
                Check(service.Error != null && !key.Enabled && File.Exists(Path.Combine(dir, "winv-original.json")), "恢复失败保留记录并释放热键");
                registry.Fail = false;
                await service.RestoreForUninstallAsync();
                Check(registry.Hotkeys.Text == "EXQ" && service.Error == null, "恢复失败后可重试");
            }
            {
                var registry = new FakeRegistry(); var explorer = new FakeExplorer { Fail = true }; var key = new FakeHotkey();
                string dir = Path.Combine(root, "failure"); var service = new WinVReplacement(dir, registry, explorer, key);
                await service.EnableAsync(true, default);
                Check(service.State == WinVState.Pending && service.Error != null && key.Calls == 0, "重启失败不注册");
                await service.DisableAsync(false, default);
                Check(!registry.Hotkeys.Exists && service.State == WinVState.RestorePending, "重启失败可恢复");
                await service.SetSystemHistoryDisabledAsync(true, default);
                Check(service.Error != null && !registry.History.Exists, "未启用拒绝历史开关");
                File.WriteAllText(Path.Combine(dir, "winv-original.json"), "broken");
                service = new(dir, registry, explorer, key);
                await service.InitializeAsync();
                Check(service.Error != null, "损坏记录报告错误");
                try { await service.RestoreForUninstallAsync(); Check(false, "损坏备份应阻止卸载"); } catch (IOException) { Check(true, "损坏备份阻止卸载"); }
            }
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var platform = new FakeExplorerPlatform { Mode = mode };
                try { await new ExplorerRestart(platform, 2).RestartAsync(default); Check(mode <= 1, "正常重启或兜底成功"); }
                catch (IOException) { Check(mode >= 2, "超时或拒绝报告失败"); }
                Check(platform.Starts == (mode is 1 or 2 ? 1 : 0), "任务栏缺失才启动兜底");
            }
            {
                string dir = Path.Combine(root, "crash"); Directory.CreateDirectory(dir);
                var journal = new WinVJournal { Version = 1, Enabled = true, HotkeysTouched = true, HistoryTouched = true,
                    Hotkeys = new(true, "Q"), History = new(true, Number: 0), Explorer = "previous-login" };
                string file = Path.Combine(dir, "winv-original.json");
                File.WriteAllText(file, JsonSerializer.Serialize(journal, WinVJson.Default.WinVJournal));
                var registry = new FakeRegistry { Hotkeys = journal.Hotkeys, History = journal.History };
                var key = new FakeHotkey(); var explorer = new FakeExplorer();
                var service = new WinVReplacement(dir, registry, explorer, key); await service.InitializeAsync();
                Check(service.State == WinVState.Pending && registry.Hotkeys.Text == "QV" && registry.History.Number == 0, "写前崩溃启动补完");
                await service.RestoreForUninstallAsync();
                Check(registry.Hotkeys.Text == "Q" && registry.History.Number == 0, "历史原来已关闭仍原样恢复");
                journal.Enabled = false; journal.Restoring = true; registry.Hotkeys = new(true, "QV");
                File.WriteAllText(file, JsonSerializer.Serialize(journal, WinVJson.Default.WinVJournal));
                service = new(dir, registry, explorer, key); await service.InitializeAsync();
                Check(registry.Hotkeys.Text == "Q" && service.State == WinVState.RestorePending, "跨登录补恢复不能提前 Off");
                explorer.Identity = "another-login";
                service = new(dir, registry, explorer, key); await service.InitializeAsync();
                Check(service.State == WinVState.Off, "补恢复后的下一次登录 Off");
                int writes = registry.Writes;
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await service.EnableAsync(true, cancellation.Token); Check(false, "应取消"); } catch (OperationCanceledException) { Check(true, "操作前取消"); }
                Check(registry.Writes == writes && explorer.Restarts == 0, "取消不触碰边界");
            }
            {
                string dir = Path.Combine(root, "uninstall");
                var paths = SetupPaths.Isolated(root) with { Root = dir }; Directory.CreateDirectory(paths.App); Directory.CreateDirectory(paths.Data);
                File.WriteAllText(paths.Exe, "fake");
                var registry = new FakeRegistry { Hotkeys = new(true, "Q"), History = new(true, Number: 1) };
                var service = new WinVReplacement(paths.Data, registry, new FakeExplorer(), new FakeHotkey { Result = true });
                await service.EnableAsync(true, default); await service.SetSystemHistoryDisabledAsync(true, default);
                var integration = new FakeIntegration(); var processes = new FakeProcesses();
                var setup = new SetupService(paths, dir, "0.1.1", integration, processes, restoreWinV: async data =>
                {
                    Check(processes.Stopped && Directory.Exists(paths.Data), "卸载先停实例，删除数据前恢复");
                    await new WinVReplacement(data, registry, new FakeExplorer(), new NoWinVHotkey()).RestoreForUninstallAsync();
                });
                registry.Fail = true;
                try { await setup.UninstallAsync(true, new Progress<SetupProgress>()); Check(false, "失败应阻止卸载"); } catch (SetupException) { Check(true, "恢复失败阻止卸载"); }
                Check(File.Exists(paths.Exe) && File.Exists(Path.Combine(paths.Data, "winv-original.json")) && !integration.Removed, "恢复失败保留安装与备份");
                registry.Fail = false;
                await setup.UninstallAsync(true, new Progress<SetupProgress>());
                Check(registry.Hotkeys.Text == "Q" && registry.History.Number == 1 && integration.Removed && !Directory.Exists(paths.Data), "卸载恢复后才删除数据");
            }
            {
                string dir = Path.Combine(root, "blocked-data"); File.WriteAllText(dir, "fixture");
                var registry = new FakeRegistry(); var explorer = new FakeExplorer();
                var service = new WinVReplacement(dir, registry, explorer, new FakeHotkey());
                await service.EnableAsync(false, default);
                await service.EnableAsync(false, default);
                Check(service.Error != null && registry.Writes == 0, "备份写失败及重试都不能修改系统");
                File.Delete(dir);
                await service.EnableAsync(false, default);
                Check(service.Error == null && registry.Hotkeys.Text == "V" && File.Exists(Path.Combine(dir, "winv-original.json")), "目录修复后先备份再应用");
                registry.Fail = true;
                await service.SetSystemHistoryDisabledAsync(true, default);
                Check(service.Error != null && !service.SystemHistoryDisabled && !registry.History.Exists, "历史写失败不误报已关闭");
                registry.Fail = false;
                await service.SetSystemHistoryDisabledAsync(true, default);
                Check(service.Error == null && service.SystemHistoryDisabled, "历史写失败后可重试");
                await service.DisableAsync(false, default);
                Check(!registry.Hotkeys.Exists && service.State == WinVState.Off, "失败恢复后可完整关闭");
            }
            var old = JsonSerializer.Deserialize("{\"Hotkey\":{\"Modifiers\":2,\"VirtualKey\":32},\"UToolsHotkeyReleased\":true}", SettingsJson.Default.LauncherSettings);
            Check(old?.Hotkey.VirtualKey == 32, "旧 uTools 字段忽略且正常读配置");
        }
        finally { SetupFiles.Delete(root); }
        Console.WriteLine($"PASS winv assertions={count}; realRegistry=0; explorer=0; realHotkeys=0");
        return count;
    }
    sealed class FakeRegistry : IWinVRegistry
    {
        public RegistryValue Hotkeys = new(), History = new(); public int Writes; public bool Fail; public Action? BeforeWrite;
        public RegistryValue Read(bool history) => history ? History : Hotkeys;
        public void Write(bool history, RegistryValue value) { BeforeWrite?.Invoke(); if (Fail) throw new IOException("fake denied"); Writes++; if (history) History = value; else Hotkeys = value; }
    }
    sealed class FakeHotkey : IWinVHotkey { public bool Result, Enabled; public int Calls; public Task<bool> SetAsync(bool enabled) { Calls++; Enabled = enabled && Result; return Task.FromResult(Enabled); } }
    sealed class FakeExplorer : IWinVExplorer { public string Identity { get; set; } = "shell"; public int Restarts; public bool Fail; public Task RestartAsync(CancellationToken token) { Restarts++; if (Fail) throw new IOException("fake restart"); Identity += "-next"; return Task.CompletedTask; } }
    sealed class FakeExplorerPlatform : IExplorerPlatform
    {
        public int Mode, Starts; public string Identity { get; private set; } = "old"; public bool TaskbarPresent { get; private set; } = true;
        public Task RequestRestartAsync(CancellationToken timeout) { if (Mode == 0) Identity = "new"; else if (Mode != 3) TaskbarPresent = false; return Task.CompletedTask; }
        public void Start() { Starts++; if (Mode == 1) { TaskbarPresent = true; Identity = "fallback"; } }
        public Task Delay(CancellationToken token) => Task.CompletedTask;
    }
    sealed class FakeProcesses : ISetupProcesses { public bool Stopped; public bool Running => !Stopped; public void SetPaths(SetupPaths paths) { } public void Stop() => Stopped = true; public void Launch(bool background = false) { } }
    sealed class FakeIntegration : ISetupIntegration
    {
        public bool Removed; public string? InstalledVersion => null; public string? InstallLocation => null; public bool StartWithWindows => false;
        public void SetPaths(SetupPaths paths) { } public object Capture() => new(); public void Restore(object snapshot) { } public void Apply(string version, long bytes, SetupOptions options) { } public void Remove() => Removed = true;
    }
}
