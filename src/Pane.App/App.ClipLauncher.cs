using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;

namespace Pane;

// 剪贴板配合启动器（设计，2026-11-08 用户定）：启动器开着时按剪贴板热键，启动器不收起、剪贴板叠在上面（激活方式）；
// 在剪贴板里选一条（Enter、点、Alt+数字）交给启动器：单行短文字填进输入框，别的当附件。剪贴板收起后焦点回到启动器。
public sealed partial class App
{
    bool clipboardOverLauncher;

    /// <summary>要叠在启动器上打开时返回启动器面板（呼出时不收起它），否则 null。</summary>
    GlassPanel? ClipboardOverLauncher(GlassPanel clipboard)
    {
        bool over = clipboard.View is ClipboardView && !clipboard.Shown && ModuleGate.On(ModuleIds.Launcher)
            && panels.TryGetValue("launcher", out var launcher) && launcher.Shown && ReferenceEquals(current, launcher);
        if (clipboard.View is ClipboardView view) view.ForLauncher = over;
        clipboardOverLauncher = over;
        return over ? panels["launcher"] : null;
    }

    /// <summary>剪贴板模块装配时接上（App.Modules）。</summary>
    void WireClipboardLauncher(GlassPanel panel, ClipboardView view)
    {
        view.UseInLauncher += (_, entry) =>
        {
            launcherView?.Use(entry);
            panel.Hide();   // Hiding 里把焦点还给启动器
        };
        panel.Hiding += (_, _) =>
        {
            if (!clipboardOverLauncher) return;
            clipboardOverLauncher = false;
            view.ForLauncher = false;
            if (panels.TryGetValue("launcher", out var launcher) && launcher.Shown) { current = launcher; launcher.Refocus(); }
        };
    }
}
