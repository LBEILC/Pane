using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pane.Contracts;
using Windows.Data.Text;

namespace Pane.Services.Words;

internal sealed class TextSegmenter : ITextSegmenter
{
    // 顺序即优先级；时间区间必须先于单个时间，电话必须先于数字。
    internal const string EntityPattern = """
        (?<link>https?://[^\s，。！？；：<>"“”‘’]+)|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})|(?<phone>(?<!\d)(?:1\d{2}[ -]?\d{4}[ -]?\d{4}|0\d{2,3}-\d{7,8}|400-\d{3}-\d{4})(?!\d))|(?<date>\d{4}-\d{1,2}-\d{1,2}|\d{4} *年 *\d{1,2} *月 *\d{1,2} *日)|(?<time>\d{1,2}:\d{2}(?::\d{2})?(?:-\d{1,2}:\d{2}(?::\d{2})?)?)|(?<order>[A-Za-z]+\d+(?:-\d+)+)|(?<number>[¥￥$]?\d+(?:,\d{3})*(?:\.\d+)?%?)|(?<code>[A-Za-z]+(?:\+\+|#))
        """;
    static readonly Regex Entities = new(EntityPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    readonly Lazy<WordsSegmenter?> system;
    readonly object sync = new();

    internal TextSegmenter() : this(() => new WordsSegmenter("zh-Hans")) { }
    // 测试注入激活失败；失败结果也缓存，避免每次重复尝试激活。
    internal TextSegmenter(Func<WordsSegmenter> create) => system = new(() => { try { return create(); } catch (Exception) { return null; } });

    public Task<WordSplit> SplitAsync(string text, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        int length = Math.Min(text.Length, ITextSegmenter.MaxLength);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        string source = text[..length];
        List<WordPiece> pieces = [];
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmenter = system.Value;
            int cursor = 0;
            foreach (Match match in Entities.Matches(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Gap(cursor, match.Index, segmenter);
                int end = match.Index + match.Length;
                var kind = WordKind.Number;
                if (match.Groups["link"].Success)
                {
                    kind = WordKind.Link;
                    while (end > match.Index && ").,!?;:]}）】》〉」』".Contains(source[end - 1])) end--;
                }
                else if (match.Groups["email"].Success) kind = WordKind.Email;
                else if (match.Groups["phone"].Success) kind = WordKind.Phone;
                else if (match.Groups["code"].Success || match.Value.Length <= 4 && match.Value.All(char.IsDigit)) kind = WordKind.Word;
                Add(match.Index, end, kind);
                cursor = end;
            }
            Gap(cursor, source.Length, segmenter);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new WordSplit(source, pieces, length != text.Length);

        void Add(int start, int end, WordKind kind) => pieces.Add(new(source[start..end], kind, start, end - start));
        void Gap(int start, int end, WordsSegmenter? segmenter)
        {
            if (start == end) return;
            if (segmenter == null) { Fallback(start, end); return; }
            IReadOnlyList<WordSegment> tokens;
            try { tokens = segmenter.GetTokens(source[start..end]); }
            catch (Exception) { Fallback(start, end); return; }
            int cursor = start;
            for (int i = 0; i < tokens.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = tokens[i];
                int at = start + (int)token.SourceTextSegment.StartPosition;
                int finish = at + (int)token.SourceTextSegment.Length;
                Missing(cursor, at);
                int last = i;
                foreach (var alternate in token.AlternateForms)
                {
                    if (alternate.NormalizationFormat != AlternateNormalizationFormat.NotNormalized || alternate.SourceTextSegment.StartPosition != token.SourceTextSegment.StartPosition) continue;
                    int candidate = start + (int)(alternate.SourceTextSegment.StartPosition + alternate.SourceTextSegment.Length);
                    int boundary = finish;
                    for (int j = i + 1; j < tokens.Count && boundary < candidate; j++)
                    {
                        var next = tokens[j].SourceTextSegment;
                        if (start + (int)next.StartPosition != boundary) break;
                        boundary += (int)next.Length;
                        if (boundary == candidate && candidate > finish && !source[at..candidate].EnumerateRunes().Any(r => Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r))) last = Math.Max(last, j);
                    }
                }
                var tail = tokens[last].SourceTextSegment;
                finish = start + (int)(tail.StartPosition + tail.Length);
                Add(at, finish, WordKind.Word); cursor = finish; i = last;
            }
            Missing(cursor, end);
        }
        void Missing(int start, int end)
        {
            for (int at = start; at < end;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Rune.TryGetRuneAt(source, at, out var rune);
                int next = at + rune.Utf16SequenceLength;
                if (!Rune.IsWhiteSpace(rune)) Add(at, next, WordKind.Punctuation);
                at = next;
            }
        }
        void Fallback(int start, int end)
        {
            for (int at = start; at < end;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Rune.TryGetRuneAt(source, at, out var rune);
                int next = at + rune.Utf16SequenceLength;
                if (Rune.IsWhiteSpace(rune)) { at = next; continue; }
                bool word = Rune.IsLetterOrDigit(rune);
                if (word && !Han(rune))
                    while (next < end && Rune.TryGetRuneAt(source, next, out var following) && Rune.IsLetterOrDigit(following) && !Han(following)) next += following.Utf16SequenceLength;
                Add(at, next, word ? WordKind.Word : WordKind.Punctuation); at = next;
            }
        }
    }, cancellationToken);

    static bool Han(Rune r) => r.Value is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;
}
