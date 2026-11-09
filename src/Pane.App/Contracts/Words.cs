// 分词（2026-11-09 设计定义，093）：分词窗口（界面）和分词服务之间的接口。
// 规则见 docs/design.md「分词」，原型 prototype/words.html；系统接口实测见 docs/research/092-分词与OCR系统接口实测.md。
namespace Pane.Contracts;

/// <summary>
/// 一块的类型。Word 普通词（含 4 位以内的纯数字、C# / C++ 这类）；Punctuation 标点和符号（界面不带底，拖选默认跳过）；
/// Link / Email / Phone 整块；Number 是日期时间、金额、百分数、订单号、5 位以上的数字这类整块。
/// </summary>
public enum WordKind { Word, Punctuation, Link, Email, Phone, Number }

/// <param name="Text">这一块的原文（不做归一化，不用 AlternateText 替换）。</param>
/// <param name="Start">在原文里的位置（UTF-16 下标）。</param>
/// <param name="Length">原文里的长度；Text == source.Substring(Start, Length)。</param>
public sealed record WordPiece(string Text, WordKind Kind, int Start, int Length);

/// <param name="Source">拆的那段原文（Truncated 时是截断后的那段）。</param>
/// <param name="Pieces">按原文顺序；空白（空格、制表、换行）不成块，界面从相邻两块之间的原文里取回空白和换行。
/// 所有块按顺序、连同它们之间的空白拼起来就是 Source。</param>
/// <param name="Truncated">原文超过 <see cref="ITextSegmenter.MaxLength"/>，只拆了前面这段。</param>
public sealed record WordSplit(string Source, IReadOnlyList<WordPiece> Pieces, bool Truncated);

public interface ITextSegmenter
{
    /// <summary>一次最多拆这么多个 UTF-16 字符（092：2 万字约 16 ms）；更长的截断，截断不切开代理对。</summary>
    const int MaxLength = 20000;

    /// <summary>
    /// 在后台线程拆，不阻塞界面。空字符串或只有空白返回空的 Pieces。取消时抛 OperationCanceledException。
    /// 拆不了（系统分词器创建失败等）时退回「只认整块和标点、其余按连续的字母数字 / 单个汉字切」，不抛异常。
    /// </summary>
    Task<WordSplit> SplitAsync(string text, CancellationToken cancellationToken);
}
