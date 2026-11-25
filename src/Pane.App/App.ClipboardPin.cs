// 剪贴板钉住（设计负责人，2026-11-25 用户定）。docs/design.md「剪贴板钉住与拖动」。
// 钉住后失焦、点外面、粘贴都不收起：宿主 GlassPanel.Pinned 读视图的 Pinned；粘贴之后、点了外面、前台换了窗口时放开键盘
// （结束按键接管、切回不激活方式），贴进最近一次在前台的非 Pane 窗口（GlassPanel.Previous 跟着前台走）。收起就取消钉住。
using Pane.Hosting;
using Pane.Views;
namespace Pane;
public sealed partial class App
{
    /// <summary>钉住的剪贴板：别的面板（启动器、超级面板、托盘菜单、文件夹跳转、二级窗口）打开时不跟着收起。</summary>
    static bool KeepPinned(GlassPanel panel)=>panel.Shown&&panel.View is ClipboardView{Pinned:true};

    /// <summary>剪贴板模块装配时接上（App.Modules）。</summary>
    void WireClipboardPin(GlassPanel panel,ClipboardView view)
    {
        panel.Pinned=()=>view.Pinned;
        view.PasteTarget=()=>panel.Previous;
        panel.LeftWhilePinned+=(_,_)=>ReleaseClipboardKeys(view);
        view.PastedWhilePinned+=(_,_)=>{ReleaseClipboardKeys(view);panel.ReleaseKeyboard();};
        // 首次以激活方式打开也可能在钉住后放开键盘，点搜索框时照常重新激活。
        view.EngageRequested+=(_,_)=>{EndClipboardKeys();EngageClipboard(panel,view);};
        panel.Hiding+=(_,_)=>{EndClipboardKeys();view.Passive=false;view.SetPinned(false,notify:false);};
    }

    // 放开键盘：↑↓、Enter、Esc 回到前台窗口；之后面板只用鼠标，点搜索框再切到激活方式（EngageRequested）
    void ReleaseClipboardKeys(ClipboardView view){EndClipboardKeys();view.Passive=true;}
}
