// 超级面板：选中文字的读取和对它的动作（设计负责人定义，工程实现）。
// 读取规则见 docs/design.md「超级面板取选中内容」：先 UI Automation，读不到再借用剪贴板（拍快照、模拟复制、恢复）。
namespace Pane.Contracts;

/// <summary>
/// Empty：读到了，但选区是空的；Unsupported：这个程序既不给直接读、模拟复制也没拿到文字；
/// ClipboardUnavailable（019 新增）：直接读不到，本该借用剪贴板，但剪贴板被占用或原内容暂存不下来，为了不弄丢它没有借用。
/// </summary>
public enum SelectionFailure { None, Empty, Unsupported, Elevated, Timeout, ClipboardUnavailable }

/// <param name="Text">读到的文字；失败时为 null。</param>
/// <param name="SourceWindow">选区所在的顶层窗口（粘贴替换时切回它）。</param>
public sealed record SelectionResult(string? Text, nint SourceWindow, SelectionFailure Failure)
{
    /// <summary>
    /// 选中的是文件（040，设计加）：前台是资源管理器或桌面、且选中了文件或文件夹时给完整路径（按资源管理器里的显示顺序），
    /// 这时 Text 为 null、Failure 为 None，超级面板换成文件动作。其他情况为 null。
    /// </summary>
    public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>读资源管理器 / 桌面里选中的文件（040，设计加），不借用剪贴板，不需要那个窗口在前台。</summary>
public interface IFileSelection
{
    /// <summary><paramref name="window"/> 是资源管理器窗口或桌面时，返回它当前选中的文件和文件夹（显示顺序）；不是、或没选中时返回空。</summary>
    Task<IReadOnlyList<string>> ReadAsync(nint window, CancellationToken cancellationToken);
}

public interface ISelectionReader
{
    /// <summary>
    /// 读当前前台窗口的选中文字。必须在超级面板显示、抢走焦点之前调用：
    /// 宿主收到「长按侧键」后先读，再把结果交给 SuperView.SetSelection，然后显示面板。
    /// </summary>
    Task<SelectionResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>超级面板里非 AI 的动作。</summary>
public interface ISelectionActions
{
    /// <summary>用默认搜索站点（设置 → 启动器 → 网页搜索）打开。</summary>
    Task SearchWebAsync(string text, CancellationToken cancellationToken);

    /// <summary>写入剪贴板（作为一次普通复制，会进 Pane 的剪贴板历史）。</summary>
    Task CopyAsync(string text, CancellationToken cancellationToken);

    /// <summary>切回 <paramref name="window"/>，用 <paramref name="text"/> 替换原来的选中文字（粘贴）。</summary>
    Task ReplaceSelectionAsync(string text, nint window, CancellationToken cancellationToken);
}
