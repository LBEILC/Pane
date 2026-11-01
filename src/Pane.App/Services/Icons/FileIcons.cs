using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Pane.Contracts;

namespace Pane.Services.Icons;

internal interface IFileIconExtractor
{
    bool Extract(string path, string output);
}

internal sealed class ShellFileIconExtractor : IFileIconExtractor
{
    [DllImport("Pane.Native.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int PaneFileIcon(string path, string output);
    public bool Extract(string path, string output) => PaneFileIcon(path, output) == 1;
}

internal sealed class EmptyFileIcons : IFileIcons
{
    public string? TryGet(string path, bool folder) => null;
    public event EventHandler<string>? Ready { add { } remove { } }
}

internal sealed class FileIcons : IFileIcons, IDisposable
{
    sealed class Entry(string key, string path)
    {
        public readonly string Key = key, Path = path;
        public readonly HashSet<string> Waiting = new(StringComparer.OrdinalIgnoreCase);
        public string? Output;
        public bool Failed;
        public long Used = DateTime.UtcNow.Ticks;
    }

    readonly object sync = new();
    readonly Dictionary<string, Entry> entries = new();
    readonly ConcurrentQueue<Entry> pending = new();
    readonly AutoResetEvent wake = new(false);
    readonly Dictionary<string, long> disk = new(); // 仅后台线程访问。
    readonly string cache;
    readonly int capacity;
    readonly IFileIconExtractor extractor;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool closing;
    public Task Completion => stopped.Task;
    public event EventHandler<string>? Ready;

    public FileIcons(string dataDirectory, IFileIconExtractor? extractor = null, int pathCapacity = 2000)
    {
        if (pathCapacity < 1) throw new ArgumentOutOfRangeException(nameof(pathCapacity));
        cache = Path.Combine(dataDirectory, "file-icons");
        capacity = pathCapacity;
        this.extractor = extractor ?? new ShellFileIconExtractor();
        var worker = new Thread(Work) { IsBackground = true, Name = "Pane file icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public string? TryGet(string path, bool folder)
    {
        if (closing || string.IsNullOrWhiteSpace(path)) return null;
        string key;
        try
        {
            string extension = Path.GetExtension(path).ToUpperInvariant();
            bool perPath = folder || extension is ".EXE" or ".LNK" or ".ICO" or ".URL" or ".APPREF-MS";
            // 只做字符串规范化与散列；不探测文件、desktop.ini 或磁盘缓存。
            string identity = perPath ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant() : extension;
            key = (perPath ? "p-" : "e-") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((folder ? "folder:" : "file:") + identity)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        lock (sync)
        {
            if (closing) return null;
            if (entries.TryGetValue(key, out var existing))
            {
                existing.Used = DateTime.UtcNow.Ticks;
                if (existing.Output != null) return existing.Output;
                if (!existing.Failed) existing.Waiting.Add(path);
                return null;
            }
            var entry = new Entry(key, path);
            entry.Waiting.Add(path);
            entries.Add(key, entry);
            pending.Enqueue(entry);
            wake.Set();
            return null;
        }
    }

    void Work()
    {
        try
        {
            try
            {
                Directory.CreateDirectory(cache);
                foreach (var file in Directory.EnumerateFiles(cache, "p-*.png"))
                    disk[Path.GetFileNameWithoutExtension(file)] = File.GetLastWriteTimeUtc(file).Ticks;
                Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            while (!closing)
            {
                if (pending.TryDequeue(out var entry)) Process(entry);
                else wake.WaitOne();
            }
            // 跨启动保存最近使用时间；所有磁盘操作仍在 STA 后台线程。
            foreach (var key in disk.Keys)
            {
                long used = LastUsed(key);
                try { File.SetLastWriteTimeUtc(Path.Combine(cache, key + ".png"), new DateTime(used, DateTimeKind.Utc)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally { wake.Dispose(); stopped.TrySetResult(); }
    }

    void Process(Entry entry)
    {
        string output = Path.Combine(cache, entry.Key + ".png"), temporary = output + ".tmp";
        bool success = false;
        try
        {
            if (!File.Exists(output))
            {
                Directory.CreateDirectory(cache);
                if (!extractor.Extract(entry.Path, temporary)) return;
                File.Move(temporary, output, true);
            }
            if (entry.Key.StartsWith("p-", StringComparison.Ordinal))
            {
                disk[entry.Key] = entry.Used;
                Trim(entry.Key);
            }
            success = true;
        }
        catch (Exception) { /* Shell 扩展或缓存写入失败：本进程记住失败，不反复调用。 */ }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            string[] waiting;
            lock (sync)
            {
                entry.Output = success ? output : null;
                entry.Failed = !success;
                waiting = entry.Waiting.ToArray();
                entry.Waiting.Clear();
            }
            if (success && !closing)
                foreach (string path in waiting)
                    foreach (EventHandler<string> handler in Ready?.GetInvocationList() ?? [])
                        try { handler(this, path); } catch (Exception) { /* 单个订阅者异常不能终止取图线程。 */ }
        }
    }

    long LastUsed(string key)
    {
        lock (sync) return entries.TryGetValue(key, out var entry) ? entry.Used : disk[key];
    }

    void Trim(string? current = null)
    {
        while (disk.Count > capacity)
        {
            string oldest = disk.Keys.Where(key => key != current).MinBy(LastUsed)!;
            // 删除失败时让本次提取失败；不把磁盘仍存在的文件当作已经清理。
            File.Delete(Path.Combine(cache, oldest + ".png"));
            disk.Remove(oldest);
            lock (sync)
                if (entries.TryGetValue(oldest, out var entry) && entry.Output != null) entries.Remove(oldest);
        }
    }

    public void Dispose()
    {
        // Shell 扩展可能很慢；关闭界面不等待它。后台线程完成当前调用后自行释放。
        lock (sync) { if (closing) return; closing = true; wake.Set(); }
    }
}
