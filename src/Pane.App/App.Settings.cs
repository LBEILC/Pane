// 设置页要用、又不属于某个面板的服务入口（设计负责人）。
using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    IAppIconCatalog? iconSample;
    /// <summary>设置页「应用图标」：已索引的应用和它们现在的图标。演示模式用假数据。</summary>
    public IAppIconCatalog AppIcons => (IAppIconCatalog?)launcherSearch ?? (iconSample ??= new Pane.DesignData.AppIconsSample());
}
