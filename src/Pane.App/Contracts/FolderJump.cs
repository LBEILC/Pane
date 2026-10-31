// 文件对话框里跳到文件夹（参考 Listary 的 Ctrl+G，2026-10-31 用户采纳）。归设计负责人定义；由工程实现（038）。
// 交互（docs/design.md「文件对话框跳转」）：
// - 「打开 / 另存为」对话框在前台时按 Ctrl+G：直接跳到最近激活过的资源管理器窗口所在的文件夹；没有开着的资源管理器就弹出下面的菜单。
// - Ctrl+Shift+G：在对话框旁边弹出菜单，列出开着的资源管理器文件夹和最近用过的文件夹，选一个跳过去。
// - 只在文件对话框前台时接管这两个组合键，其他程序里的 Ctrl+G 不受影响。
namespace Pane.Contracts;

public enum FolderJumpSource { Explorer, Recent }

/// <param name="Path">完整路径。</param>
/// <param name="Name">显示名（文件夹名；盘符根目录显示「本地磁盘 (D:)」这类系统名称）。</param>
public sealed record FolderJumpEntry(string Path, string Name, FolderJumpSource Source);

/// <param name="Dialog">文件对话框的顶层窗口。</param>
/// <param name="X">菜单锚点（物理像素，屏幕坐标）：对话框文件名输入框的左下角。</param>
/// <param name="Y">同上。</param>
public sealed record FolderJumpRequest(nint Dialog, int X, int Y);

public interface IFolderJump
{
    /// <summary>用户在文件对话框里按了 Ctrl+Shift+G，或按了 Ctrl+G 但没有可直接跳的资源管理器窗口。可能在后台线程触发。</summary>
    event EventHandler<FolderJumpRequest>? MenuRequested;

    /// <summary>菜单要列的内容：开着的资源管理器文件夹在前（最近激活的排第一），再是最近用过的文件夹（去重，最多 8 个）。</summary>
    IReadOnlyList<FolderJumpEntry> Entries();

    /// <summary>让对话框跳到这个文件夹；对话框文件名框里原来的文字保留。对话框已经关了就什么都不做。</summary>
    Task JumpAsync(nint dialog, string path, CancellationToken cancellationToken);
}
