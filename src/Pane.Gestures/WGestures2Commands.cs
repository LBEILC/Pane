using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pane.Gestures;

internal static partial class WGestures2Commands
{
    [GeneratedRegex(@"\{[^}]*\}|#")]
    private static partial Regex KeyToken();
    [GeneratedRegex(@"%WG_[^%\s]+%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetVariable();

    internal static GestureCommand Keys(string sequence)
    {
        // 仅 WG2 把裸 # 解释为 Win；保留普通 Pane 文本及花括号内的井号。
        sequence = KeyToken().Replace(sequence, m => m.Value == "#" ? "{WINMOD}" : m.Value);
        var strokes = SendKeysParser.Parse(sequence, importingWg2: true);
        if (strokes.Count == 0) throw new NotSupportedException("按键为空");
        KeyChord Chord(KeyStroke s)
        {
            if (s.Key != 0) return new KeyChord(s.Modifiers, s.Key);
            var (key, modifiers) = WindowsSystem.CharacterKey(s.Character);
            return new KeyChord(s.Modifiers | modifiers, key);
        }
        if (strokes.Any(s => s.DelayMs.HasValue))
        {
            foreach (var stroke in strokes)
                if (!stroke.DelayMs.HasValue) _ = Chord(stroke);
            return new SendTextCommand(sequence);
        }
        return new HotkeyCommand(strokes.Select(Chord).ToArray());
    }

    internal static GestureCommand Read(JsonElement c)
    {
        string type = WGestures2Importer.Text(c, "$type");
        string Field(string key) => WGestures2Importer.Text(c, key);
        bool Flag(string key) => c.TryGetProperty(key, out var value) && value.GetBoolean();
        if (type is "Cmd" or "RunApp" && Flag("AsAdmin")) throw new NotSupportedException("以管理员身份运行");
        switch (type)
        {
            case "SendKeys":
                if (Field("Mode") == "Mapping") throw new NotSupportedException("按住期间映射按键");
                return Keys(Field("Keys"));
            case "RunApp":
                string line = Field("Cmd").Trim();
                string path = Field("Path");
                if (line.Length > 0)
                {
                    int end = line[0] == '"' ? line.IndexOf('"', 1) : -1;
                    if (line[0] == '"' && end < 0) throw new FormatException("程序路径引号不完整");
                    if (end >= 0 && end + 1 < line.Length || end < 0 && line.Any(char.IsWhiteSpace))
                        return new RunCommand(Variables(line), false, true);
                    if (string.IsNullOrWhiteSpace(path)) path = end >= 0 ? line[1..end] : line;
                }
                if (string.IsNullOrWhiteSpace(path)) throw new FormatException("程序路径为空");
                return new OpenCommand(path);
            case "Cmd":
                string code = Field("Code");
                if (string.IsNullOrWhiteSpace(code)) throw new FormatException("命令行为空");
                return new RunCommand(Variables(code), Flag("AutoSetWorkDir"), true);
            case "WindowControl":
                return new WindowCommand(Field("Command") switch
                {
                    "WINDOW_MINIMIZE" => WindowAction.Minimize,
                    "WINDOW_TOGGLE_MAXIMIZE" => WindowAction.MaximizeRestore,
                    "WINDOW_CLOSE" => WindowAction.Close,
                    "WINDOW_TOGGLE_TOPMOST" => WindowAction.ToggleTopmost,
                    var value => throw new NotSupportedException($"未知窗口操作 {value}")
                });
            case "Script": throw new NotSupportedException("Lua 脚本");
            default: throw new NotSupportedException($"不支持命令 {type}");
        }
    }

    static string Variables(string code) => TargetVariable().Replace(code, match => match.Value.ToUpperInvariant() switch
    {
        "%WG_TARGET_PID%" => "%WG_PROCID%", "%WG_TARGET_WID%" => "%WG_WINID%",
        "%WG_PROCID%" or "%WG_WINID%" or "%WG_STARTPOINT_X%" or "%WG_STARTPOINT_Y%"
            or "%WG_ENDPOINT_X%" or "%WG_ENDPOINT_Y%" => match.Value.ToUpperInvariant(),
        _ => throw new NotSupportedException($"未知变量 {match.Value}")
    });
}
