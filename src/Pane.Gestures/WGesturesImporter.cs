using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Pane.Gestures;

public sealed class WGesturesImporter : IWGesturesImporter
{
    public ImportPreview? Preview(string? directory = null)
    {
        directory ??= FindDirectory();
        if (directory is null) return null;
        var (config, unsupported, version) = Read(directory, true);
        using var source=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"gestures.wg2")));
        return new(directory, version, source.RootElement.GetProperty("Global").GetProperty("GestureIntents").GetArrayLength(), config.Apps.Count,
            config.Apps.Count(a => a.Mode == AppRuleMode.Disabled), unsupported,
            config.Settings.TrailColors.Any(p=>GestureConfigStore.Empty.Settings.TrailColors[p.Key]!=p.Value));
    }
    public GestureConfig Import(ImportPreview preview, bool keepTrailColors) => Read(preview.SourceDirectory, keepTrailColors).Config;

    static string? FindDirectory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YingDev.com", "WGestures");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root).OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "gestures.wg2")) && File.Exists(Path.Combine(d, "config.plist")));
    }

    static (GestureConfig Config, List<UnsupportedItem> Unsupported, string Version) Read(string directory, bool colors)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gestures.wg2")));
        var root = doc.RootElement;
        var unsupported = new List<UnsupportedItem>();
        string version = Text(root, "FileVersion");
        if (version != "3") throw new InvalidDataException($"不支持 WGestures 文件版本 {version}");
        var global = Bindings(root.GetProperty("Global"), "全局", unsupported);
        var apps = new List<AppRule>();
        foreach (var prop in root.GetProperty("Apps").EnumerateObject())
        {
            var a = prop.Value;
            bool enabled = Bool(a, "IsGesturingEnabled", true);
            if (enabled && !Bool(a, "InheritGlobalGestures", true))
                unsupported.Add(new(prop.Name, "程序规则", "不继承全局的独立规则暂无法用接口草案表达"));
            apps.Add(new(Text(a, "ExecutablePath", prop.Name), null,
                enabled ? AppRuleMode.InheritAndOverride : AppRuleMode.Disabled, Bindings(a, prop.Name, unsupported)));
        }
        using var reader = XmlReader.Create(Path.Combine(directory, "config.plist"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var elements = XDocument.Load(reader).Root!.Element("dict")!.Elements().ToArray();
        var settings = new Dictionary<string, XElement>();
        for (int i = 0; i + 1 < elements.Length; i += 2) settings[elements[i].Value] = elements[i + 1];
        bool Flag(string key, bool fallback) => settings.TryGetValue(key, out var e) ? e.Name.LocalName == "true" : fallback;
        int Number(string key, int fallback) => settings.TryGetValue(key, out var e) && int.TryParse(e.Value, out var n) ? n : fallback;
        var defaults = GestureConfigStore.Empty.Settings;
        var palette = new Dictionary<GestureButton, string>(defaults.TrailColors);
        if (colors)
        {
            foreach (var (button, key) in new[] { (GestureButton.Right, "GestureViewMainPathColor"), (GestureButton.Middle, "GestureViewMiddleBtnMainColor"), (GestureButton.X1, "GestureViewXBtnPathColor"), (GestureButton.X2, "GestureViewXBtnPathColor") })
                if (settings.TryGetValue(key, out var e) && int.TryParse(e.Value, out var argb)) palette[button] = $"#{argb & 0xffffff:x6}";
        }
        KeyChord? hotkey = defaults.PauseHotkey;
        if (settings.TryGetValue("PauseResumeHotKey", out var hot))
        {
            var bytes = Convert.FromBase64String(hot.Value);
            if (bytes.Length != 8) throw new InvalidDataException("暂停热键数据长度无效");
            // WGestures 的热键修饰值使用 Win32 MOD_ALT=1、CONTROL=2。
            int m = BitConverter.ToInt32(bytes, 4);
            hotkey = new((KeyModifiers)(((m & 1) != 0 ? 2 : 0) | ((m & 2) != 0 ? 1 : 0) | (m & 12)), (ushort)BitConverter.ToInt32(bytes, 0));
        }
        var config = new GestureConfig(1, new((GestureButton)Number("PathTrackerTriggerButton", 1),
            Flag("GestureParserDisableInFullScreenMode", true), Flag("GestureViewShowPath", true),
            Flag("GestureViewFadeOut", true), Flag("GestureViewShowCommandName", true),
            Flag("PathTrackerStayTimeout", false) ? Number("PathTrackerStayTimeoutMillis", 1200) : null,
            hotkey, palette), global, apps);
        GestureConfigStore.Validate(config);
        return (config, unsupported, version);
    }

    static List<GestureBinding> Bindings(JsonElement app, string where, List<UnsupportedItem> unsupported)
    {
        var result = new List<GestureBinding>();
        foreach (var b in app.GetProperty("GestureIntents").EnumerateArray())
        {
            string name = Text(b, "Name");
            try
            {
                var g = b.GetProperty("Gesture");
                var pattern = new GesturePattern((GestureButton)g.GetProperty("GestureButton").GetInt32(),
                    g.GetProperty("Dirs").EnumerateArray().Select(d => (GestureDirection)d.GetInt32()).ToArray(),
                    (GestureModifier)g.GetProperty("Modifier").GetInt32());
                bool immediate = Bool(b, "ExecuteOnModifier", false);
                var binding=new GestureBinding(name, pattern, Command(b.GetProperty("Command"), immediate), immediate);
                GestureConfigStore.Validate(GestureConfigStore.Empty with{Global=[binding]});
                result.Add(binding);
            }
            catch (Exception e) when (e is NotSupportedException or FormatException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
            { unsupported.Add(new(where, name, e.Message)); }
        }
        return result;
    }

    static GestureCommand Command(JsonElement c, bool immediate)
    {
        string type = Text(c, "$type").Split(',')[0].Split('.').Last();
        switch (type)
        {
            case "HotKeyCommand":
                KeyModifiers modifiers = 0;
                foreach (var k in c.GetProperty("Modifiers").EnumerateArray())
                    modifiers |= k.GetInt32() switch { 16 or 160 or 161 => KeyModifiers.Shift, 17 or 162 or 163 => KeyModifiers.Ctrl, 18 or 164 or 165 => KeyModifiers.Alt, 91 or 92 => KeyModifiers.Win, _ => throw new NotSupportedException("未知键盘修饰键") };
                var keys = c.GetProperty("Keys").EnumerateArray().Select(k => (ushort)k.GetInt32()).ToArray();
                // 连续菜单序列仅第一键带 Alt，其余键在菜单内逐个按下松开。
                return new HotkeyCommand(keys.Length == 0 ? [new(modifiers, 0)] : keys.Select((k, i) => new KeyChord(i == 0 ? modifiers : KeyModifiers.None, k)).ToArray());
            case "SendTextCommand":
                string sequence = Text(c, "Text");
                SendKeysParser.Parse(sequence);
                return new SendTextCommand(sequence);
            case "WebSearchCommand": return new WebSearchCommand(Text(c, "SearchEngineUrl").Replace("{0}", "{query}"));
            case "CmdCommand": return new RunCommand(Text(c, "Code"), Bool(c, "AutoSetWorkingDir", true), Bool(c, "ShowWindow", true));
            case "WindowControlCommand":
                int action = c.GetProperty("ChangeWindowStateTo").GetInt32();
                if (action is < 0 or > 5) throw new NotSupportedException("未知窗口操作");
                return new WindowCommand((WindowAction)action);
            case "ChangeAudioVolumeCommand": return new VolumeCommand(immediate ? VolumeAction.WheelAdjust : VolumeAction.ToggleMute, c.TryGetProperty("Delta", out var delta) ? Math.Max(1, delta.GetInt32()) : 1);
            case "TaskSwitcherCommand": return new TaskSwitchCommand();
            case "OpenFileCommand": return new OpenCommand(Text(c, "FilePath"));
            case "GotoUrlCommand": return new OpenCommand(Text(c, "Url"));
            case "PauseWGesturesCommand": return new PauseGesturesCommand();
            case "DoNothingCommand": return new DoNothingCommand();
            default: throw new NotSupportedException($"不支持命令 {type}");
        }
    }
    static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    static bool Bool(JsonElement e, string key, bool fallback) => e.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
}
