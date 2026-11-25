using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pane.Settings;

namespace Pane.Services.Cloud;

internal sealed class SyncRecord
{
    public string Category { get; set; } = "";
    public long Base { get; set; }
    public string Device { get; set; } = "";
    public JsonObject? Body { get; set; }
    // 旧日志中的待发记录和冲突合并后的记录都必须遵守当前的同步范围。
    bool dirty;
    public bool Dirty
    {
        get => dirty && !(Category == "settings" && Body?["k"] is JsonValue key &&
            key.TryGetValue<string>(out var path) && SyncData.IsLocal(path));
        set => dirty = value;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoteBaseHash { get; set; }
    public long DeletedAt { get; set; }
    public string? LocalProgram { get; set; }
    public bool DeleteFirst { get; set; }
    public JsonObject? Unknown { get; set; }
}

internal sealed class SyncJournal
{
    public string? Account { get; set; }
    public string? KeyId { get; set; }
    public long Cursor { get; set; }
    public bool NotesEnabled { get; set; }
    public bool SettingsEnabled { get; set; }
    public bool ClipboardEnabled { get; set; }
    public bool UsageEnabled { get; set; }
    public bool Initialized { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public Dictionary<string,SyncRecord> Records { get; set; } = [];
    public JsonObject Settings { get; set; } = new();
    public JsonObject? Usage { get; set; }

    internal static SyncJournal Read(string file) => File.Exists(file)
        ? JsonSerializer.Deserialize(File.ReadAllText(file), JournalJson.Default.SyncJournal) ?? throw new JsonException() : new();
    internal void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var stream = File.Create(file + ".tmp"))
        {
            JsonSerializer.Serialize(stream, this, JournalJson.Default.SyncJournal);
            stream.Flush(true);
        }
        File.Move(file + ".tmp", file, true);
    }
}

[JsonSerializable(typeof(SyncJournal))]
internal partial class JournalJson : JsonSerializerContext { }

internal static class SyncData
{
    // TODO(design): 本机环境和绑定本机条目标识的设置先留在本机，待设计确认完整清单。
    static readonly HashSet<string> Local = [
        .. LocalSettingsFields.Paths.Select(p => string.Join('.', p)),
        "Version", "SuperPanel.DisabledPrograms", "Modules", "General.UiFont", "Clipboard.KeepHotkeyWithWinV",
        "Launcher.Width", "Launcher.Portable", "Launcher.Favorites", "Launcher.PinnedApps",
        "Launcher.AppAliases", "Launcher.HiddenItems", "Icons.CustomIcons", "Icons.Overrides",
        "Ai.ProxyMode", "Ai.ProxyUrl", "Ai.ApiEndpoint", "Ai.Endpoints", "Ai.ApiBaseUrl", "Ai.ApiModel",
        "Update.Source", "Update.IgnoredVersion", "Update.NewsShown", "Update.AutoInstallFailedVersion", "Update.SeenVersion",
        "Ai.Disabled", "SuperPanel.Enabled", "FolderJump.Enabled", "Clipboard.Enabled"
    ];
    internal static bool IsLocal(string path) => Local.Contains(path) || path.StartsWith("Modules.", StringComparison.Ordinal);
    internal static JsonObject Leaves(PaneSettings settings)
    {
        var flat = new JsonObject();
        void Walk(string path, JsonNode? value)
        {
            if (IsLocal(path)) return;
            if (value is JsonObject obj && !path.EndsWith("Hotkey", StringComparison.Ordinal))
                foreach (var (name, child) in obj) Walk(path.Length == 0 ? name : path + "." + name, child);
            else flat[path] = value?.DeepClone();
        }
        Walk("", JsonSerializer.SerializeToNode(settings, SettingsJson.Default.PaneSettings));
        return flat;
    }
    // 新版枚举值等留在同步记录中，其他可识别设置仍可应用。先整批校验，保留相互依赖的字段。
    internal static PaneSettings Apply(PaneSettings current, JsonObject patch)
    {
        try { return ApplyBatch(current,patch); }
        catch(Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            foreach(var group in patch.GroupBy(p => p.Key.Split('.')[0]))
            {
                var part = new JsonObject();
                foreach(var (name,value) in group) part[name] = value?.DeepClone();
                try { current = ApplyBatch(current,part); }
                catch(Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    foreach(var (name,value) in part)
                        try { current = ApplyBatch(current,new JsonObject { [name] = value?.DeepClone() }); }
                        catch(Exception invalid) when (invalid is JsonException or ArgumentException or InvalidOperationException) { }
                }
            }
            return current;
        }
    }
    static PaneSettings ApplyBatch(PaneSettings current, JsonObject patch)
    {
        var root = JsonSerializer.SerializeToNode(current, SettingsJson.Default.PaneSettings)!.AsObject();
        foreach (var (path, value) in patch)
        {
            if (IsLocal(path)) continue;
            var names = path.Split('.');
            var parent = root;
            foreach (string name in names[..^1])
            {
                if (parent[name] == null) parent[name] = new JsonObject();
                parent = parent[name]!.AsObject();
            }
            parent[names[^1]] = value?.DeepClone();
        }
        var next = root.Deserialize(SettingsJson.Default.PaneSettings)!;
        SettingsStore.Validate(next);
        return next with { SyncOptions = current.SyncOptions };
    }

    static JsonNode Read(string directory, string file, JsonNode empty) =>
        File.Exists(Path.Combine(directory,file)) ? JsonNode.Parse(File.ReadAllText(Path.Combine(directory,file)))! : empty;
    internal static JsonObject Usage(string directory)
    {
        var counts = new JsonObject();
        foreach (var (id, item) in Read(directory,"launcher-usage.json",new JsonObject()).AsObject())
            counts[id] = (int)item!["Count"]!;
        // TODO(design): 协议未指定终端历史的字段名，v1 使用 history，顺序沿用本机最近优先。
        return new() { ["counts"] = counts, ["history"] = Read(directory,"terminal-history.json",new JsonArray()) };
    }
    internal static void ReplaceUsage(SettingsStore store, JsonObject? body)
    {
        var counts = new JsonObject();
        foreach (var (id, count) in body?["counts"]?.AsObject() ?? new())
            counts[id] = new JsonObject { ["Count"] = (int)count!, ["LastUsed"] = DateTimeOffset.UnixEpoch };
        void Write(string name, JsonNode value)
        {
            var file = Path.Combine(store.DirectoryPath, name);
            File.WriteAllText(file + ".sync-tmp", value.ToJsonString());
            File.Move(file + ".sync-tmp", file, true);
        }
        Write("launcher-usage.json",counts);
        Write("terminal-history.json",body?["history"] ?? new JsonArray());
        store.NotifyUsageReplaced();
    }
}
