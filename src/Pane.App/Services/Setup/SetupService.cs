using Pane.Localization;
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
    string step = "开始操作", operationSource = ""; // 不翻译：日志
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
        try { Track("解析安装路径", options.InstallPath); target = SetupFiles.Full(options.InstallPath); } // 不翻译：日志
        catch (Exception error) { Log(error); throw Failure(error); }
        bool installed = File.Exists(paths.Exe) || integration.InstalledVersion != null;
        if (installed && !target.Equals(SetupFiles.Full(paths.Root), StringComparison.OrdinalIgnoreCase))
            throw new SetupException(Strings.Setup_UpdateLocation);
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
            Track("检查暂存目录", stage, previous); // 不翻译：日志
            if (Directory.Exists(stage) || Directory.Exists(previous)) throw new IOException("存在未完成的安装，请先检查 app.new / app.previous"); // 不翻译：日志异常
            Track("检查安装源", source); // 不翻译：日志
            if (!File.Exists(Path.Combine(source, "Pane.App.exe"))) throw new IOException("安装包缺少程序文件"); // 不翻译：日志异常
            Track("备份登记", paths.Root, paths.Data); snapshot = integration.Capture(); // 不翻译：日志
            progress.Report(new(.1, Strings.Setup_CopyingFiles));
            Track("复制程序", source, stage); // 不翻译：日志
            (copy ?? SetupFiles.Copy)(source, stage);
            progress.Report(new(.5, Strings.Setup_StoppingPane));
            Track("停止程序", paths.App); // 不翻译：日志
            processes.Stop(); stopped = true;
            progress.Report(new(.65, Strings.Setup_UpdatingFiles));
            if (Directory.Exists(old)) { Move(old, previous); movedOld = true; }
            if (Directory.Exists(paths.App)) { Move(paths.App, old); movedCurrent = true; }
            Move(stage, paths.App); activated = true;
            progress.Report(new(.8, Strings.Setup_CreatingRegistration));
            Track("登记安装", paths.Root, paths.Data); // 不翻译：日志
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
                if (snapshot != null) { Track("回滚暂存目录", stage); SetupFiles.Delete(stage); Track("回滚登记", paths.Root, paths.Data); integration.Restore(snapshot); } // 不翻译：日志
                if (stopped && running && File.Exists(paths.Exe)) { Track("回滚启动旧程序", paths.Exe); processes.Launch(true); } // 不翻译：日志
            }
            catch (Exception rollback) { Log(rollback); throw new SetupException(Strings.Setup_RollbackIncomplete, new AggregateException(error, rollback)); }
            throw Failure(error);
        }
        // 提交点之后的历史副本清理不再触发回滚，以免已部分删除的 previous 覆盖完好的旧版。
        if (movedOld) { try { Track("清理历史副本", previous); SetupFiles.Delete(previous); } catch (IOException error) { Log(error); } catch (UnauthorizedAccessException error) { Log(error); } } // 不翻译：日志
        progress.Report(new(1, Strings.Setup_InstallComplete));
    }
    public Task UninstallAsync(bool removeData, IProgress<SetupProgress> progress) => Operation = Task.Run(() =>
    {
        using var operation = Lock();
        try
        {
            Track("停止程序", paths.App); progress.Report(new(.1, Strings.Setup_StoppingPane)); processes.Stop(); // 不翻译：日志
            // 恢复先于移除登记和数据；失败保留安装与原值备份，供用户重试。
            Track("恢复 Win+V", paths.Data); // 不翻译：日志
            if (restoreWinV != null) restoreWinV(paths.Data).GetAwaiter().GetResult();
            else if (File.Exists(Path.Combine(paths.Data, "winv-original.json")))
                throw new IOException("卸载缺少 Win+V 恢复服务"); // 不翻译：日志异常
            progress.Report(new(.35, Strings.Setup_RemovingRegistration)); Track("移除登记", paths.Root); integration.Remove(); // 不翻译：日志
            progress.Report(new(.6, Strings.Setup_DeletingFiles));
            foreach (string name in new[] { "app", "app.old", "app.new", "app.previous" }) { Track("删除程序", Path.Combine(paths.Root, name)); SetupFiles.Delete(Path.Combine(paths.Root, name)); } // 不翻译：日志
            if (removeData) { progress.Report(new(.85, Strings.Setup_DeletingData)); Track("删除数据", paths.Data); SetupFiles.Delete(paths.Data); } // 不翻译：日志
            operation.Dispose();
            if (Directory.Exists(paths.Root) && !Directory.EnumerateFileSystemEntries(paths.Root).Any())
                { Track("删除空安装目录", paths.Root); Directory.Delete(paths.Root); } // 不翻译：日志
            progress.Report(new(1, Strings.Setup_UninstallComplete));
        }
        catch (Exception error) { Log(error); throw Failure(error); }
    });
    IDisposable Lock()
    {
        try
        {
            Track("获取安装锁", paths.Root); SetupFiles.Check(paths.Root); Directory.CreateDirectory(paths.Root); // 不翻译：日志
            return new FileStream(Path.Combine(paths.Root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) { Log(error); throw new SetupException(Strings.Setup_CannotStart, error); }
    }
    void Track(string name, string source, string? target = null)
    { step = name; operationSource = source; operationTarget = target; }
    void Move(string source, string target)
    {
        Track("移动目录", source, target); // 不翻译：日志
        SetupFiles.Check(source); SetupFiles.Check(target);
        SetupDiagnostics.Move(source, target, move, delay);
    }
    void Log(Exception error) => SetupDiagnostics.Write(paths.Data, error, step, operationSource, operationTarget);
    static SetupException Failure(Exception error) => error as SetupException ?? new SetupException(
        (error.HResult & 0xffff) is 112 or 39 ? Strings.Setup_DiskFull : SetupDiagnostics.Busy(error) ? Strings.Setup_DirectoryBusy : Strings.Setup_OperationFailed, error);
}
