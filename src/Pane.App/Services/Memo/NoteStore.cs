using System.Text;
using Microsoft.VisualBasic.FileIO;
using Pane.Contracts;
using Pane.Services.Launcher;

namespace Pane.Services.Memo;

internal sealed partial class NoteStore : INoteStore
{
    readonly Func<string> folder;
    readonly object storageGate, gate = new();
    readonly Action<string> recycle;
    readonly Dictionary<string,Note> notes = new();
    readonly Dictionary<string,long> pending = new();
    readonly Dictionary<string,NoteFile> files = new();
    readonly Dictionary<string,Note> persisted = new();
    readonly Lazy<PinyinTable> pinyin = new(() => new());
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Note,SearchName> searchNames = new();
    readonly Action<string>? log;
    string? loadedFolder;
    Task tail = Task.CompletedTask;
    long revision;

    internal NoteStore(Func<string> folder, object storageGate, Action<string>? recycle = null, Action<string>? log = null)
    {
        this.log = log;
        this.folder = folder;
        this.storageGate = storageGate;
        this.recycle = recycle ?? (p => FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException));
        Refresh();
    }

    public string Folder => folder();
    internal Task Completion { get { lock (gate) return tail; } }
    public IReadOnlyList<Note> All { get { lock (gate) return notes.Values.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Modified).ToArray(); } }
    public event EventHandler? Changed;

    void Queue(Action action)
    {
        tail = tail.ContinueWith(_ =>
        {
            try { lock (storageGate) action(); }
            catch (Exception e) { log?.Invoke($"读写失败 {e.GetType().Name} 0x{e.HResult:X8}"); throw; }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void Refresh()
    {
        lock (gate) Queue(() => { Reload(Folder); Changed?.Invoke(this,EventArgs.Empty); });
    }

    void Reload(string root)
    {
        Directory.CreateDirectory(root);
        var index = NoteDisk.ScanIndex(root,log);
        var rows = index.Rows;
        fileIds = index.FileIds!;
        diskTags = index.Definitions.ToDictionary(t => t.Name,StringComparer.OrdinalIgnoreCase);
        _ = pinyin.Value;
        var loaded = new Dictionary<string,Note>();
        foreach (var row in rows)
        {
            var old = files.GetValueOrDefault(row.Id);
            bool sameBody = loadedFolder == root && old?.Size == row.Size && old.LastWrite == row.LastWrite
                && old.FileName == row.FileName;
            var previous = sameBody ? persisted[row.Id] : null;
            var names = row.Tags ?? Array.Empty<string>();
            loaded[row.Id] = previous != null && previous.Modified == row.Modified && previous.Pinned == row.Pinned
                && previous.Created == row.Created && previous.From == row.From && previous.Tags.SequenceEqual(names)
                ? previous : new Note(row.Id,previous?.Text ?? File.ReadAllText(Path.Combine(root,row.FileName)),
                    row.Pinned,row.Created,row.Modified,row.From) { Tags = names };
        }
        NoteDisk.SaveIndex(root,index);
        loadedFolder = root;
        files.Clear(); persisted.Clear();
        foreach (var row in rows) { files.Add(row.Id,row); persisted.Add(row.Id,loaded[row.Id]); }
        lock (gate)
        {
            foreach (var name in tags.Keys.Except(pendingTags.Keys,StringComparer.OrdinalIgnoreCase).ToArray()) tags.Remove(name);
            foreach (var t in diskTags.Values)
                if (!pendingTags.ContainsKey(t.Name)) tags[t.Name] = t;
            foreach (string id in notes.Keys.Except(loaded.Keys).Except(pending.Keys).ToArray()) notes.Remove(id);
            foreach (var pair in loaded) if (!pending.ContainsKey(pair.Key)) notes[pair.Key] = pair.Value;
        }
    }

    public Note Add(string text) => Add(text,[]);

    public Note Add(string text,IReadOnlyList<string> tags,IReadOnlyDictionary<string,int?>? colors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var now = DateTimeOffset.UtcNow;
        Note note;
        lock (gate)
        {
            var names = ResolveTags(tags,colors,out var definitions);
            note = new Note(NoteDisk.NewId(),text,false,now,now) { Tags = names };
            Change(note.Id,note,definitions);
        }
        Changed?.Invoke(this,EventArgs.Empty);
        return note;
    }
    public Note? Update(string id, string text)
    {
        Note changed;
        lock (gate)
        {
            if (!notes.TryGetValue(id, out var note) || note.Text == text) return note;
            changed = note with { Text = text, Modified = DateTimeOffset.UtcNow };
            Change(id, changed);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }
    public void SetPinned(string id, bool pinned)
    {
        lock (gate)
        {
            if (!notes.TryGetValue(id,out var note) || note.Pinned == pinned) return;
            Change(id, note with { Pinned = pinned, Modified = DateTimeOffset.UtcNow });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Delete(string id)
    {
        lock (gate) { if (!notes.ContainsKey(id)) return; Change(id,null); }
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Restore(Note note)
    {
        if (!NoteDisk.ValidId(note.Id)) throw new ArgumentException(nameof(note.Id));
        lock (gate) Change(note.Id,WithCurrentTags(note));
        Changed?.Invoke(this,EventArgs.Empty);
    }

    // 网络往返期间如果有本机编辑，调用方重新合并。
    internal bool ApplySynced(string id, Note? expected, Note? value, out Task saved)
    {
        lock (gate)
        {
            saved = tail;
            if (notes.GetValueOrDefault(id) != expected) return false;
            if (value != null) value = WithCurrentTags(value);
            if (value == expected || (value != null && expected != null &&
                value.Tags.SequenceEqual(expected.Tags) && (value with { Tags = expected.Tags }) == expected)) return true;
            Change(id,value);
            saved = tail;
        }
        Changed?.Invoke(this,EventArgs.Empty);
        return true;
    }

    internal Note? Find(string id) { lock (gate) return notes.GetValueOrDefault(id); }

    // 调用方持有内存锁；磁盘操作在同一队列中按调用顺序执行。
    void Change(string id, Note? note, MemoTag[]? definitions = null)
    {
        if (note == null) notes.Remove(id); else notes[id] = note;
        long version = ++revision;
        pending[id] = version;
        TrackDefinitions(definitions,version);
        void Finish()
        {
            lock (gate)
            {
                if (pending.GetValueOrDefault(id) == version) pending.Remove(id);
                FinishDefinitions(definitions,version);
            }
        }
        Queue(() =>
        {
            try { Save(id,note,definitions); }
            catch { Finish(); Reload(Folder); Changed?.Invoke(this,EventArgs.Empty); throw; }
            Finish();
        });
    }

    void Save(string id, Note? note, MemoTag[]? definitions)
    {
        string root = Folder;
        if (loadedFolder != root) Reload(root);
        var old = files.GetValueOrDefault(id);
        string? oldPath = old == null ? null : Path.Combine(root,old.FileName);
        if (note == null)
        {
            if (oldPath != null && File.Exists(oldPath)) recycle(oldPath);
            files.Remove(id); persisted.Remove(id); fileIds.Remove(id);
        }
        else
        {
            var previous = persisted.GetValueOrDefault(id);
            string name = old != null && previous?.Title == note.Title ? old.FileName :
                NoteDisk.FileName(note, n => !string.Equals(old?.FileName,n,StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root,n)));
            string path = Path.Combine(root,name);
            if (oldPath != null && oldPath != path && File.Exists(oldPath)) File.Move(oldPath,path);
            if (previous?.Text != note.Text || !File.Exists(path))
            {
                NoteDisk.Atomic(path,Encoding.UTF8.GetBytes(note.Text));
                if (NoteDisk.Identity(path) is {} identity) fileIds[id] = identity;
            }
            var f = new FileInfo(path);
            files[id] = new(id,name,note.Pinned,note.Created,note.Modified,note.From,f.Length,f.LastWriteTimeUtc,note.Tags.Count == 0 ? null : note.Tags.ToArray());
            persisted[id] = note;
        }
        SaveMetadata(root,definitions);
    }

    public IReadOnlyList<NoteMatch> Search(string query,int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return [];
        var matches = new List<NoteMatch>();
        var words = query.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        // TODO(design): 单独的 # 暂按空前缀匹配所有带标签的备忘录。
        var prefixes = words.Where(w => w.StartsWith('#')).Select(w => w[1..]).ToArray();
        var candidates = All.Where(n => prefixes.All(p => n.Tags.Any(t => t.StartsWith(p,StringComparison.OrdinalIgnoreCase))));
        if (prefixes.Length != 0)
        {
            query = string.Join(' ',words.Where(w => !w.StartsWith('#')));
            if (query.Length == 0) return candidates.Take(max)
                .Select(n => new NoteMatch(n,null,n.Text[..Math.Min(80,n.Text.Length)],null)).ToArray();
        }
        foreach (var note in candidates)
        {
            var title = searchNames.GetValue(note, n => pinyin.Value.Create(n.Title)).Match(query);
            int body = note.Text.IndexOf(query,StringComparison.OrdinalIgnoreCase);
            if (title == null && body < 0) continue;
            int start = title != null ? 0 : Math.Max(0,body-40);
            int end = title != null ? Math.Min(80,note.Text.Length) : Math.Min(note.Text.Length,body+query.Length+40);
            matches.Add(new(note,title?.Spans,note.Text[start..end],
                body >= start && body+query.Length <= end ? [new(body-start,query.Length)] : null));
        }
        return matches.OrderByDescending(m => m.TitleHighlights != null).ThenByDescending(m => m.Note.Modified).Take(max).ToArray();
    }
}
