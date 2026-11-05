// 更新说明（issue 25、062）：每版一份 JSON（src/Pane.App/ReleaseNotes/），随安装包打包，关于页的更新内容、更新日志和呼出时的提示条共用。
// 归设计负责人定义；由工程实现读取、打包时盖版本号、生成 Release 正文和解析（062）。规则见 docs/design.md「更新说明与更新提示」。
namespace Pane.Contracts;

/// <summary>新增 / 改进 / 修复。界面按这个顺序分组。</summary>
public enum ReleaseNoteKind { New, Improved, Fixed }

/// <param name="Text">一条一件事，直接给用户看的一句话。</param>
/// <param name="Highlight">这一版最值得说的（1～3 条），「更新后」提示条用第一条。</param>
public sealed record ReleaseNoteItem(ReleaseNoteKind Kind, string Text, bool Highlight = false);

/// <param name="Version">如 0.1.366。</param>
/// <param name="Date">发布日期；解析不出时为 null。</param>
public sealed record ReleaseNotes(string Version, DateOnly? Date, IReadOnlyList<ReleaseNoteItem> Items);

/// <summary>随安装包带着的历代更新说明（离线可读，不联网）。</summary>
public interface IReleaseNotesCatalog
{
    /// <summary>全部版本，新的在前。读不到时为空列表，不抛异常。</summary>
    IReadOnlyList<ReleaseNotes> All { get; }

    /// <summary>某一版的说明；没有时为 null。版本号按数字逐段比较（0.1.36 和 0.1.036 相同）。</summary>
    ReleaseNotes? Find(string version);
}
