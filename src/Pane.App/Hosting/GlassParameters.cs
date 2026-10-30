using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pane.Settings;
using Windows.UI.ViewManagement;
namespace Pane.Hosting;
public sealed class GlassParameters : IDisposable
{
    readonly UISettings system=new();
    readonly Microsoft.UI.Dispatching.DispatcherQueue queue=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    public GeneralSettings Current {get; private set;}
    public bool Dark => Current.Theme==ThemeMode.Dark || Current.Theme==ThemeMode.System && system.GetColorValue(UIColorType.Foreground).R>128;
    public bool Solid => !system.AdvancedEffectsEnabled || Environment.GetEnvironmentVariable("PANE_FORCE_SOLID")=="1";
    public GlassThemeParameters Active => Dark?Current.GlassDark:Current.GlassLight;
    public event EventHandler? Changed;
    public static ResourceDictionary Tokens {get;private set;}=null!;
    public static void InitializeResources(ResourceDictionary resources) { Tokens=resources.MergedDictionaries[1]; }
    public static object Resource(string key)=>Tokens[key];
    /// <summary>界面字体（设计加，2026-10-30）：启动时、建窗口之前按设置覆盖令牌里的字体；代码里建的文字用 <see cref="UiFont"/>。
    /// Tokens.xaml 是带 Source 合并进来的，不能写值；写到应用的根字典里，根字典自己的键优先于合并字典，之后建的窗口和样式都会查到这里。</summary>
    public static void SetFonts(Microsoft.UI.Xaml.Media.FontFamily ui,Microsoft.UI.Xaml.Media.FontFamily display){var r=Application.Current.Resources;r["PaneFontUi"]=ui;r["PaneFontDisplay"]=display;r["PaneFontMono"]=new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, "+ui.Source);UiFont=ui;}
    public static Microsoft.UI.Xaml.Media.FontFamily UiFont {get;private set;}=new("MiSans VF, Microsoft YaHei UI, Segoe UI Variable Text");
    public static double Token(string key)=>(double)Resource(key);
    public static PaneSettings Defaults() {
        var glass=new GlassThemeParameters(Token("PaneGlassTintOpacity"),Token("PaneGlassLumOpacity"),Token("PaneGlassSaturation"),Token("PaneGlassHighlight"),Token("PaneGlassShadow"),Token("PaneGlassExtraBlur"));
        return new(1,new(false,true,ThemeMode.System,Resource("PaneAccentColor").ToString()!,Token("PaneRadiusPanelValue"),1,glass,glass),new(new(KeyModifiers.Alt,32)),new(new(KeyModifiers.Alt,86)));
    }
    public GlassParameters(GeneralSettings settings) { Current=settings;system.ColorValuesChanged+=SystemChanged;system.AdvancedEffectsEnabledChanged+=SystemChanged; Apply(settings); }
    void SystemChanged(UISettings sender,object args)=>queue.TryEnqueue(()=>Changed?.Invoke(this,EventArgs.Empty));
    public void Apply(GeneralSettings settings) {
        Current=settings;var r=Application.Current.Resources;uint argb=Convert.ToUInt32(settings.AccentColor[1..],16);
        var color=Windows.UI.Color.FromArgb((byte)(argb>>24),(byte)(argb>>16),(byte)(argb>>8),(byte)argb);
        r["PaneAccentColor"]=color;r["PaneAccentBrush"]=new SolidColorBrush(color);
        r["PaneRadiusPanelValue"]=settings.PanelRadius;r["PaneRadiusPanel"]=new CornerRadius(settings.PanelRadius);
        r["PaneRadiusItem"]=new CornerRadius(Math.Max(4,settings.PanelRadius-8));r["PaneRadiusControl"]=new CornerRadius(Math.Max(4,settings.PanelRadius-10));
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Dispose(){system.ColorValuesChanged-=SystemChanged;system.AdvancedEffectsEnabledChanged-=SystemChanged;}
}
