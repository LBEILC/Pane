using Pane.Contracts;
namespace Pane.Services.Setup;

// 工程内部的可替换系统边界，不改变设计定义的产品接口。
internal interface ISetupIntegration
{
    string? InstalledVersion { get; }
    string? InstallLocation { get; }
    void SetPaths(SetupPaths paths);
    bool StartWithWindows { get; }
    object Capture();
    void Restore(object snapshot);
    void Apply(string version, long bytes, SetupOptions options);
    void Remove();
}
internal interface ISetupProcesses
{
    bool Running { get; }
    void SetPaths(SetupPaths paths);
    void Stop();
    void Launch(bool background = false);
}

internal sealed class SetupService : ISetupService, IUninstallService
{
    SetupPaths paths;
    readonly string source, version;
    readonly ISetupIntegration integration;
    readonly ISetupProcesses processes;
    readonly Action<string, string>? copy;
    readonly Func<string, Task>? restoreWinV;
    public SetupService(SetupPaths paths, string source, string version, ISetupIntegration integration,
        ISetupProcesses processes, Action<string, string>? copy = null, bool autoUpdate = false, Func<string, Task>? restoreWinV = null)
    {
        this.paths = integration.InstallLocation is { Length: > 0 } location ? paths with { Root = SetupFiles.Full(location) } : paths;
        this.source = source; this.version = version; this.integration = integration; this.processes = processes; this.copy = copy;
        this.restoreWinV = restoreWinV;
        integration.SetPaths(this.paths); processes.SetPaths(this.paths);
        AutoUpdate = autoUpdate && (File.Exists(this.paths.Exe) || integration.InstalledVersion != null);
    }
    public bool AutoUpdate { get; }
    public InstallPathCheck CheckInstallPath(string folder) => SetupInstallPath.Check(folder);
    public SetupInfo Info => new(version, SetupFiles.VersionOf(paths.Exe) ?? integration.InstalledVersion,
        processes.Running, paths.Root, SetupFiles.Size(source), !File.Exists(paths.Exe) || integration.StartWithWindows);
    UninstallInfo IUninstallService.Info => new(SetupFiles.VersionOf(paths.Exe) ?? integration.InstalledVersion ?? version,
        processes.Running, paths.Data, SetupFiles.Size(paths.Data));
    public void LaunchInstalled() => processes.Launch();
    internal Task Operation { get; private set; } = Task.CompletedTask;
    public Task InstallAsync(SetupOptions options, IProgress<SetupProgress> progress) => Operation = Task.Run(() => Install(options, progress));
    void Install(SetupOptions options, IProgress<SetupProgress> progress)
    {
        string target;
        try { target = SetupFiles.Full(options.InstallPath); }
        catch (Exception error) { throw Failure(error); }
        bool installed = File.Exists(paths.Exe) || integration.InstalledVersion != null;
        if (installed && !target.Equals(SetupFiles.Full(paths.Root), StringComparison.OrdinalIgnoreCase))
            throw new SetupException("更新时装在原来的位置");
        var check = SetupInstallPath.Check(target, appendPane: false, allowExisting: installed);
        if (check.Error != null) throw new SetupException(check.Error);
        paths = paths with { Root = target };
        integration.SetPaths(paths); processes.SetPaths(paths);
        using var operation = Lock();
        string stage = Path.Combine(paths.Root, "app.new"), old = Path.Combine(paths.Root, "app.old"), previous = Path.Combine(paths.Root, "app.previous");
        bool movedCurrent = false, movedOld = false, activated = false, stopped = false;
        bool running = processes.Running;
        object? snapshot = null;
        try
        {
            if (Directory.Exists(stage) || Directory.Exists(previous)) throw new IOException("存在未完成的安装，请先检查 app.new / app.previous");
            if (!File.Exists(Path.Combine(source, "Pane.App.exe"))) throw new IOException("安装包缺少程序文件");
            snapshot = integration.Capture();
            progress.Report(new(.1, "正在复制文件"));
            (copy ?? SetupFiles.Copy)(source, stage);
            progress.Report(new(.5, "正在关闭正在运行的 Pane"));
            processes.Stop(); stopped = true;
            progress.Report(new(.65, "正在更新程序"));
            if (Directory.Exists(old)) { SetupFiles.Check(old); Directory.Move(old, previous); movedOld = true; }
            if (Directory.Exists(paths.App)) { SetupFiles.Check(paths.App); Directory.Move(paths.App, old); movedCurrent = true; }
            Directory.Move(stage, paths.App); activated = true;
            progress.Report(new(.8, "正在创建快捷方式和卸载入口"));
            integration.Apply(version, SetupFiles.Size(paths.App), options);
        }
        catch (Exception error)
        {
            try
            {
                if (activated) Directory.Move(paths.App, stage);
                if (movedCurrent) Directory.Move(old, paths.App);
                if (movedOld) Directory.Move(previous, old);
                // 前置冲突时不删除其他操作留下的暂存目录。
                if (snapshot != null) { SetupFiles.Delete(stage); integration.Restore(snapshot); }
                if (stopped && running && File.Exists(paths.Exe)) processes.Launch(true);
            }
            catch (Exception rollback) { throw new SetupException("安装失败，回退未完成。请保留 app.old / app.previous 后重试。", new AggregateException(error, rollback)); }
            throw Failure(error);
        }
        // 提交点之后的历史副本清理不再触发回滚，以免已部分删除的 previous 覆盖完好的旧版。
        if (movedOld) { try { SetupFiles.Delete(previous); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        progress.Report(new(1, "安装完成"));
    }
    public Task UninstallAsync(bool removeData, IProgress<SetupProgress> progress) => Operation = Task.Run(() =>
    {
        using var operation = Lock();
        try
        {
            progress.Report(new(.1, "正在关闭正在运行的 Pane")); processes.Stop();
            // 恢复先于移除登记和数据；失败保留安装与原值备份，供用户重试。
            if (restoreWinV != null) restoreWinV(paths.Data).GetAwaiter().GetResult();
            else if (File.Exists(Path.Combine(paths.Data, "winv-original.json")))
                throw new IOException("卸载缺少 Win+V 恢复服务");
            progress.Report(new(.35, "正在移除快捷方式和卸载入口")); integration.Remove();
            progress.Report(new(.6, "正在删除程序文件"));
            foreach (string name in new[] { "app", "app.old", "app.new", "app.previous" }) SetupFiles.Delete(Path.Combine(paths.Root, name));
            if (removeData) { progress.Report(new(.85, "正在删除用户数据")); SetupFiles.Delete(paths.Data); }
            operation.Dispose();
            if (Directory.Exists(paths.Root) && !Directory.EnumerateFileSystemEntries(paths.Root).Any())
                Directory.Delete(paths.Root);
            progress.Report(new(1, "卸载完成"));
        }
        catch (Exception error) { throw Failure(error); }
    });
    IDisposable Lock()
    {
        try
        {
            SetupFiles.Check(paths.Root); Directory.CreateDirectory(paths.Root);
            return new FileStream(Path.Combine(paths.Root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) { throw new SetupException("无法开始安装或卸载，请检查目录权限以及是否已有安装程序正在运行。", error); }
    }
    static SetupException Failure(Exception error) => error as SetupException ?? new SetupException(
        (error.HResult & 0xffff) is 112 or 39 ? "磁盘空间不够" : "操作未完成，请检查目录权限和文件是否被其他程序占用后重试。", error);
}
