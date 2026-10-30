using Pane.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
namespace Pane;
public sealed partial class App {
    Window? background;
    bool wallpaperReady;
    void StartBackground(){
        StartPositions();
        var wallpaper=Environment.GetEnvironmentVariable("PANE_WALLPAPER");
        if(!demo||string.IsNullOrEmpty(wallpaper)){wallpaperReady=true;return;}
        background=new Window{Title="Pane 011 实验背景"};
        var svg=new SvgImageSource(new Uri(wallpaper));svg.Opened+=(_,_)=>wallpaperReady=true;svg.OpenFailed+=(_,_)=>File.WriteAllText(output+".error","背景加载失败");
        background.Content=new Image{Source=svg,Stretch=Stretch.Fill};var presenter=OverlappedPresenter.Create();presenter.SetBorderAndTitleBar(false,false);background.AppWindow.SetPresenter(presenter);
        var monitor=GlassPanel.MonitorFromPoint(new GlassPanel.Point(),2);var info=new GlassPanel.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<GlassPanel.MonitorInfo>()};GlassPanel.GetMonitorInfoW(monitor,ref info);var r=info.Monitor;
        background.AppWindow.MoveAndResize(new RectInt32(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top));var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(background);Native.ShowWindow(hwnd,4);Native.SetWindowPos(hwnd,-1,0,0,0,0,0x13);
    }
}
