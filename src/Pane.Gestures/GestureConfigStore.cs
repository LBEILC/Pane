using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pane.Gestures;

public static class GestureConfigStore
{
    public static GestureConfig LoadOrDefaults(string path)
    {
        try { return Load(path); }
        catch (FileNotFoundException) { return Defaults; }
        catch (DirectoryNotFoundException) { return Defaults; }
    }

    public static GestureConfig Defaults => Empty with { Global = [
        Default("后退", [GestureDirection.Left], KeyModifiers.Alt, 0x25),
        Default("前进", [GestureDirection.Right], KeyModifiers.Alt, 0x27),
        Default("回到顶部", [GestureDirection.Up], KeyModifiers.None, 0x24),
        Default("到底部", [GestureDirection.Down], KeyModifiers.None, 0x23),
        Default("关闭标签页", [GestureDirection.Down, GestureDirection.Right], KeyModifiers.Ctrl, 0x57),
        Default("重新打开标签页", [GestureDirection.Left, GestureDirection.Up], KeyModifiers.Ctrl | KeyModifiers.Shift, 0x54),
        Default("上一个标签页", [], KeyModifiers.Ctrl | KeyModifiers.Shift, 0x09, GestureModifier.WheelUp),
        Default("下一个标签页", [], KeyModifiers.Ctrl, 0x09, GestureModifier.WheelDown)
    ] };
    static GestureBinding Default(string name, GestureDirection[] directions, KeyModifiers keys, ushort key, GestureModifier modifier = GestureModifier.None)
        => new(name, new(GestureButton.Right, directions, modifier), new HotkeyCommand([new(keys, key)]), modifier != GestureModifier.None);

    public static GestureConfig Load(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.GestureConfig)
                ?? throw new InvalidDataException("配置为空");
            Validate(config);
            return config;
        }
        catch (JsonException e) { throw new InvalidDataException($"配置字段或类型无效：{e.Path}", e); }
    }

    public static void Save(string path, GestureConfig config)
    {
        Validate(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, ConfigJson.Default.GestureConfig));
        File.Move(temp, path, true);
    }

    public static void Validate(GestureConfig config)
    {
        if (config.Version != 1) throw new InvalidDataException($"不支持配置版本 {config.Version}");
        if (config.Settings is null || config.Global is null || config.Apps is null)
            throw new InvalidDataException("缺少配置必要字段");
        if (((int)config.Settings.EnabledButtons & ~15) != 0 || config.Settings.StationaryTimeoutMs is <= 0)
            throw new InvalidDataException("触发键或静止超时无效");
        if(config.Settings.TrailColors is null||config.Settings.TrailColors.Any(p=>p.Value is null||p.Value.Length!=7||p.Value[0]!='#'||!uint.TryParse(p.Value.AsSpan(1),System.Globalization.NumberStyles.HexNumber,null,out _)))
            throw new InvalidDataException("轨迹颜色必须为 #rrggbb");
        if(config.Settings.PauseHotkey is {} key&&(((int)key.Modifiers&~15)!=0||key.VirtualKey==0))throw new InvalidDataException("暂停热键无效");
        if(config.Apps.Any(a=>a is null||string.IsNullOrWhiteSpace(a.ExePath)||a.Bindings is null||!Enum.IsDefined(a.Mode)))throw new InvalidDataException("程序规则无效");
        foreach (var binding in config.Global.Concat(config.Apps.SelectMany(a => a.Bindings)))
        {
            if(binding is null)throw new InvalidDataException("手势条目为空");
            var p = binding.Pattern;
            if (p is null || p.Directions is null || binding.Command is null ||
                p.Button is not (GestureButton.Right or GestureButton.Middle or GestureButton.X1 or GestureButton.X2) ||
                p.Directions.Any(d => (uint)d > 7) || !Enum.IsDefined(p.Modifier))
                throw new InvalidDataException("手势定义无效");
            if(binding.Command is HotkeyCommand h&&(h.Chords is null||h.Chords.Any(k=>((int)k.Modifiers&~15)!=0)))throw new InvalidDataException("快捷键命令无效");
            if(binding.Command is VolumeCommand v&&(!Enum.IsDefined(v.Action)||v.Step<1||v.Step>100))throw new InvalidDataException("音量命令无效");
            if(binding.Command is WindowCommand w&&!Enum.IsDefined(w.Action))throw new InvalidDataException("窗口命令无效");
        }
    }

    public static GestureConfig Empty => new(1, new(GestureButton.Right, true, true, true, true, null,
        new(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, 87),
        new Dictionary<GestureButton, string> { [GestureButton.Right] = "#5874c9", [GestureButton.Middle] = "#5874c9", [GestureButton.X1] = "#5874c9", [GestureButton.X2] = "#5874c9" }), [], []);
}

[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GestureConfig))]
internal partial class ConfigJson : JsonSerializerContext;
