using System.Text;
using Microsoft.VisualBasic.FileIO;
using Pane.Contracts;
using Pane.Services.Launcher;

namespace Pane.Services.Memo;

internal sealed class NoteStore : INoteStore
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
        var rows = NoteDisk.Scan(root,log);
        _ = pinyin.Value;
        var loaded = new Dictionary<string,Note>();
        foreach (var row in rows)
        {
            loaded[row.Id] = loadedFolder == root && files.GetValueOrDefault(row.Id) == row ? persisted[row.Id] :
                new(row.Id, File.ReadAllText(Path.Combine(root,row.FileName)), row.Pinned, row.Created, row.Modified, row.From);
        }
        NoteDisk.SaveIndex(root,rows);
        loadedFolder = root;
        files.Clear(); persisted.Clear();
        foreach (var row in rows) { files.Add(row.Id,row); persisted.Add(row.Id,loaded[row.Id]); }
        lock (gate)
        {
            foreach (string id in notes.Keys.Except(loaded.Keys).Except(pending.Keys).ToArray()) notes.Remove(id);
            foreach (var pair in loaded) if (!pending.ContainsKey(pair.Key)) notes[pair.Key] = pair.Value;
        }
    }

    public Note Add(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var now = DateTimeOffset.UtcNow;
        var note = new Note(NoteDisk.NewId(), text, false, now, now);
        Restore(note);
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
        lock (gate) Change(note.Id,note);
        Changed?.Invoke(this,EventArgs.Empty);
    }

    // 网络往返期间如果有本机编辑，调用方重新合并。
    internal bool ApplySynced(string id, Note? expected, Note? value, out Task saved)
    {
        lock (gate)
        {
            saved = tail;
            if (notes.GetValueOrDefault(id) != expected) return false;
            if (value == expected) return true;
            Change(id,value);
            saved = tail;
        }
        Changed?.Invoke(this,EventArgs.Empty);
        return true;
    }

    internal Note? Find(string id) { lock (gate) return notes.GetValueOrDefault(id); }

    // 调用方持有内存锁；磁盘操作在同一队列中按调用顺序执行。
    void Change(string id, Note? note)
    {
        if (note == null) notes.Remove(id); else notes[id] = note;
        long version = ++revision;
        pending[id] = version;
        Queue(() =>
        {
            try { Save(id,note); }
            catch
            {
                lock (gate) if (pending.GetValueOrDefault(id) == version) pending.Remove(id);
                Reload(Folder);
                Changed?.Invoke(this,EventArgs.Empty);
                throw;
            }
            finally { lock (gate) if (pending.GetValueOrDefault(id) == version) pending.Remove(id); }
        });
    }

    void Save(string id, Note? note)
    {
        string root = Folder;
        if (loadedFolder != root) Reload(root);
        var old = files.GetValueOrDefault(id);
        string? oldPath = old == null ? null : Path.Combine(root,old.FileName);
        if (note == null)
        {
            if (oldPath != null && File.Exists(oldPath)) recycle(oldPath);
            files.Remove(id); persisted.Remove(id);
        }
        else
        {
            var previous = persisted.GetValueOrDefault(id);
            string name = old != null && previous?.Title == note.Title ? old.FileName :
                NoteDisk.FileName(note, n => !string.Equals(old?.FileName,n,StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root,n)));
            string path = Path.Combine(root,name);
            if (oldPath != null && oldPath != path && File.Exists(oldPath)) File.Move(oldPath,path);
            if (previous?.Text != note.Text || !File.Exists(path)) NoteDisk.Atomic(path,Encoding.UTF8.GetBytes(note.Text));
            var f = new FileInfo(path);
            files[id] = new(id,name,note.Pinned,note.Created,note.Modified,note.From,f.Length,f.LastWriteTimeUtc);
            persisted[id] = note;
        }
        NoteDisk.Atomic(Path.Combine(root,NoteDisk.Index), NoteDisk.Encode(files.Values), true);
    }

    public IReadOnlyList<NoteMatch> Search(string query,int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return [];
        var matches = new List<NoteMatch>();
        foreach (var note in All)
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
