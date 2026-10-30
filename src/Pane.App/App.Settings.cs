// 设置页要用、又不属于某个面板的服务入口（设计负责人）。
using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    IAppIconCatalog? iconSample;
    /// <summary>设置页「应用图标」：已索引的应用和它们现在的图标。演示模式用假数据。</summary>
    public IAppIconCatalog AppIcons => (IAppIconCatalog?)launcherSearch ?? (iconSample ??= new Pane.DesignData.AppIconsSample());

    IWinVReplacement? winVSample;
    /// <summary>设置页「用 Pane 替换 Win+V」的真实实现，工程在 030 里赋值。</summary>
    public IWinVReplacement? WinVService { get; set; }
    /// <summary>设置页用：真实服务；演示模式用假服务；正式版没有真实服务时为 null，设置页不显示这一项。</summary>
    public IWinVReplacement? WinV => WinVService ?? (demo ? winVSample ??= new Pane.DesignData.WinVSample() : null);
}
