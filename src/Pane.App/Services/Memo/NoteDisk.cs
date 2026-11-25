using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;

namespace Pane.Services.Memo;

internal sealed record NoteFile(string Id, string FileName, bool Pinned, DateTimeOffset Created,
    DateTimeOffset Modified, string? From, long Size, DateTime LastWrite);

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

    internal static NoteFile[] ReadIndex(string folder)
    {
        var rows = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(folder, Index)), MemoJson.Default.NoteFileArray)
            ?? throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (rows.Any(r => r == null || !ValidId(r.Id) || !BodyName(r.FileName) || !names.Add(r.FileName) || !ids.Add(r.Id)))
            throw new InvalidDataException();
        return rows;
    }

    internal static IEnumerable<FileInfo> Bodies(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) return [];
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("备忘录目录为重解析点");
        return dir.EnumerateFiles("*.md").Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && BodyName(f.Name));
    }

    internal static NoteFile[] Scan(string folder, Action<string>? log = null)
    {
        var rows = Array.Empty<NoteFile>();
        if (File.Exists(Path.Combine(folder, Index)))
        {
            try { rows = ReadIndex(folder); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            { string message = $"备忘录索引损坏，按正文重建：{e.GetType().Name}"; System.Diagnostics.Trace.TraceWarning(message); log?.Invoke(message); }
        }
        var known = rows.ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
        return Bodies(folder).Select(f =>
        {
            if (!known.TryGetValue(f.Name, out var row))
                return new NoteFile(NewId(), f.Name, false, f.CreationTimeUtc, f.LastWriteTimeUtc, null, f.Length, f.LastWriteTimeUtc);
            return row.Size == f.Length && row.LastWrite == f.LastWriteTimeUtc ? row :
                row with { Size = f.Length, LastWrite = f.LastWriteTimeUtc, Modified = f.LastWriteTimeUtc };
        }).ToArray();
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

    internal static void SaveIndex(string folder, NoteFile[] rows)
    {
        string path = Path.Combine(folder,Index);
        byte[] bytes = Encode(rows);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) Atomic(path,bytes,true);
    }

    internal static byte[] Encode(IEnumerable<NoteFile> rows) =>
        JsonSerializer.SerializeToUtf8Bytes(rows.ToArray(), MemoJson.Default.NoteFileArray);
}
