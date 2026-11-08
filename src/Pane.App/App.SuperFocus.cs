// 超级面板不抢焦点（设计负责人，2026-11-08 用户定，079）。docs/design.md「超级面板不抢焦点、剪贴板配合启动器、动画不被重建截断」。
// 照剪贴板（059）：不激活显示，选中文字的原窗口一直保有焦点；← → 1～6 Enter Ctrl+C Ctrl+R Tab Esc 由 IPanelKeyRouter 拦下转给面板。
// 超级面板没有输入框，没有「打字切到激活」。接管不了（目标权限更高、钩子挂不上、前台是 Pane 自己、演示模式）就照原来激活打开。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    GlassPanel? superWiredPanel;

    /// <summary>以不激活方式在 (x, y) 显示超级面板；不能接管时返回 false，调用方照原来激活显示。</summary>
    bool ShowSuperPassive(GlassPanel panel,int x,int y)
    {
        if(demo||Environment.GetEnvironmentVariable("PANE_VERIFY")=="1"||panelKeys is not {} keys||panel.View is not SuperView)return false;
        nint foreground=Native.GetForegroundWindow();
        if(foreground==0)return false;
        GetWindowThreadProcessId(foreground,out uint pid);
        if(pid==(uint)Environment.ProcessId)return false;
        if(!keys.Begin(foreground,PanelKeyMode.Super))return false;
        if(!ReferenceEquals(superWiredPanel,panel))
        {
            superWiredPanel=panel;
            panel.Hiding+=(_,_)=>{if(panels.TryGetValue("clipboard",out var clip)&&clip.Shown&&clip.Passive)return;panelKeys?.End();};
        }
        panel.Show(true,x,y,activate:false,passive:true);
        return true;
    }

    /// <summary>不激活打开的超级面板收到拦下的键；不是它在接管时返回 false。</summary>
    bool SuperPanelKey(PanelKeyPress press)
    {
        if(!panels.TryGetValue("super",out var panel)||!panel.Shown||!panel.Passive||panel.View is not SuperView view)return false;
        view.HandleKey(press);
        return true;
    }

    /// <summary>剪贴板或超级面板任一启用时需要按键接管服务；两个都停用才释放。</summary>
    void EnsurePanelKeys(){if(!demo)PanelKeys??=new Pane.Services.PanelKeyRouter();}
    void ReleasePanelKeys(bool clipboardActive,bool superActive)
    {
        panelKeys?.End();
        if(clipboardActive||superActive)return;
        var keys=panelKeys;PanelKeys=null;(keys as IDisposable)?.Dispose();
    }
}
