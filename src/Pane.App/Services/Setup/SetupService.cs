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
    readonly Action<string, string>? move;
    readonly Action<int>? delay;
    string step = "开始操作", operationSource = "";
    string? operationTarget;
    public SetupService(SetupPaths paths, string source, string version, ISetupIntegration integration,
        ISetupProcesses processes, Action<string, string>? copy = null, bool autoUpdate = false, Func<string, Task>? restoreWinV = null, Action<string, string>? move = null, Action<int>? delay = null)
    {
        this.paths = integration.InstallLocation is { Length: > 0 } location ? paths with { Root = SetupFiles.Full(location) } : paths;
        this.source = source; this.version = version; this.integration = integration; this.processes = processes; this.copy = copy;
        this.restoreWinV = restoreWinV; this.move = move; this.delay = delay;
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
        try { Track("解析安装路径", options.InstallPath); target = SetupFiles.Full(options.InstallPath); }
        catch (Exception error) { Log(error); throw Failure(error); }
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
            Track("检查暂存目录", stage, previous);
            if (Directory.Exists(stage) || Directory.Exists(previous)) throw new IOException("存在未完成的安装，请先检查 app.new / app.previous");
            Track("检查安装源", source);
            if (!File.Exists(Path.Combine(source, "Pane.App.exe"))) throw new IOException("安装包缺少程序文件");
            Track("备份登记", paths.Root, paths.Data); snapshot = integration.Capture();
            progress.Report(new(.1, "正在复制文件"));
            Track("复制程序", source, stage);
            (copy ?? SetupFiles.Copy)(source, stage);
            progress.Report(new(.5, "正在关闭正在运行的 Pane"));
            Track("停止程序", paths.App);
            processes.Stop(); stopped = true;
            progress.Report(new(.65, "正在更新程序"));
            if (Directory.Exists(old)) { Move(old, previous); movedOld = true; }
            if (Directory.Exists(paths.App)) { Move(paths.App, old); movedCurrent = true; }
            Move(stage, paths.App); activated = true;
            progress.Report(new(.8, "正在创建快捷方式和卸载入口"));
            Track("登记安装", paths.Root, paths.Data);
            integration.Apply(version, SetupFiles.Size(paths.App), options);
        }
        catch (Exception error)
        {
            Log(error);
            try
            {
                if (activated) Move(paths.App, stage);
                if (movedCurrent) Move(old, paths.App);
                if (movedOld) Move(previous, old);
                // 前置冲突时不删除其他操作留下的暂存目录。
                if (snapshot != null) { Track("回滚暂存目录", stage); SetupFiles.Delete(stage); Track("回滚登记", paths.Root, paths.Data); integration.Restore(snapshot); }
                if (stopped && running && File.Exists(paths.Exe)) { Track("回滚启动旧程序", paths.Exe); processes.Launch(true); }
            }
            catch (Exception rollback) { Log(rollback); throw new SetupException("安装失败，回退未完成。请保留 app.old / app.previous 后重试。", new AggregateException(error, rollback)); }
            throw Failure(error);
        }
        // 提交点之后的历史副本清理不再触发回滚，以免已部分删除的 previous 覆盖完好的旧版。
        if (movedOld) { try { Track("清理历史副本", previous); SetupFiles.Delete(previous); } catch (IOException error) { Log(error); } catch (UnauthorizedAccessException error) { Log(error); } }
        progress.Report(new(1, "安装完成"));
    }
    public Task UninstallAsync(bool removeData, IProgress<SetupProgress> progress) => Operation = Task.Run(() =>
    {
        using var operation = Lock();
        try
        {
            Track("停止程序", paths.App); progress.Report(new(.1, "正在关闭正在运行的 Pane")); processes.Stop();
            // 恢复先于移除登记和数据；失败保留安装与原值备份，供用户重试。
            Track("恢复 Win+V", paths.Data);
            if (restoreWinV != null) restoreWinV(paths.Data).GetAwaiter().GetResult();
            else if (File.Exists(Path.Combine(paths.Data, "winv-original.json")))
                throw new IOException("卸载缺少 Win+V 恢复服务");
            progress.Report(new(.35, "正在移除快捷方式和卸载入口")); Track("移除登记", paths.Root); integration.Remove();
            progress.Report(new(.6, "正在删除程序文件"));
            foreach (string name in new[] { "app", "app.old", "app.new", "app.previous" }) { Track("删除程序", Path.Combine(paths.Root, name)); SetupFiles.Delete(Path.Combine(paths.Root, name)); }
            if (removeData) { progress.Report(new(.85, "正在删除用户数据")); Track("删除数据", paths.Data); SetupFiles.Delete(paths.Data); }
            operation.Dispose();
            if (Directory.Exists(paths.Root) && !Directory.EnumerateFileSystemEntries(paths.Root).Any())
                { Track("删除空安装目录", paths.Root); Directory.Delete(paths.Root); }
            progress.Report(new(1, "卸载完成"));
        }
        catch (Exception error) { Log(error); throw Failure(error); }
    });
    IDisposable Lock()
    {
        try
        {
            Track("获取安装锁", paths.Root); SetupFiles.Check(paths.Root); Directory.CreateDirectory(paths.Root);
            return new FileStream(Path.Combine(paths.Root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) { Log(error); throw new SetupException("无法开始安装或卸载，请检查目录权限以及是否已有安装程序正在运行。", error); }
    }
    void Track(string name, string source, string? target = null)
    { step = name; operationSource = source; operationTarget = target; }
    void Move(string source, string target)
    {
        Track("移动目录", source, target);
        SetupFiles.Check(source); SetupFiles.Check(target);
        SetupDiagnostics.Move(source, target, move, delay);
    }
    void Log(Exception error) => SetupDiagnostics.Write(paths.Data, error, step, operationSource, operationTarget);
    static SetupException Failure(Exception error) => error as SetupException ?? new SetupException(
        (error.HResult & 0xffff) is 112 or 39 ? "磁盘空间不够" : SetupDiagnostics.Busy(error) ? "有程序正在使用 Pane 的安装目录，关掉打开着这个目录的窗口或程序后再试。" : "操作未完成，请检查目录权限和文件是否被其他程序占用后重试。", error);
}
