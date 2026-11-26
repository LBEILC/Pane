using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;

namespace Pane.Services.Memo;

internal sealed record NoteFile(string Id, string FileName, bool Pinned, DateTimeOffset Created,
    DateTimeOffset Modified, string? From, long Size, DateTime LastWrite, string[]? Tags = null);

internal sealed record MemoTag(string Name, int? Color, long Created, long Modified, bool Deleted = false)
{
    internal static MemoTag[] Merge(IEnumerable<MemoTag> tags) => tags
        .GroupBy(t => t.Name,StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(t => t.Modified).ThenByDescending(t => t.Deleted)
            .ThenBy(t => t.Name,StringComparer.Ordinal).ThenBy(t => t.Color).ThenBy(t => t.Created).First())
        .OrderBy(t => t.Created).ThenBy(t => t.Name,StringComparer.Ordinal).ToArray();
}

internal sealed record MemoIndex(NoteFile[] Rows, MemoTag[] Definitions, Dictionary<string,string>? FileIds = null);

[JsonSerializable(typeof(MemoIndex))]
[JsonSerializable(typeof(MemoTag))]
[JsonSerializable(typeof(NoteFile[]))]
internal partial class MemoJson : JsonSerializerContext;

internal static class NoteDisk
{
    internal const string DirectoryName = "备忘录", Index = ".index.json";
    internal static string FolderFor(string data, LargeFilesLocation? location) =>
        Path.Combine(location is { IsDefault: false } ? location.Path : data, DirectoryName);
    internal static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static bool ValidId(string? id) => id is { Length: 22 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && "AQgw".Contains(id[^1]);
    internal static bool BodyName(string name) => !string.IsNullOrEmpty(name) && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Path.EndsInDirectorySeparator(name)
        && !Reserved(name);

    static bool Reserved(string name)
    {
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "NUL" or "PRN" or "AUX" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]);
    }

    internal static string FileName(Note note, Func<string,bool> occupied)
    {
        var title = string.Concat(note.Title.EnumerateRunes().Take(40)).Trim('.', ' ');
        title = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim('.', ' ');
        if (title.Length == 0) title = "无标题";
        if (Reserved(title)) title = "_" + title;
        for (int length = 8; length <= note.Id.Length; length++)
        {
            string name = $"{title} {note.Id[..length]}.md";
            if (!occupied(name)) return name;
        }
        throw new IOException("备忘录文件名已被占用");
    }

    internal static NoteFile[] ReadIndex(string folder) => Read(folder).Rows;

    internal static MemoIndex Read(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder,Index)));
        var index = doc.RootElement.ValueKind == JsonValueKind.Array
            ? new MemoIndex(doc.RootElement.Deserialize(MemoJson.Default.NoteFileArray)!, [])
            : doc.RootElement.Deserialize(MemoJson.Default.MemoIndex);
        if (index?.Rows == null || index.Definitions == null || index.Definitions.Any(t =>
            t == null || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 30 || t.Name != t.Name.Trim()))
            throw new InvalidDataException();
        if (index.Definitions.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Definitions.Length)
            throw new InvalidDataException();
        var rows = index.Rows;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (rows.Any(r => r == null || !ValidId(r.Id) || !BodyName(r.FileName) || !names.Add(r.FileName) || !ids.Add(r.Id)
            || r.Tags?.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 30 || n != n.Trim()) == true))
            throw new InvalidDataException();
        return index;
    }

    internal static IEnumerable<FileInfo> Bodies(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) return [];
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("备忘录目录为重解析点");
        return dir.EnumerateFiles("*.md").Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && BodyName(f.Name));
    }

    internal static NoteFile[] Scan(string folder, Action<string>? log = null) => ScanIndex(folder,log).Rows;

    // 卷序列号与文件编号在同卷改名时保持稳定，避免把同名或同内容的文件误认作原件。
    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    internal static string? Identity(string path)
    {
        using var handle = File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle,out var i) && (i.IndexHigh | i.IndexLow) != 0
            ? $"{i.Volume:X8}{i.IndexHigh:X8}{i.IndexLow:X8}" : null;
    }

    internal static MemoIndex ScanIndex(string folder, Action<string>? log = null)
    {
        MemoIndex index = new([],[]);
        if (File.Exists(Path.Combine(folder, Index)))
        {
            try { index = Read(folder); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            { string message = $"备忘录索引损坏，按正文重建：{e.GetType().Name}"; System.Diagnostics.Trace.TraceWarning(message); log?.Invoke(message); }
        }
        var bodies = Bodies(folder).ToArray();
        var known = index.Rows.ToDictionary(r => r.FileName,StringComparer.OrdinalIgnoreCase);
        var bodyIds = bodies.ToDictionary(f => f.Name,f => Identity(f.FullName),StringComparer.OrdinalIgnoreCase);
        var oldFiles = index.Rows.Where(r => index.FileIds?.ContainsKey(r.Id) == true)
            .ToLookup(r => index.FileIds![r.Id]);
        var matches = new Dictionary<string,NoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in bodies.Where(f => bodyIds[f.Name] != null).GroupBy(f => bodyIds[f.Name]!))
        {
            var candidates = oldFiles[group.Key].ToArray();
            if (candidates.Length == 1 && group.Count() == 1) matches[group.First().Name] = candidates[0];
        }
        var identities = new Dictionary<string,string>();
        var claimed = matches.Values.Select(r => r.Id).ToHashSet();
        var rows = bodies.Select(f =>
        {
            var identity = bodyIds[f.Name];
            if (!matches.TryGetValue(f.Name,out var row) &&
                known.TryGetValue(f.Name,out var sameName) && !claimed.Contains(sameName.Id)) row = sameName;
            row ??= new NoteFile(NewId(),f.Name,false,f.CreationTimeUtc,f.LastWriteTimeUtc,null,f.Length,f.LastWriteTimeUtc);
            if (identity != null) identities[row.Id] = identity;
            if (row.FileName != f.Name) row = row with { FileName = f.Name };
            return row.Size == f.Length && row.LastWrite == f.LastWriteTimeUtc ? row :
                row with { Size = f.Length, LastWrite = f.LastWriteTimeUtc, Modified = f.LastWriteTimeUtc };
        }).ToArray();
        return index with { Rows = rows, FileIds = identities };
    }

    internal static void Atomic(string path, byte[] bytes, bool hidden = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            if (hidden) File.SetAttributes(temp, FileAttributes.Hidden);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    internal static void SaveIndex(string folder, NoteFile[] rows) => SaveIndex(folder,new MemoIndex(rows,[]));

    internal static void SaveIndex(string folder, MemoIndex index)
    {
        string path = Path.Combine(folder,Index);
        byte[] bytes = Encode(index);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) Atomic(path,bytes,true);
    }

    internal static byte[] Encode(IEnumerable<NoteFile> rows) =>
        Encode(new MemoIndex(rows.ToArray(),[]));

    internal static MemoIndex MergeIndex(IEnumerable<NoteFile> rows, MemoIndex left, MemoIndex right)
    {
        var definitions = MemoTag.Merge(left.Definitions.Concat(right.Definitions));
        var lookup = definitions.ToDictionary(t => t.Name,StringComparer.OrdinalIgnoreCase);
        return new(rows.Select(row =>
        {
            if (row.Tags == null) return row;
            var names = row.Tags.Select(n => lookup.TryGetValue(n,out var t) ? (t.Deleted ? null : t.Name) : n)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return row with { Tags = names.Length == 0 ? null : names };
        }).ToArray(),definitions);
    }

    internal static byte[] Encode(MemoIndex index) =>
        JsonSerializer.SerializeToUtf8Bytes(index,MemoJson.Default.MemoIndex);
}
