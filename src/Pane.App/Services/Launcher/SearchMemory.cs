using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
namespace Pane.Services.Launcher;

internal sealed record Selection(string Id, DateTimeOffset Time);
[JsonSerializable(typeof(Dictionary<string,Selection[]>))]
internal partial class SearchMemoryJson : JsonSerializerContext { }

internal sealed class SearchMemory : ISearchMemory, IDisposable
{
    readonly object gate = new();
    readonly string path;
    // 数组发布后不修改，后台写入只需复制字典。
    Dictionary<string, Selection[]> history = [];
    DateTimeOffset nextExpiry = DateTimeOffset.MaxValue;
    bool dirty, disposed;
    Task writer = Task.CompletedTask;
    static readonly TimeSpan retention = TimeSpan.FromDays(90);

    readonly Func<string,string?> normalize;
    public SearchMemory(string directory, Func<string,string?>? normalize = null)
    {
        this.normalize = normalize ?? (q => q.Trim().ToLowerInvariant());
        path = Path.Combine(directory, "search-memory.json");
        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SearchMemoryJson.Default.DictionaryStringSelectionArray);
            if (saved == null || saved.Any(p => p.Key.Length == 0 || p.Value == null || p.Value.Length == 0 ||
                    p.Value.Any(s => s == null || string.IsNullOrEmpty(s.Id) || s.Time == default || s.Time > DateTimeOffset.MaxValue - retention)))
                throw new JsonException();
            history = saved.ToDictionary(p => p.Key, p => p.Value.OrderByDescending(s => s.Time).Take(10).ToArray());
            lock (gate) Prune(DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Log(e); }
    }

    void Log(Exception error)
    {
        try { File.AppendAllText(Path.ChangeExtension(path, ".log"), $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Prune(DateTimeOffset now)
    {
        var removed = history.Where(p => now - p.Value[0].Time >= retention).Select(p => p.Key).ToArray();
        foreach (var key in removed) history.Remove(key);
        bool oversized = history.Count > 2000;
        if (oversized)
            foreach (var key in history.OrderBy(p => p.Value[0].Time).Take(history.Count - 2000).Select(p => p.Key).ToArray())
                history.Remove(key);
        nextExpiry = history.Count == 0 ? DateTimeOffset.MaxValue : history.Min(p => p.Value[0].Time) + retention;
        if (removed.Length > 0 || oversized) Save();
    }

    public int Count { get { lock (gate) { Expire(); return history.Count; } } }
    void Expire() { var now = DateTimeOffset.UtcNow; if (now >= nextExpiry) Prune(now); }

    string? FileQueryKey(string query)
    {
        var key = normalize(query);
        if (string.IsNullOrEmpty(key) || key.StartsWith('>') || OpenTargetService.Url(OpenTargetService.SingleLine(key)) != null ||
            OpenTargetService.LocalPath(OpenTargetService.SingleLine(key)) != null || Calculator.Calc(key) != null ||
            PowerTimerParser.Parse(key,DateTimeOffset.Now) != null) return null;
        return key;
    }

    public void RecordFile(string query, string path)
    {
        if (FileQueryKey(query) is { Length: > 0 } key && Path.IsPathFullyQualified(path)) Record(key,"file:" + path);
    }

    internal string[] FileIds(string query) => FileQueryKey(query) is { Length: > 0 } key
        ? Ranked(key).Where(id => id.StartsWith("file:", StringComparison.Ordinal)).ToArray() : [];

    internal void Record(string query, string id)
    {
        lock (gate)
        {
            if (disposed) return;
            var now = DateTimeOffset.UtcNow;
            Expire();
            Selection[] previous = history.GetValueOrDefault(query) ?? [];
            history[query] = [new(id, now), ..previous.Take(9)];
            if (history.Count > 2000 || now >= nextExpiry) Prune(now);
            if (nextExpiry > now + retention) nextExpiry = now + retention;
            Save();
        }
    }

    internal string[] Ranked(string query)
    {
        Selection[]? recent;
        lock (gate)
        {
            Expire();
            if (!history.TryGetValue(query, out recent) && query.Length >= 2 && (query.Length > 2 || !char.IsSurrogatePair(query, 0)))
                recent = history.Where(p => p.Key.StartsWith(query, StringComparison.Ordinal)).Select(p => p.Value).MaxBy(s => s[0].Time);
        }
        // GroupBy 保留首次出现顺序，稳定排序后同票者最近选择优先。
        return recent == null ? [] : recent.GroupBy(s => s.Id).OrderByDescending(g => g.Count()).Select(g => g.Key).ToArray();
    }

    internal void Remove(string id)
    {
        lock (gate)
        {
            foreach (var key in history.Where(p => p.Value.Any(s => s.Id == id)).Select(p => p.Key).ToArray())
            {
                var remaining = history[key].Where(s => s.Id != id).ToArray();
                if (remaining.Length == 0) history.Remove(key); else history[key] = remaining;
                nextExpiry = DateTimeOffset.MinValue;
                Save();
            }
        }
    }

    public void Clear() { lock (gate) { history.Clear(); Save(); } }

    void Save()
    {
        if (disposed || dirty) return;
        dirty = true;
        writer = writer.ContinueWith(_ =>
        {
            Dictionary<string,Selection[]> snapshot;
            lock (gate) { snapshot = new(history); dirty = false; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, SearchMemoryJson.Default.DictionaryStringSelectionArray));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void Dispose()
    {
        Task pending;
        lock (gate) { disposed = true; pending = writer; }
        pending.GetAwaiter().GetResult();
    }
}
