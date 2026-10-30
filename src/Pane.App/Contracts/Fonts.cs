// 本机字体列表：设置 → 通用 → 字体 用它让用户选界面字体。归设计负责人定义，工程照着实现（DirectWrite 系统字体集合，docs/specs/024）。
// 2026-10-30 用户定：MiSans 不随包，要有地方让用户自己选本机的字体。界面开发期间用 DesignData/FontCatalogSample。
namespace Pane.Contracts;

/// <param name="Name">字族名，写进设置、交给 XAML 的 FontFamily 用（英文名优先，例如 "MiSans VF"、"Microsoft YaHei UI"）。</param>
/// <param name="DisplayName">给用户看的名字：有中文名用中文名（如「微软雅黑 UI」），没有就同 Name。</param>
/// <param name="HasChinese">能显示常用汉字（界面主要是中文；不能显示的会被系统用别的字体顶上，观感不统一）。</param>
public sealed record FontFamilyInfo(string Name, string DisplayName, bool HasChinese);

public interface IFontCatalog
{
    /// <summary>
    /// 本机已安装的字体（含当前用户安装的），按 DisplayName 排序、去重。不含只有符号的字体（Wingdings、Segoe MDL2 Assets 这类）。
    /// 第一次调用可以慢一点（几十毫秒），之后用缓存；在后台线程也能调。
    /// </summary>
    Task<IReadOnlyList<FontFamilyInfo>> GetFamiliesAsync(CancellationToken cancellationToken);
}
