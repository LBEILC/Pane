using System.Text;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal static class FileRanking
{
    static readonly HashSet<string> clutter = new(StringComparer.OrdinalIgnoreCase)
        { "AppData", "Temp", "tmp", "node_modules", "__pycache__", "site-packages", "WinSxS", "System Volume Information" };
    internal static bool Plain(string text) => text.IndexOfAny(['\\','/','*','?',':','"','|','<','>','!']) < 0;

    static (int Tier, int Index) Match(string name, string word)
    {
        int first = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (first < 0) return (0, -1);
        if (first == 0) return (name.Length == word.Length ? 4 : 3, 0);
        for (int i = first; i >= 0; i = name.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            char before = name[i - 1];
            if (char.IsWhiteSpace(before) || "-_.()[]{}+@#,，、".Contains(before) ||
                char.IsLower(before) && char.IsUpper(name[i])) return (2, i);
        }
        return (1, first);
    }

    static (int Tier, IReadOnlyList<TextSpan>? Spans) MatchName(string name, string text, string[] words)
    {
        var (tier, index) = Match(name, text);
        if (words.Length == 1) return (tier, index < 0 ? null : [new(index, text.Length)]);
        var spans = new List<TextSpan>();
        int lowest = 4;
        foreach (string word in words)
        {
            lowest = Math.Min(lowest, Match(name, word).Tier);
            int first = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (first >= 0) spans.Add(new(first, word.Length));
        }
        return (index >= 0 ? tier : lowest, spans);
    }

    internal static FileHit Highlight(FileHit hit, string text) =>
        hit with { Highlights = MatchName(hit.Name, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Spans };

    internal static FileHit[] Rank(IEnumerable<FileHit> hits, string text, string[] ids)
    {
        var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        int queryLength = text.EnumerateRunes().Count();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return hits.DistinctBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase).Select(hit =>
        {
            var (tier, spans) = MatchName(hit.Name, text, words);
            var parts = (Path.GetDirectoryName(hit.Path) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
            double days = hit.Modified.HasValue ? (now - hit.Modified.Value).TotalDays : double.PositiveInfinity;
            double score = tier * 100 - (parts.Any(p => p.StartsWith('.') || p.StartsWith('$') || clutter.Contains(p)) ? 150 : 0)
                - 2 * Math.Min(10, parts.Length) - .5 * Math.Clamp(hit.Name.EnumerateRunes().Count() - queryLength, 0, 30)
                + (days <= 1 ? 10 : days <= 7 ? 6 : days <= 30 ? 3 : 0)
                + (selected.Contains("file:" + hit.Path) ? 1000 : 0);
            return (Hit: hit with { Highlights = spans }, Score: score);
        }).OrderByDescending(row => row.Score).ThenBy(row => row.Hit.Name.Length).Select(row => row.Hit).ToArray();
    }

    internal const int PrefixLimit = 4096;
    internal sealed record Head(FileHit[] Items, int Total);
    internal static async Task<Head> HeadAsync(IEverythingIpc ipc, string text, Func<string,string> filter,
        string[] ids, CancellationToken token, int timeout, bool allowPartial, int? extraDeadlineMs = null,
        Func<Task<EverythingPage>>? sharedPrefix = null)
    {
        long start = Environment.TickCount64;
        // 名字排序取候选；在本地恢复包含批次按修改时间降序的同分先后。
        var page = await ipc.QueryAsync(filter(text), 0, 512, 1, token, timeout).WaitAsync(token).ConfigureAwait(false);
        IEnumerable<FileHit> candidates = page.Items.OrderByDescending(hit => hit.Modified);
        if (page.Total > page.Items.Count)
        {
            // 启动器给补充查询单独设截止时间，留出本地打分和交付的时间。
            using var supplement = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (extraDeadlineMs is int deadline)
                supplement.CancelAfter((int)Math.Max(0,deadline - (Environment.TickCount64 - start)));
            Task<EverythingPage> Extra(string modifier, int count) =>
                ipc.QueryAsync(filter(modifier + "\"" + text + "\""), 0, count, 1, supplement.Token, timeout);
            try
            {
                var prefix = await (sharedPrefix?.Invoke() ?? Extra("startwith:", PrefixLimit)).WaitAsync(supplement.Token).ConfigureAwait(false);
                // 保留名字靠前、最近修改和有选择记忆的前缀项，复用宽页中的元数据。
                var contains = candidates;
                var firstPrefixes = prefix.Items.Take(300)
                    .Concat(prefix.Items.OrderByDescending(hit => hit.Modified).Take(300));
                if (ids.Length > 0)
                {
                    var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    firstPrefixes = firstPrefixes.Concat(prefix.Items.Where(hit => selected.Contains("file:" + hit.Path)));
                }
                candidates = contains.Concat(firstPrefixes);
                if (prefix.Total > prefix.Items.Count)
                {
                    var exact = await Extra("wfn:", 50).WaitAsync(supplement.Token).ConfigureAwait(false);
                    candidates = contains.Concat(exact.Items).Concat(firstPrefixes);
                }
                else
                {
                    var exact = prefix.Items.Where(hit => hit.Name.Equals(text,StringComparison.OrdinalIgnoreCase)).Take(50);
                    candidates = contains.Concat(exact).Concat(firstPrefixes);
                }
            }
            catch (Exception e) when (allowPartial && e is TimeoutException or OperationCanceledException) { }
        }
        return new(Rank(candidates, text, ids), page.Total);
    }
}
