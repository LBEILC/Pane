// 安装 / 卸载：安装界面（Views/SetupView）和安装逻辑之间的接口。归设计负责人定义，工程照着实现（018）。
// 安装程序就是 Pane 自己：Pane 安装.exe 把程序解压到临时目录，再以「安装模式」运行其中的 Pane.App.exe；
// 系统「已安装的应用」里的卸载入口以「卸载模式」运行已安装的 Pane.App.exe。两种模式都不启动热键、钩子、托盘。
namespace Pane.Contracts;

/// <param name="Version">要装的版本，例如 0.1.110。</param>
/// <param name="InstalledVersion">已经装着的版本；没装过为 null（界面据此显示「安装」还是「更新到 x」）。</param>
/// <param name="PaneRunning">已装的 Pane 正在运行（安装时会先让它正常退出，界面提前说一句）。</param>
/// <param name="InstallPath">默认安装位置：新装是 %LOCALAPPDATA%\Programs\Pane；升级是已装的位置（升级不让改）。</param>
/// <param name="SizeBytes">装好后占的空间。</param>
/// <param name="StartWithWindows">现在是否开机启动（升级时沿用；新装默认 true）。</param>
public sealed record SetupInfo(string Version, string? InstalledVersion, bool PaneRunning, string InstallPath, long SizeBytes, bool StartWithWindows);

/// <param name="InstallPath">装到哪（用 <see cref="ISetupService.CheckInstallPath"/> 检查过的路径；升级时必须等于已装的位置）。</param>
public sealed record SetupOptions(bool StartWithWindows, bool DesktopShortcut, string InstallPath);

/// <param name="Path">最终会装到的路径（用户选的文件夹不叫 Pane 时，后面补一层 \Pane）。</param>
/// <param name="Error">不能装在这里的原因（给用户看的一句话）；null 表示可以。</param>
public sealed record InstallPathCheck(string Path, string? Error);

/// <param name="Fraction">0～1。</param>
/// <param name="Step">给用户看的一句话，例如「正在复制文件」「正在关闭正在运行的 Pane」。</param>
public sealed record SetupProgress(double Fraction, string Step);

public interface ISetupService
{
    SetupInfo Info { get; }

    /// <summary>
    /// 用户在「更改」里选了一个文件夹：算出最终路径并检查能不能装。
    /// 拒绝：要管理员权限才能写的位置（Program Files、Windows、盘符根目录……）、网络路径、
    /// 已经有别的文件的文件夹（不是空的、也不是 Pane 的安装目录）——卸载时整个删掉，不能误删用户的东西。
    /// </summary>
    InstallPathCheck CheckInstallPath(string folder);

    /// <summary>
    /// 安装或更新。失败时已经回到安装前的状态（旧版本原样可用），抛 <see cref="SetupException"/>，消息给用户看。
    /// 不支持中途取消（复制很快；取消反而容易留下半个安装）。
    /// </summary>
    Task InstallAsync(SetupOptions options, IProgress<SetupProgress> progress);

    /// <summary>启动刚装好的 Pane（「打开 Pane」按钮），然后安装程序自己退出。</summary>
    void LaunchInstalled();

    /// <summary>
    /// 由软件内更新启动的（023，安装包带「自动更新」参数）：界面一出来就开始更新，装好后自己打开新版，不用点按钮。
    /// 只在升级已装的 Pane 时为 true。默认实现只为了在工程实现前能编译。
    /// </summary>
    bool AutoUpdate => false;
}

/// <param name="DataPath">用户数据目录（设置、剪贴板历史、手势……），卸载时可选一起删。</param>
/// <param name="DataBytes">数据目录大小。</param>
public sealed record UninstallInfo(string InstalledVersion, bool PaneRunning, string DataPath, long DataBytes);

public interface IUninstallService
{
    UninstallInfo Info { get; }

    /// <summary>卸载：让正在运行的 Pane 退出、删开机启动项、快捷方式、卸载登记和程序文件；removeData 时连数据目录一起删。失败抛 SetupException。</summary>
    Task UninstallAsync(bool removeData, IProgress<SetupProgress> progress);
}

public sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);
