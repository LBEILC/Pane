// 剪贴板不抢焦点（设计负责人，issue 20、059）。docs/design.md「剪贴板不抢焦点」。
// 剪贴板默认以不激活方式打开：原窗口一直保有焦点（重命名框、地址栏不退出编辑），面板开着时导航 / 粘贴键由 IPanelKeyRouter 拦下转过来；
// 打字或点搜索框时切到激活方式。接管不了（目标权限更高、钩子挂不上、前台是 Pane 自己、演示模式）就照原来激活打开。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    IPanelKeyRouter? panelKeys;
    /// <summary>剪贴板面板开着时的按键接管（059）。剪贴板能力启动时由工程赋值，停止时置 null；为 null 时剪贴板照原来激活方式打开。</summary>
    public IPanelKeyRouter? PanelKeys
    {
        get=>panelKeys;
        set
        {
            if(ReferenceEquals(panelKeys,value))return;
            if(panelKeys!=null){panelKeys.Pressed-=OnPanelKey;panelKeys.End();}
            panelKeys=value;
            if(value!=null)value.Pressed+=OnPanelKey;
        }
    }

    // 按键接管现在归剪贴板（2026-11-25）：钉住的剪贴板放开键盘后和超级面板可能同时开着，结束接管前先看是不是自己的
    bool clipboardKeys;
    void EndClipboardKeys(){if(!clipboardKeys)return;clipboardKeys=false;panelKeys?.End();}

    /// <summary>以不激活方式显示剪贴板；不能接管时返回 false，调用方照原来激活显示。</summary>
    bool ShowClipboardPassive(GlassPanel panel)
    {
        if(demo||panelKeys is not {} keys||panel.View is not ClipboardView view)return false;
        nint foreground=Native.GetForegroundWindow();
        if(foreground==0)return false;
        GetWindowThreadProcessId(foreground,out uint pid);
        if(pid==(uint)Environment.ProcessId)return false;   // 从启动器等 Pane 自己的窗口切过来
        if(!keys.Begin(foreground,ModuleGate.On(ModuleIds.Launcher))){view.Passive=false;return false;}
        clipboardKeys=true;
        view.Passive=true;
        panel.Show(activate:false,passive:true);
        return true;
    }

    void EngageClipboard(GlassPanel panel,ClipboardView view)
    {
        view.Passive=false;
        panel.Engage();
        view.FocusSearch();
    }

    void OnPanelKey(object? sender,PanelKeyPress press)
    {
        if(SuperPanelKey(press))return;   // 超级面板不激活打开时（079）
        if(!clipboardKeys||!panels.TryGetValue("clipboard",out var panel)||!panel.Shown||!panel.Passive||panel.View is not ClipboardView view){clipboardKeys=false;panelKeys?.End();return;}
        // 操作列表、标签、编辑（2026-11-23，106）：先切到激活方式，模式里要用到搜索框、数字键和编辑框
        if(press.Key is PanelKey.More or PanelKey.Tags or PanelKey.Edit){EndClipboardKeys();EngageClipboard(panel,view);view.HandleKey(press);return;}
        if(press.Key==PanelKey.Type)
        {
            // 先激活、聚焦搜索框，再让服务把排队的键重放进来（输入法照常组字）
            EngageClipboard(panel,view);
            clipboardKeys=false;panelKeys?.ReplayAndEnd();
            return;
        }
        view.HandleKey(press);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window,out uint pid);
}
