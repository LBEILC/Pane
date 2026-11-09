// 能力模块（2026-11-04 设计定义，055）：界面（设置 →「能力」页、各处联动入口）和模块宿主之间的接口。
// 规则见 docs/design.md「能力模块」；工程照着实现宿主和各模块。
namespace Pane.Contracts;

/// <summary>能力的稳定标识，存进 settings.json，不能改。显示名称和说明由界面资源给出。</summary>
public static class ModuleIds
{
    public const string Launcher = "launcher";
    public const string Clipboard = "clipboard";
    public const string Gestures = "gestures";
    public const string SuperPanel = "super-panel";
    public const string Ai = "ai";
    public const string FolderJump = "folder-jump";
    public const string Rename = "rename";
    public const string PowerTimer = "power-timer";   // 定时关机（2026-11-05，058）
    public const string Words = "words";   // 分词（2026-11-09，093）
}

/// <summary>「能力」页的分组。</summary>
public enum ModuleCategory { Search, Clipboard, Mouse, Ai, Files, System }

/// <summary>
/// Required = 硬依赖：对方没启用就不能启用自己（启用时界面问一句「同时启用 X？」）。
/// Optional = 软依赖：对方在就显示联动入口，不在就隐藏，自己照常工作。
/// </summary>
public enum DependencyKind { Required, Optional }

public sealed record ModuleDependency(string ModuleId, DependencyKind Kind);

/// <summary>BuiltIn = Pane 自带；Community = 社区通过 PR 贡献、随版本发布（「能力」页标出作者）。</summary>
public enum ModuleSource { BuiltIn, Community }

/// <param name="Glyph">Lucide 图标名。</param>
/// <param name="DefaultEnabled">新装用户的默认值。</param>
/// <param name="Author">Community 才有：GitHub 用户名。</param>
public sealed record ModuleInfo(
    string Id,
    ModuleCategory Category,
    string Glyph,
    bool DefaultEnabled,
    IReadOnlyList<ModuleDependency> Dependencies,
    ModuleSource Source = ModuleSource.BuiltIn,
    string? Author = null);

public enum ModuleStatus
{
    /// <summary>启用并在运行。</summary>
    Running,
    /// <summary>用户停用了。</summary>
    Disabled,
    /// <summary>用户启用了，但启动失败（钩子装不上等）；Problem 写一句给用户看的原因。其他能力不受影响。</summary>
    Failed,
    /// <summary>改了启用状态，要重启 Pane 才生效。</summary>
    RestartPending,
}

/// <param name="Enabled">用户的选择。</param>
/// <param name="Problem">Failed / RestartPending 时给用户看的一句话（进资源）。</param>
public sealed record ModuleState(string Id, bool Enabled, ModuleStatus Status, string? Problem = null);

/// <summary>
/// 启用 / 停用一项会连带什么。界面先 Preview，有连带变化时弹确认，用户同意再 Apply。
/// </summary>
/// <param name="AlsoEnable">启用时一起启用的硬依赖。</param>
/// <param name="AlsoDisable">停用时一起停用的（硬依赖了它的能力）。</param>
/// <param name="RestartRequired">这次改动有一项做不到即时生效。</param>
/// <param name="RestoresSystemSetting">停用会先恢复一项系统设置（剪贴板停用时恢复「替换 Win+V」），界面要在确认里说明。</param>
public sealed record ModuleChange(
    string Id,
    bool Enable,
    IReadOnlyList<string> AlsoEnable,
    IReadOnlyList<string> AlsoDisable,
    bool RestartRequired,
    bool RestoresSystemSetting);

public interface IModuleCatalog
{
    /// <summary>全部能力，按「能力」页的显示顺序。</summary>
    IReadOnlyList<ModuleInfo> Modules { get; }

    ModuleState StateOf(string id);

    /// <summary>正在运行（Running）。界面用它决定联动入口显不显示，例如剪贴板的「交给启动器」。</summary>
    bool IsActive(string id);

    /// <summary>只计算、不改任何东西。</summary>
    ModuleChange Preview(string id, bool enable);

    /// <summary>
    /// 照 Preview 的结果执行：写设置、启动或停止模块。停止要把这块的钩子、热键、线程、窗口都释放掉。
    /// 某块启动失败不抛异常，状态记成 Failed。
    /// </summary>
    Task ApplyAsync(ModuleChange change, CancellationToken cancellationToken);

    /// <summary>任意一项状态变了（参数是 Id）。可能在后台线程触发，界面自己切回界面线程。</summary>
    event EventHandler<string>? Changed;
}
