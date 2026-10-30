// 界面字体（设计负责人，2026-10-30）：MiSans 不随包，用户在 设置 → 通用 → 字体 里选本机字体。
// 字体是 XAML 的 StaticResource（PaneFontUi / PaneFontDisplay），在启动时、建任何窗口之前按设置改好；换了字体重启后生效。
// 选中的字体后面跟着回落链：没装或缺字时用微软雅黑 / Segoe。
using Microsoft.UI.Xaml.Media;
using Pane.Contracts;
using Pane.Hosting;
namespace Pane;
public sealed partial class App
{
    /// <summary>本机字体列表；工程在 024 里赋真实实现（DirectWrite），演示模式用假数据；没有时设置页让用户手填字族名。</summary>
    public IFontCatalog? Fonts {get;set;}
    /// <summary>这次启动实际用的界面字体（设置页据此判断「重启后生效」）。</summary>
    public string? AppliedUiFont {get;private set;}
    public const string DefaultUiFont="MiSans VF";
    void ApplyUiFont()
    {
        if(demo)Fonts??=new Pane.DesignData.FontCatalogSample();
        var name=Store.Current.General.UiFont is {Length:>0} f?f:DefaultUiFont;
        AppliedUiFont=Store.Current.General.UiFont;
        GlassParameters.SetFonts(new FontFamily($"{name}, Microsoft YaHei UI, Segoe UI Variable Text"),new FontFamily($"{name}, Microsoft YaHei UI, Segoe UI Variable Display"));
    }
}
