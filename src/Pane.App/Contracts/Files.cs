// 文件窗口（二级窗口）：界面和文件搜索服务之间的接口。归设计负责人定义，工程照着实现（Everything 1.4 IPC）。
// 依据 docs/design.md「启动器排法与二级窗口」→「文件窗口」。界面开发期间用 DesignData/FileSearchSample。
namespace Pane.Contracts;

/// <summary>文件窗口左侧的分类。按扩展名归类，清单见 docs/specs/021；文件夹单独一类；归不进去的只出现在「全部」。</summary>
public enum FileCategory { All, Folder, Document, Spreadsheet, Presentation, Pdf, Text, Image, Video, Audio, Archive, Program }

/// <summary>排序：相关度（Everything 的默认顺序，名字开头命中靠前）或修改时间（新的在前）。</summary>
public enum FileSort { Relevance, Modified }

public sealed record FileQuery(string Text, FileCategory Category, FileSort Sort);

/// <summary>
/// 一条结果。Name 是文件名（含扩展名），Path 是完整路径。
/// Size：文件夹为 null；Modified 取不到为 null。Highlights 是 Name 里命中的位置（按 UTF-16 下标）。
/// </summary>
public sealed record FileHit(string Path, string Name, FileCategory Category, long? Size, DateTimeOffset? Modified,
    IReadOnlyList<TextSpan>? Highlights = null);

/// <summary>
/// 一页结果。Total：当前分类下的总数（不只是这一页）。
/// Counts：同一个关键词下每个分类的数量（含 All）；取不到或还没算完时为 null，界面就不显示数字。
/// </summary>
public sealed record FilePage(IReadOnlyList<FileHit> Items, int Total, IReadOnlyDictionary<FileCategory, int>? Counts);

/// <summary>
/// 右侧预览要的、FileHit 里没有的信息。都可以为 null（取不到就不显示那一项）。
/// TextHead：文本类文件的前 40 行（自动识别 UTF-8 / UTF-16 / GBK，超过 4000 字截断；二进制文件为 null）。
/// ImageWidth/Height：图片的像素尺寸（只读文件头）。FolderItems：文件夹里直接包含的项数。
/// 图片缩略图界面自己按路径解码，不经过这里。
/// </summary>
public sealed record FilePreview(string? TextHead = null, int? ImageWidth = null, int? ImageHeight = null, int? FolderItems = null);

public interface IFileSearch
{
    /// <summary>
    /// 关键词、分类、排序变化时 offset = 0 调用（界面已防抖）；滚到接近底部时用下一个 offset 再要一批（每批 50）。
    /// 取消即放弃这次结果。空关键词返回空页。Everything 没运行抛 <see cref="FileSearchUnavailableException"/>。
    /// </summary>
    Task<FilePage> SearchAsync(FileQuery query, int offset, int count, CancellationToken cancellationToken);

    /// <summary>选中项变化时调用（界面已防抖）；读文件要有超时，不能卡住（单个文件最多约 200ms）。</summary>
    Task<FilePreview> PreviewAsync(FileHit hit, CancellationToken cancellationToken);

    /// <summary>用默认程序打开（文件夹用资源管理器打开）。</summary>
    Task OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>打开所在文件夹并选中它。</summary>
    Task RevealAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Everything 没运行或版本不支持；Message 是给用户看的一句话。</summary>
public sealed class FileSearchUnavailableException(string message) : Exception(message);
