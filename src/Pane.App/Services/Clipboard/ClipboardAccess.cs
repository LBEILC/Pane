using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Clipboard;

internal static class ClipboardAccess
{
    // TODO(design): 当前手势模型没有「打开剪贴板」动作；定义后应接入 show-clipboard 的统一入口。
    internal const string DisabledNotice="剪贴板已关闭，可以在 设置 → 剪贴板 里打开";
    internal static bool Allow(string command,bool enabled,Action<string> notify)
    {
        if(command!="show-clipboard"||enabled)return true;
        notify(DisabledNotice);return false;
    }
    internal static string Cycle(int index,bool enabled)=>enabled
        ? new[]{"launcher","clipboard","super"}[index%3]
        : new[]{"launcher","super"}[index%2];
}

// 只给启动器的附件读取使用；设置页仍持有完整的历史服务。
internal sealed class LauncherClipboardHistory(IClipboardHistory history,SettingsStore settings):IClipboardHistory
{
    public IReadOnlyList<ClipEntry> Snapshot()=>settings.Current.Clipboard.Enabled?history.Snapshot():[];
    public event EventHandler? Changed {add=>history.Changed+=value;remove=>history.Changed-=value;}
    public Task PasteAsync(string id,bool plainText,nint targetWindow,CancellationToken cancellationToken)=>history.PasteAsync(id,plainText,targetWindow,cancellationToken);
    public void SetPinned(string id,bool pinned)=>history.SetPinned(id,pinned);
    public void Delete(string id)=>history.Delete(id);
    public void ClearUnpinned()=>history.ClearUnpinned();
}
