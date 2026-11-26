using Pane.Contracts;

namespace Pane.Services.Memo;

internal sealed partial class NoteStore
{
    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    readonly Dictionary<string,MemoTag> tags = new(Names);
    readonly Dictionary<string,long> pendingTags = new(Names);
    Dictionary<string,MemoTag> diskTags = new(Names);
    Dictionary<string,string> fileIds = new();

    internal MemoTag[] Definitions { get { lock (gate) return MemoTag.Merge(tags.Values); } }
    public IReadOnlyList<NoteTag> Tags => Definitions.Where(t => !t.Deleted).Select(t => new NoteTag(t.Name,t.Color)).ToArray();

    static string NameOf(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 30) throw new ArgumentException("标签名须为 1 到 30 个字。");
        return name;
    }

    static int? ValidColor(int? color) => color is < 1 or > 6
        ? throw new ArgumentException("请选择 1 到 6 号色点，或不设色点。") : color;

    long NextStamp() => Math.Max(DateTimeOffset.UtcNow.UtcTicks,tags.Values.Select(t => t.Modified).DefaultIfEmpty().Max()+1);

    string[] ResolveTags(IReadOnlyList<string> names,IReadOnlyDictionary<string,int?>? colors,out MemoTag[] definitions)
    {
        var additions = new List<MemoTag>();
        var result = new List<string>();
        long stamp = NextStamp();
        foreach (var name in names.Select(NameOf).Distinct(Names))
        {
            if (tags.GetValueOrDefault(name) is { Deleted:false } existing) result.Add(existing.Name);
            else
            {
                var color = colors?.FirstOrDefault(p => Names.Equals(p.Key.Trim(),name)).Value;
                additions.Add(new(name,ValidColor(color),stamp,stamp));
                stamp++;
                result.Add(name);
            }
        }
        definitions = additions.ToArray();
        return result.ToArray();
    }

    public void SetTags(string id,IReadOnlyList<string> tags)
    {
        lock (gate)
        {
            if (!notes.TryGetValue(id,out var note)) return;
            var names = ResolveTags(tags,null,out var definitions);
            if (note.Tags.SequenceEqual(names)) return;
            CommitMetadata(definitions,[note with { Tags = names, Modified = DateTimeOffset.UtcNow }]);
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }

    public NoteTag CreateTag(string name,int? color)
    {
        name = NameOf(name);
        lock (gate)
        {
            if (tags.GetValueOrDefault(name) is { Deleted:false } existing) return new(existing.Name,existing.Color);
            long stamp = NextStamp();
            EditDefinitions([new(name,ValidColor(color),stamp,stamp)]);
        }
        Changed?.Invoke(this,EventArgs.Empty);
        return new(name,color);
    }

    public void UpdateTag(string name,string newName,int? color)
    {
        newName = NameOf(newName);
        lock (gate)
        {
            if (tags.GetValueOrDefault(name.Trim()) is not { Deleted:false } old) return;
            if (old.Name == newName && old.Color == color) return;
            bool rename = !Names.Equals(old.Name,newName);
            if (rename && tags.GetValueOrDefault(newName) is { Deleted:false }) throw new ArgumentException("已有同名标签，请换个名字。");
            long stamp = NextStamp();
            var updated = old with { Name = newName, Color = ValidColor(color), Modified = stamp };
            EditDefinitions(rename ? [old with { Deleted = true, Modified = stamp },updated] : [updated],old.Name,newName);
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }

    public void DeleteTag(string name)
    {
        lock (gate)
        {
            if (tags.GetValueOrDefault(name.Trim()) is not { Deleted:false } old) return;
            EditDefinitions([old with { Deleted = true, Modified = NextStamp() }],old.Name);
        }
        Changed?.Invoke(this,EventArgs.Empty);
    }

    internal MemoTag MergeDefinition(MemoTag incoming,out Task saved)
    {
        MemoTag winner;
        lock (gate)
        {
            tags.TryGetValue(incoming.Name,out var local);
            winner = local == null ? incoming : MemoTag.Merge([local,incoming]).Single();
            if (winner == local) { saved = tail; return winner; }
            EditDefinitions([winner],winner.Name,winner.Deleted ? null : winner.Name);
            saved = tail;
        }
        Changed?.Invoke(this,EventArgs.Empty);
        return winner;
    }

    // 收到旧设备的正文或撤销删除时，忽略已删除的定义并采用当前写法。
    Note WithCurrentTags(Note note)
    {
        var names = note.Tags.Select(n => tags.TryGetValue(n,out var t) ? (t.Deleted ? null : t.Name) : n)
            .OfType<string>().Distinct(Names).ToArray();
        return names.SequenceEqual(note.Tags) ? note : note with { Tags = names };
    }

    void TrackDefinitions(MemoTag[]? definitions,long version)
    {
        if (definitions == null) return;
        foreach (var t in definitions) { tags[t.Name] = t; pendingTags[t.Name] = version; }
    }

    void FinishDefinitions(MemoTag[]? definitions,long version)
    {
        if (definitions == null) return;
        foreach (var t in definitions)
            if (pendingTags.GetValueOrDefault(t.Name) == version) pendingTags.Remove(t.Name);
    }

    void SaveMetadata(string root,MemoTag[]? definitions)
    {
        if (definitions != null) foreach (var t in definitions) diskTags[t.Name] = t;
        NoteDisk.SaveIndex(root,new MemoIndex(files.Values.ToArray(),MemoTag.Merge(diskTags.Values),fileIds));
    }

    // 改名和删除将所有受影响的行及标签定义一次写入索引。
    void EditDefinitions(MemoTag[] definitions,string? oldName = null,string? newName = null)
    {
        var changed = new List<Note>();
        if (oldName != null)
        {
            foreach (var note in notes.Values)
            {
                if (!note.Tags.Contains(oldName,Names)) continue;
                var names = note.Tags.Select(n => Names.Equals(n,oldName) ? newName : n).OfType<string>().Distinct(Names).ToArray();
                if (names.SequenceEqual(note.Tags)) continue;
                var modified = new DateTimeOffset(Math.Max(note.Modified.UtcTicks,definitions[0].Modified),TimeSpan.Zero);
                changed.Add(note with { Tags = names, Modified = modified });
            }
        }
        CommitMetadata(definitions,changed);
    }

    void CommitMetadata(MemoTag[] definitions,IReadOnlyList<Note> changed)
    {
        long version = ++revision;
        TrackDefinitions(definitions,version);
        foreach (var next in changed)
        {
            notes[next.Id] = next;
            pending[next.Id] = version;
        }
        void Finish()
        {
            lock (gate)
            {
                FinishDefinitions(definitions,version);
                foreach (var n in changed)
                    if (pending.GetValueOrDefault(n.Id) == version) pending.Remove(n.Id);
            }
        }
        Queue(() =>
        {
            try
            {
                string root = Folder;
                if (loadedFolder != root) Reload(root);
                foreach (var note in changed)
                    if (files.TryGetValue(note.Id,out var row))
                    {
                        files[note.Id] = row with { Tags = note.Tags.ToArray(), Modified = note.Modified };
                        persisted[note.Id] = persisted[note.Id] with { Tags = note.Tags, Modified = note.Modified };
                    }
                SaveMetadata(root,definitions);
            }
            catch { Finish(); Reload(Folder); Changed?.Invoke(this,EventArgs.Empty); throw; }
            Finish();
        });
    }
}
