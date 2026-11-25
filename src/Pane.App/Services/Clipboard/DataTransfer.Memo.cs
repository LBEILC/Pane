using Pane.Contracts;
using Pane.Services.Memo;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    internal Func<Task>? FlushNotes { get; set; }
    internal Action? NotesMoved { get; set; }
    static string MemoFolder(string data)
    {
        string path = Path.Combine(data,"settings.json");
        string? configured = File.Exists(path) ? ReadObject(path)["Storage"]?["LargeFilesDirectory"]?.GetValue<string>() : null;
        return NoteDisk.FolderFor(data,StorageLocation.Resolve(data,configured));
    }

    static bool MemoEntry(string name) => name.StartsWith("memo/",StringComparison.Ordinal) &&
        (name[5..] == NoteDisk.Index || NoteDisk.BodyName(name[5..]));

    // TODO(design): 083 的选项暂无备忘录字段，有备忘录时随归档包含，导入始终按 id 合并。
    static void ExportNotes(string folder,Dictionary<string,byte[]> contents,Dictionary<string,string> bodies)
    {
        if (!Directory.Exists(folder)) return;
        var rows = NoteDisk.Scan(folder);
        if (rows.Length == 0) return;
        NoteDisk.SaveIndex(folder,rows);
        contents.Add("memo/"+NoteDisk.Index,File.ReadAllBytes(Path.Combine(folder,NoteDisk.Index)));
        foreach (var row in rows) bodies.Add("memo/"+row.FileName,Path.Combine(folder,row.FileName));
    }

    static NoteFile[] ImportNotes(string root)
    {
        var rows = NoteDisk.ReadIndex(root);
        var names = NoteDisk.Bodies(root).ToDictionary(f => f.Name,StringComparer.OrdinalIgnoreCase);
        if (names.Count != rows.Length || rows.Any(r => !names.TryGetValue(r.FileName,out var f) || f.Length != r.Size)) throw Invalid();
        return rows;
    }

    static void MergeNotes(string data,string pending,string output,Dictionary<string,string?> writes)
    {
        string source = Path.Combine(pending,"memo");
        if (!Directory.Exists(source)) return;
        string target = MemoFolder(data);
        var local = NoteDisk.Scan(target).ToDictionary(r => r.Id);
        var occupied = local.Values.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ImportNotes(source))
        {
            var prior = local.GetValueOrDefault(row.Id);
            if (prior != null && prior.Modified >= row.Modified) continue;
            string name = row.FileName;
            if (prior != null) occupied.Remove(prior.FileName);
            string body = Path.Combine(source,row.FileName);
            if (occupied.Contains(name) || (File.Exists(Path.Combine(target,name)) && !string.Equals(prior?.FileName,name,StringComparison.OrdinalIgnoreCase)))
                name = NoteDisk.FileName(new Note(row.Id,File.ReadAllText(body),row.Pinned,row.Created,row.Modified,row.From),
                    n => occupied.Contains(n) || File.Exists(Path.Combine(target,n)));
            if (prior != null && !string.Equals(prior.FileName,name,StringComparison.OrdinalIgnoreCase))
                writes[Path.Combine(target,prior.FileName)] = null;
            occupied.Add(name);
            File.SetLastWriteTimeUtc(body,row.LastWrite);
            local[row.Id] = row with { FileName = name, LastWrite = File.GetLastWriteTimeUtc(body) };
            writes[Path.Combine(target,name)] = body;
        }
        string index = Path.Combine(output,NoteDisk.Index);
        NoteDisk.Atomic(index,NoteDisk.Encode(local.Values),true);
        writes[Path.Combine(target,NoteDisk.Index)] = index;
    }
}
