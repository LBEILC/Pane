// 批量重命名的演示数据（设计负责人，040）：照原型 prototype/rename.html 的「12 张照片」。
// 假服务按契约 Rename.cs 的规则含义算新名字，只在内存里改，不碰磁盘；真实实现由工程写在服务层。
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class RenameSample : IBatchRenamer, IFileSelection
{
    public const string Folder = @"D:\照片\2026-10 青岛";

    // 原型的 12 张照片（文件名里就是拍摄时间，修改日期取它）
    static readonly string[] Shots = ["20261012_093015", "20261012_101244", "20261012_164802", "20261013_081930", "20261013_120511", "20261013_183340",
        "20261014_075902", "20261014_102217", "20261014_145530", "20261015_090118", "20261015_112645", "20261015_190023"];

    public static IReadOnlyList<string> Photos => [.. Shots.Select(s => Path.Combine(Folder, $"IMG_{s}.jpg"))];

    /// <summary>原型带的两条规则：正则把 IMG_20261012_093015 换成 2026-10-12 青岛，末尾加两位序号。</summary>
    public static IReadOnlyList<RenameRule> SampleRules =>
    [
        new(RenameRuleKind.Replace) { Find = @"IMG_(\d{4})(\d{2})(\d{2})_\d+", Text = "$1-$2-$3 青岛", Regex = true },
        new(RenameRuleKind.Number) { Position = RenamePosition.End, Start = 1, Step = 1, Digits = 2, Separator = "-" },
    ];

    // 同目录里不在这批的文件（演示「已有同名文件」）
    readonly HashSet<string> others = new(StringComparer.OrdinalIgnoreCase) { Path.Combine(Folder, "行程.txt") };
    readonly Dictionary<string, string> renamed = new(StringComparer.OrdinalIgnoreCase);   // 当前路径 → 原路径（演示里的「磁盘」）
    readonly Dictionary<string, List<(string From, string To)>> undo = [];

    public Task<IReadOnlyList<string>> ReadAsync(nint window, CancellationToken cancellationToken) => Task.FromResult(Photos);

    public Task<IReadOnlyList<RenamePreview>> PreviewAsync(IReadOnlyList<string> paths, IReadOnlyList<RenameRule> rules, CancellationToken cancellationToken)
    {
        var list = new List<RenamePreview>(paths.Count);
        Regex?[] patterns = new Regex?[rules.Count];
        for (int r = 0; r < rules.Count; r++)
        {
            if (!rules[r].Enabled || rules[r].Kind != RenameRuleKind.Replace || !rules[r].Regex || rules[r].Find.Length == 0) continue;
            try { patterns[r] = new Regex(rules[r].Find, rules[r].MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase); }
            catch (ArgumentException) { return Task.FromResult<IReadOnlyList<RenamePreview>>([.. paths.Select(p => new RenamePreview(p, Path.GetFileName(p), RenameIssue.BadPattern))]); }
        }
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var name = NewName(path, i, rules, patterns);
            var old = Path.GetFileName(path);
            var issue = name == old ? RenameIssue.Unchanged
                : Path.GetFileNameWithoutExtension(name).Trim().Length == 0 ? RenameIssue.Empty
                : name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.EndsWith(' ') || name.EndsWith('.') || Reserved(name) ? RenameIssue.Invalid
                : RenameIssue.None;
            list.Add(new(path, name, issue));
        }
        var batch = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var counts = list.Where(p => p.Issue == RenameIssue.None).GroupBy(p => Path.Combine(Path.GetDirectoryName(p.Path)!, p.NewName), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Issue != RenameIssue.None) continue;
            var target = Path.Combine(Path.GetDirectoryName(list[i].Path)!, list[i].NewName);
            if (counts[target] > 1) list[i] = list[i] with { Issue = RenameIssue.Duplicate };
            else if (others.Contains(target) && !batch.Contains(target)) list[i] = list[i] with { Issue = RenameIssue.Exists };
        }
        return Task.FromResult<IReadOnlyList<RenamePreview>>(list);
    }

    static bool Reserved(string name) => Regex.IsMatch(Path.GetFileNameWithoutExtension(name), "^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.IgnoreCase);

    // 演示文件的修改日期：文件名里的拍摄时间；认不出就用今天
    static DateTime DateOf(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"(\d{8})_(\d{6})");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
    }

    static string NewName(string path, int index, IReadOnlyList<RenameRule> rules, Regex?[] patterns)
    {
        var folder = Directory.Exists(path) || !Path.HasExtension(path);
        var file = Path.GetFileName(path);
        string stem = folder ? file : Path.GetFileNameWithoutExtension(file);
        string? ext = folder ? null : Path.GetExtension(file).TrimStart('.');
        if (ext is { Length: 0 }) ext = null;
        for (int r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            if (!rule.Enabled) continue;
            switch (rule.Kind)
            {
                case RenameRuleKind.Replace when rule.Find.Length > 0:
                    stem = patterns[r] is { } re ? re.Replace(stem, rule.Text)
                        : Regex.Replace(stem, Regex.Escape(rule.Find), rule.Text.Replace("$", "$$"), rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                    break;
                case RenameRuleKind.Insert when rule.Text.Length > 0: stem = Insert(stem, rule.Text, rule, false); break;
                case RenameRuleKind.Number:
                    stem = Insert(stem, (rule.Start + index * rule.Step).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, rule.Digits), '0'), rule, true); break;
                case RenameRuleKind.Remove:
                {
                    int from = Math.Clamp(rule.Index - 1, 0, stem.Length), n = Math.Clamp(rule.Count, 0, stem.Length - from);
                    stem = rule.FromEnd ? stem.Remove(stem.Length - from - n, n) : stem.Remove(from, n);
                    break;
                }
                case RenameRuleKind.Case:
                    stem = Case(stem, rule.Case);
                    if (rule.IncludeExtension && ext is not null) ext = rule.Case == RenameCase.Title ? ext.ToLowerInvariant() : Case(ext, rule.Case);
                    break;
                case RenameRuleKind.Extension when !folder: ext = rule.Text.TrimStart('.') is { Length: > 0 } t ? t : null; break;
                case RenameRuleKind.Date:
                {
                    string text;
                    try { text = DateOf(path).ToString(rule.DateFormat.Length > 0 ? rule.DateFormat : "yyyy-MM-dd", CultureInfo.InvariantCulture); }
                    catch (FormatException) { text = DateOf(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
                    stem = Insert(stem, text, rule, true);
                    break;
                }
            }
        }
        return ext is null ? stem : stem + "." + ext;
    }

    // 契约：Index = 从开头数第 Index 个字之前（FromEnd 时从末尾数）；序号和日期在开头 / 末尾时和名字之间加 Separator
    static string Insert(string stem, string piece, RenameRule r, bool separator) => r.Position switch
    {
        RenamePosition.Start => piece + (separator ? r.Separator : "") + stem,
        RenamePosition.End => stem + (separator ? r.Separator : "") + piece,
        _ => stem.Insert(Math.Clamp(r.FromEnd ? stem.Length - (r.Index - 1) : r.Index - 1, 0, stem.Length), piece),
    };

    static string Case(string s, RenameCase c) => c switch
    {
        RenameCase.Upper => s.ToUpperInvariant(),
        RenameCase.Lower => s.ToLowerInvariant(),
        _ => Title(s),
    };

    static string Title(string s)
    {
        var b = new StringBuilder(s.ToLowerInvariant());
        for (int i = 0; i < b.Length; i++)
            if (char.IsLetter(b[i]) && (i == 0 || " _-.(（".Contains(b[i - 1]))) b[i] = char.ToUpperInvariant(b[i]);
        return b.ToString();
    }

    public async Task<RenameResult> ApplyAsync(IReadOnlyList<RenamePreview> plan, CancellationToken cancellationToken)
    {
        await Task.Delay(120, cancellationToken);   // 演示：像真的改了一下
        var done = plan.Where(p => p.Issue == RenameIssue.None).Select(p => (From: p.Path, To: Path.Combine(Path.GetDirectoryName(p.Path)!, p.NewName))).ToList();
        if (done.Count == 0) return new(0, [], null);
        var id = Guid.NewGuid().ToString("N");
        undo[id] = done;
        return new(done.Count, [], id);
    }

    public async Task<RenameResult> UndoAsync(string undoId, CancellationToken cancellationToken)
    {
        await Task.Delay(80, cancellationToken);
        return undo.Remove(undoId, out var done) ? new(done.Count, [], null) : new(0, [], null);
    }
}
