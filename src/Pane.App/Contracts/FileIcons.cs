// 文件的系统图标：文件窗口、启动器文件行、预览缩略位在「设置 → 外观 → 文件图标：系统」时用。归设计负责人定义；由工程实现（036）。
// 默认仍是 Pane 自己的彩色类型图标；用户反馈两套图标并存有割裂感，所以给一个跟系统一致的选项（2026-10-31）。
namespace Pane.Contracts;

public interface IFileIcons
{
    /// <summary>
    /// 已经缓存好就立刻返回 PNG 路径（64 px，透明底，主题无关）；还没有就返回 null，并在后台提取，好了发 <see cref="Ready"/>。
    /// 普通文件按扩展名共用一张；.exe / .lnk / .ico / .url / .appref-ms 和文件夹（有自定义图标时）按路径各取各的。界面线程调用，不得阻塞。
    /// </summary>
    string? TryGet(string path, bool folder);

    /// <summary>某个路径的图标提取好了（参数是 <see cref="TryGet"/> 传进来的路径）。可能在后台线程触发。</summary>
    event EventHandler<string>? Ready;
}
