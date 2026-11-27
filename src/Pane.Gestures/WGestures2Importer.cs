using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Pane.Gestures;

internal static class WGestures2Importer
{
    const int EntryLimit = 32 * 1024 * 1024;
    internal static WGesturesImporter.ReadResult Read(Stream stream, bool colors)
    {
        string version;
        JsonDocument gestures;
        JsonDocument? prefs;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var versionEntry = zip.GetEntry("Version") ?? throw new InvalidDataException();
            var gestureEntry = zip.GetEntry("gestures.json") ?? throw new InvalidDataException();
            version = Content(versionEntry).Trim();
            if (version.Split('.')[0] != "2")
                throw new UnsupportedVersion(version);
            prefs = zip.GetEntry("prefs.json") is { } entry ? JsonDocument.Parse(Content(entry)) : null;
            gestures = JsonDocument.Parse(Content(gestureEntry));
        }
        catch (UnsupportedVersion e) { throw new InvalidDataException(e.Message); }
        catch (Exception e) when (e is InvalidDataException or JsonException or DecoderFallbackException)
        { throw new InvalidDataException("不是 WGestures 导出的文件", e); }
        using (gestures)
        using (prefs)
        {
            try { return Convert(gestures.RootElement, prefs?.RootElement ?? default, version, colors); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("不是 WGestures 导出的文件", e); }
        }
    }

    sealed class UnsupportedVersion(string version) : Exception($"不支持的 WGestures 2 版本 {version}");

    static string Content(ZipArchiveEntry entry)
    {
        if (entry.Length > EntryLimit) throw new InvalidDataException("条目超过 32 MiB");
        using var source = entry.Open();
        using var bytes = new MemoryStream((int)entry.Length);
        var buffer = new byte[8192];
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            if (bytes.Length + n > entry.Length) throw new InvalidDataException("条目长度不一致");
            bytes.Write(buffer, 0, n);
        }
        if (bytes.Length != entry.Length) throw new InvalidDataException("条目不完整");
        return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimStart('\uFEFF');
    }

    internal static string Text(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    static IEnumerable<JsonElement> Items(JsonElement target, string property)
    {
        if (!target.TryGetProperty(property, out var list) || list.ValueKind == JsonValueKind.Null) yield break;
        if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in list.EnumerateObject()) yield return item.Value;
        }
        else foreach (var item in list.EnumerateArray()) yield return item;
    }

    static WGesturesImporter.ReadResult Convert(JsonElement root, JsonElement prefs, string version, bool colors)
    {
        var unsupported = new List<UnsupportedItem>();
        var approximated = new List<ApproximatedItem>();
        var general = root.GetProperty("General");
        var global = new List<GestureBinding>();
        var byPath = new Dictionary<string, List<GestureBinding>>(StringComparer.OrdinalIgnoreCase);

        int Bind(JsonElement target, string where, List<GestureBinding> bindings, string? reason = null)
        {
            int count = 0;
            foreach (var intent in Items(target, "Intents"))
            {
                count++;
                string name = Text(intent, "Name");
                try
                {
                    if (reason != null) throw new NotSupportedException(reason);
                    var (pattern, exact) = WGestures2Shape.Read(intent.GetProperty("Gesture"));
                    var command = WGestures2Commands.Read(intent.GetProperty("Command"));
                    var first = bindings.FirstOrDefault(b => GestureMatcher.Same(b.Pattern, pattern));
                    if (first != null) throw new NotSupportedException($"转换后和「{first.Name}」相同");
                    bindings.Add(new(name, pattern, command, pattern.Modifier is GestureModifier.WheelUp or GestureModifier.WheelDown));
                    if (!exact) approximated.Add(new(where, name, pattern));
                }
                catch (Exception e) when (e is NotSupportedException or FormatException or InvalidOperationException or KeyNotFoundException)
                { unsupported.Add(new(where, name, e.Message)); }
            }
            return count;
        }
        int globalCount = Bind(general, "全局", global);
        foreach (var target in Items(root, "Apps"))
        {
            string path = Text(target, "Path"), name = Text(target, "Name"), type = Text(target, "$type");
            if (type != "WinAppTarget" || string.IsNullOrWhiteSpace(path))
            {
                Bind(target, name, [], type != "WinAppTarget" ? $"未知程序目标 {type}" : "程序路径为空");
                continue;
            }
            if (!byPath.TryGetValue(path, out var bindings)) byPath[path] = bindings = [];
            Bind(target, name, bindings);
        }
        foreach (var target in Items(root, "Specials")) Bind(target, Text(target, "Name"), [], "桌面 / 任务栏专属手势");
        foreach (var target in Items(root, "Groups")) Bind(target, Text(target, "Name"), [], "分组");
        var apps = byPath.Where(p => p.Value.Count > 0).Select(p =>
            new AppRule(p.Key, null, AppRuleMode.InheritAndOverride, p.Value)).ToArray();
        var config = new GestureConfig(1, Settings(general, prefs, colors), global, apps);
        GestureConfigStore.Validate(config);
        return new(config, unsupported, version, globalCount, approximated);
    }

    static GestureSettings Settings(JsonElement general, JsonElement prefs, bool colors)
    {
        var defaults = GestureConfigStore.Empty.Settings;
        var buttons = defaults.EnabledButtons;
        if (general.TryGetProperty("Triggers", out _))
        {
            buttons = GestureButton.None;
            foreach (var trigger in Items(general, "Triggers"))
            {
                var def = trigger.GetProperty("Def");
                if (def.GetArrayLength() == 1 && Text(def[0], "$type") == "KeyDown")
                    buttons |= WGestures2Shape.Button(Text(def[0], "Key"));
            }
        }
        if (prefs.ValueKind == JsonValueKind.Undefined) return defaults with { EnabledButtons = buttons };
        bool Flag(string key, bool fallback) => prefs.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
        var hotkey = defaults.PauseHotkey;
        if (prefs.TryGetProperty("PauseResumeHotkey", out _))
        {
            hotkey = null;
            try
            {
                if (WGestures2Commands.Keys(Text(prefs, "PauseResumeHotkey")) is HotkeyCommand { Chords: [var chord] })
                    hotkey = chord;
            }
            catch (Exception e) when (e is FormatException or NotSupportedException) { }
        }
        var palette = defaults.TrailColors;
        string color = Text(prefs, "PathColorRecognized");
        if (colors && color.Length == 9 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
            palette = palette.Keys.ToDictionary(k => k, k => color[..7].ToLowerInvariant());
        return defaults with
        {
            EnabledButtons = buttons, PauseHotkey = hotkey, TrailColors = palette,
            PauseInFullscreen = Flag("DisableInFullscreen", defaults.PauseInFullscreen),
            ShowTrail = Flag("ShowPath", defaults.ShowTrail), FadeTrail = Flag("FadeOutExecuted", defaults.FadeTrail),
            ShowCommandName = Flag("ShowGestureName", defaults.ShowCommandName)
        };
    }
}
