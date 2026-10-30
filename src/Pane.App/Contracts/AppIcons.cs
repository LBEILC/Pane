// 应用图标：设置页「应用图标」分区和启动器索引之间的接口。归设计负责人定义；由启动器搜索服务实现（LauncherSearch）。
// 规则见 docs/design.md「应用图标的显示」：每个应用可选 自动 / 强制遮罩 / 原样，也能换成自己的图片。
namespace Pane.Contracts;

/// <summary>遮罩分析的结果：满幅（裁成圆角方形）、彩色底板、中性底板、原样。</summary>
public enum AppIconKind { Unknown, Raw, Full, Plate, Neutral }

/// <param name="Icon">现在显示的图标（已按当前主题和遮罩处理）；没有就是 null。</param>
public sealed record AppIconInfo(string Id, string Name, string? Icon, AppIconKind Kind);

public interface IAppIconCatalog
{
    /// <summary>已索引的全部应用，按名称排序。</summary>
    IReadOnlyList<AppIconInfo> Apps();

    /// <summary>图标设置改了以后正在后台重新生成（辅助进程，十几秒）。</summary>
    bool Rebuilding { get; }

    /// <summary>重新生成完成、应用列表变化。可能在后台线程触发。</summary>
    event EventHandler? Changed;
}
