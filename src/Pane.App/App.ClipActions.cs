// 剪贴板操作列表里交给宿主的事、拖动改高度（设计负责人，2026-11-23）。docs/design.md「剪贴板操作列表、富文本、标签、编辑、高度」。
// 打开、在资源管理器中显示、另存为图片借用启动器的附件动作（同一个 ClipEntry）；启动器能力停用时这几项不出现（FileActionsAvailable）。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Localization;
using Pane.Views;

namespace Pane;

public sealed partial class App
{
    void WireClipboardActions(GlassPanel panel, ClipboardView view)
    {
        view.HeightDragged += (_, a) => panel.ResizeTo(a.Height, a.FromTop);
        view.FileActionsAvailable = () => demo || launcherSearch != null;
        view.ActionRequested += async (_, x) =>
        {
            try
            {
                if (x.Action == "open-link")
                {
                    // 只打开 http / https 网址，别的协议不交给系统
                    if (Uri.TryCreate(x.Entry.Text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    return;
                }
                ILauncherAttachmentActions? actions = demo ? new Pane.DesignData.LauncherAttachmentActionsSample()
                    : launcherSearch is { } search ? new Pane.Services.Clipboard.ClipboardAttachmentActions(search) : null;
                if (actions is null) return;
                await actions.RunAsync(x.Action == "save-as" ? "save-image" : x.Action, x.Entry, CancellationToken.None);
            }
            catch (Exception e) { ReportFailure(x.Action == "save-as" ? Strings.Common_SaveAs : Strings.Common_Open, e); }
        };
    }
}
