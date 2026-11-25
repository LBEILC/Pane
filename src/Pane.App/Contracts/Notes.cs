// 备忘录（2026-11-25，第一期「随手记」）：界面和备忘录存储之间的接口。归设计负责人定义，工程照着实现（任务 123；同步 124）。
// 交互见 docs/design.md「备忘录」，原型 prototype/memo.html。
namespace Pane.Contracts;

/// <summary>
/// 一条备忘录。Id：16 字节随机数的 base64url（22 个字符），同步时就是记录 id。Text：正文，第一行就是标题。
/// Created / Modified：创建、最后修改时间（只在正文或固定变了时更新）。From：同步冲突时另存出来的副本，写来源电脑名；其余为 null。
/// </summary>
public sealed record Note(string Id, string Text, bool Pinned, DateTimeOffset Created, DateTimeOffset Modified, string? From = null)
{
    /// <summary>标题：第一行去掉开头的 # 和首尾空白；为空时界面显示「无标题」。</summary>
    public string Title
    {
        get
        {
            int end = Text.IndexOf('\n');
            return (end < 0 ? Text : Text[..end]).TrimStart('#').Trim();
        }
    }
}

/// <summary>搜索结果：Snippet 是正文里命中处前后的一段（命中在标题时是正文开头），高亮按 UTF-16 下标。</summary>
public sealed record NoteMatch(Note Note, IReadOnlyList<TextSpan>? TitleHighlights, string Snippet, IReadOnlyList<TextSpan>? SnippetHighlights);

/// <summary>
/// 备忘录存储：每条一个 .md 文件（只有正文），放在「大文件位置」下的 备忘录 文件夹，固定、时间等记在同目录的隐藏索引里。
/// 方法都可以在界面线程调用：读的直接返回内存里的；写盘放后台、不阻塞。
/// </summary>
public interface INoteStore
{
    /// <summary>全部备忘录：固定的在前，再按 Modified 新的在前。</summary>
    IReadOnlyList<Note> All { get; }

    /// <summary>列表变了（外部编辑器改了文件、同步进来、别处「存为备忘录」）。可能在后台线程触发，界面自己切回 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>新建一条并写盘（正文不能全是空白，否则抛 ArgumentException）。窗口里新建的空白备忘录由界面先放在内存里，第一次有字时才调这个。</summary>
    Note Add(string text);

    /// <summary>改正文。内容没变时什么都不做；变了更新 Modified、写盘、标题变了顺便改文件名。返回最新的；这条已经不在了返回 null。</summary>
    Note? Update(string id, string text);

    void SetPinned(string id, bool pinned);

    /// <summary>删除：文件放进系统回收站。</summary>
    void Delete(string id);

    /// <summary>撤销删除（界面的撤销条，5 秒内）：按原 id、原内容、原时间写回。</summary>
    void Restore(Note note);

    /// <summary>搜标题和正文（不分大小写；标题另认拼音全拼 / 首字母），标题命中的在前，再按修改时间。</summary>
    IReadOnlyList<NoteMatch> Search(string query, int max);

    /// <summary>文件夹的完整路径（设置里显示、「打开文件夹」用）。</summary>
    string Folder { get; }

    /// <summary>重新扫描文件夹（打开窗口时调）：外部改过、新加、删掉的文件对上之后发 Changed。在后台做，不阻塞。</summary>
    void Refresh();
}
