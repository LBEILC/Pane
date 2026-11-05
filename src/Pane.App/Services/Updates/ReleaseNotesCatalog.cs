using System.Text.Json;
using System.Text.Json.Serialization;
using Pane.Contracts;
using Pane.Services.Setup;

namespace Pane.Services.Updates;

internal sealed record NoteData(string Version, string? Date, NoteItemData[] Items);
internal sealed record NoteItemData(string Kind, string Text, bool Highlight = false);
[JsonSerializable(typeof(NoteData))]
[JsonSerializable(typeof(CachedUpdate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ReleaseNotesJson : JsonSerializerContext { }

internal sealed class ReleaseNotesCatalog : IReleaseNotesCatalog
{
    public IReadOnlyList<ReleaseNotes> All { get; }
    public ReleaseNotesCatalog(Action<string> log) : this(ReadResources(), log) { }
    internal ReleaseNotesCatalog(IEnumerable<(string Name, Func<Stream> Open)> files, Action<string> log)
    {
        var notes = new List<ReleaseNotes>();
        foreach (var file in files)
        {
            try
            {
                using var stream = file.Open();
                using var json = JsonDocument.Parse(stream);
                // 汇总中一版损坏不影响其他版本；也支持单份说明供测试使用。
                var entries = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
                foreach (var entry in entries)
                    try
                    {
                        var data = entry.Deserialize(ReleaseNotesJson.Default.NoteData) ?? throw new JsonException();
                        _ = SetupFiles.CompareVersions(data.Version, data.Version);
                        var items = data.Items.Select(i => new ReleaseNoteItem(i.Kind switch {
                            "new" => ReleaseNoteKind.New, "improved" => ReleaseNoteKind.Improved, "fixed" => ReleaseNoteKind.Fixed,
                            _ => throw new JsonException() }, string.IsNullOrWhiteSpace(i.Text) ? throw new JsonException() : i.Text, i.Highlight)).ToArray();
                        if (notes.Any(n => SetupFiles.CompareVersions(n.Version, data.Version) == 0)) throw new JsonException();
                        notes.Add(new(data.Version, DateOnly.TryParse(data.Date, out var date) ? date : null, items));
                    }
                    catch (Exception e) when (Invalid(e)) { log($"release-notes skipped {file.Name}: {e.GetType().Name}"); }
            }
            catch (Exception e) when (Invalid(e)) { log($"release-notes skipped {file.Name}: {e.GetType().Name}"); }
        }
        All = notes.OrderByDescending(n => n.Version, Comparer<string>.Create(SetupFiles.CompareVersions)).ToArray();
    }
    static bool Invalid(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException;
    static IEnumerable<(string, Func<Stream>)> ReadResources()
    {
        var assembly = typeof(ReleaseNotesCatalog).Assembly;
        yield return ("catalog.json", () => assembly.GetManifestResourceStream("Pane.ReleaseNotes.json") ?? throw new IOException());
    }
    public ReleaseNotes? Find(string version)
    {
        try { return All.FirstOrDefault(n => SetupFiles.CompareVersions(n.Version, version) == 0); }
        catch (Exception e) when (Invalid(e)) { return null; }
    }
    internal static ReleaseNotes? ParseMarkdown(string body, string version, DateTimeOffset published)
    {
        ReleaseNoteKind? kind = null;
        var items = new List<ReleaseNoteItem>();
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                kind = line switch { "## 新增" => ReleaseNoteKind.New, "## 改进" => ReleaseNoteKind.Improved, "## 修复" => ReleaseNoteKind.Fixed, _ => null };
                continue;
            }
            if (kind is {} k && line.Length > 2 && line[0] is '-' or '*' && char.IsWhiteSpace(line[1]) && !string.IsNullOrWhiteSpace(line[2..]))
                items.Add(new(k, line[2..].Trim()));
        }
        return items.Count == 0 ? null : new(version, DateOnly.FromDateTime(published.Date), items);
    }
}
