using System.Runtime.CompilerServices;
using Pane.Contracts;
using Pane.Settings;
namespace Pane.Services.Launcher;

internal sealed partial class LauncherSearch
{
    public SearchMemory SearchMemory { get; }
    readonly ConditionalWeakTable<LauncherItem, string> selectionQueries = new();
    // 同一次搜索的首批和最终结果共用 Id 集合，晚到的旧搜索只更新自己的集合。
    sealed class SelectionBatch(string? query)
    {
        public string? Query { get; } = query;
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
    }
    volatile SelectionBatch currentBatch = new(null);
    readonly SemaphoreSlim pathChecks = new(4);

    static string? LearningQuery(string raw, LauncherSettings settings)
    {
        if (!settings.LearnSelections) return null;
        string input = raw.TrimStart();
        int space = input.IndexOf(' ');
        if (space > 0)
        {
            string key = input[..space];
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (settings.SearchEngines.Any(e => comparer.Equals(e.Keyword, key)) ||
                settings.FilesKeywords.Contains(key, comparer) || settings.TranslateKeywords.Contains(key, comparer) ||
                settings.MemoKeywords.Contains(key, comparer) || settings.AskKeywords.Contains(key, comparer)) return null;
        }
        string query = input.TrimEnd();
        return query.Length == 0 || query.StartsWith('>') ? null : query.ToLowerInvariant();
    }

    static bool CanLearn(LauncherItem row) => row.Group is LauncherGroups.Applications or LauncherGroups.Settings
        or LauncherGroups.Favorites or LauncherGroups.Files or LauncherGroups.Commands or LauncherGroups.Best;

    string? SelectionQuery(LauncherItem item)
    {
        if (!CanLearn(item) || !store.Current.Launcher.LearnSelections) return null;
        if (selectionQueries.TryGetValue(item, out var original)) return original;
        var batch = currentBatch;
        lock (batch.Ids) return batch.Ids.Contains(item.Id) ? batch.Query : null;
    }

    void RecordSelection(string? query, LauncherItem item)
    {
        if (query != null && store.Current.Launcher.LearnSelections) SearchMemory.Record(query, item.Id);
    }

    async Task<LauncherItem[]> ApplyLearningAsync(LauncherItem[] rows, SelectionBatch batch, SourceContext context, bool final, CancellationToken token)
    {
        var query = batch.Query;
        if (query == null || !store.Current.Launcher.LearnSelections ||
            rows.Any(r => r.Group is LauncherGroups.Calculation or LauncherGroups.Direct ||
                r.Group == LauncherGroups.Timer && r.Id != "power:panel")) return rows;

        var ranked = SearchMemory.Ranked(query);
        if (ranked.Length > 0)
        {
            string id = ranked[0];
            var best = rows.FirstOrDefault(r => r.Id == id && CanLearn(r));
            if (context.Hidden(id)) best = null;
            else if (id.StartsWith("file:", StringComparison.Ordinal) || best?.Group == LauncherGroups.Favorites)
                best = final ? await CheckFileAsync(id, best, query, token).ConfigureAwait(false) : null;

            // 所有其余项沿用来源顺序；同组中只把选中过的项前移。
            int Rank(LauncherItem row)
            {
                int position = Array.IndexOf(ranked, row.Id);
                return position < 0 ? int.MaxValue : position;
            }
            rows = rows.Where(r => best == null || r.Id != best.Id).GroupBy(r => r.Group)
                .SelectMany(g => g.OrderBy(Rank)).ToArray();
            if (best != null)
            {
                int at = Array.FindIndex(rows, r => r.Group is not (LauncherGroups.Direct or LauncherGroups.Timer));
                if (at < 0) at = rows.Length;
                rows = [..rows[..at], best with { Group = LauncherGroups.Best }, ..rows[at..]];
            }
        }
        for (int i = 0; i < rows.Length; i++)
        {
            if (!CanLearn(rows[i])) continue;
            // 来源可复用行，按返回实例关联输入，重叠查询和首批回调互不覆盖。
            rows[i] = rows[i] with { };
            selectionQueries.Add(rows[i], query);
        }
        lock (batch.Ids) batch.Ids.UnionWith(rows.Where(CanLearn).Select(row => row.Id));
        return rows;
    }

    async Task<LauncherItem?> CheckFileAsync(string id, LauncherItem? candidate, string query, CancellationToken token)
    {
        string path = id[(id.IndexOf(':') + 1)..];
        string name = Path.GetFileName(path);
        if (candidate == null && (!Path.IsPathFullyQualified(path) || !name.Contains(query, StringComparison.OrdinalIgnoreCase))) return null;
        if (!pathChecks.Wait(0)) return null;
        var check = Task.Run(() =>
        {
            try
            {
                bool folder = Directory.Exists(path);
                if (!folder && !File.Exists(path)) return null;
                return candidate ?? new LauncherItem(id, folder ? LauncherItemKind.Folder : LauncherItemKind.File,
                    LauncherGroups.Files, name, Path.GetDirectoryName(path) ?? "", new(folder ? "folder" : "doc"), FileSearch.Highlights(name, query));
            }
            finally { pathChecks.Release(); }
        });
        try { return await check.WaitAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }
}
