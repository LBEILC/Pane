// 托盘菜单（设计负责人，2026-10-30）：右键托盘图标弹出玻璃小菜单，替代 Windows 原生菜单。原型 prototype/tray.html。
// 托盘线程在右键那一刻取鼠标位置，连同命令「tray-menu x,y」交过来；菜单第一次用到时才建，之后复用。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
using Pane.Views.Controls;
namespace Pane;
public sealed partial class App
{
    TrayMenuView? trayView;
    GlassPanel? trayPanel;
    void ShowTrayMenu(string point)
    {
        var parts=point.Split(',');
        if(parts.Length!=2||!int.TryParse(parts[0],out var x)||!int.TryParse(parts[1],out var y))return;
        if(trayPanel==null)
        {
            trayView=new TrayMenuView();trayView.Invoked+=(_,id)=>TrayInvoked(id);
            trayPanel=new GlassPanel("托盘菜单",trayView,trayView,Glass){Placement=PanelPlacement.Tray};
            WatchFrames(trayPanel);trayPanel.Prepare();panels.Add("tray",trayPanel);
        }
        var u=Updates;
        var pause=gestureService!=null?GestureConfig.Settings.PauseHotkey:null;
        trayView!.Apply(new TrayMenuState(
            u is {State:UpdateState.Ready,Latest:{} r}?r.Version:null,
            HotkeyBox.Names(Store.Current.Launcher.Hotkey).ToList(),
            HotkeyBox.Names(Store.Current.Clipboard.Hotkey).ToList(),
            gestureService!=null,gestureService?.IsPaused??false,
            pause is {} k?HotkeyBox.Names(new Settings.Hotkey((Settings.KeyModifiers)(int)k.Modifiers,k.VirtualKey)).ToList():[]));
        foreach(var other in panels.Values)if(other!=trayPanel)other.Hide();
        current=trayPanel;
        trayPanel.Show(true,x,y,!demo);
    }
    void TrayInvoked(string id)
    {
        switch(id)
        {
            case "update":Updates?.InstallAndRestart();break;
            case "launcher":Command("show-launcher");break;
            case "clipboard":Command("show-clipboard");break;
            case "settings":Command("show-settings");break;
            case "pause":Command("pause-gestures");break;
            case "exit":Command("exit");break;
        }
    }
}
