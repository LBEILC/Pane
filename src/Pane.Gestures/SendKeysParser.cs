namespace Pane.Gestures;

public readonly record struct KeyStroke(ushort Key, char Character, KeyModifiers Modifiers)
{
    internal int? DelayMs { get; init; }
}

// 解析与发送分离；导入预览和假系统测试使用同一份解析结果。
public static class SendKeysParser
{
    public static IReadOnlyList<KeyStroke> Parse(string sequence) => Parse(sequence, false);

    internal static IReadOnlyList<KeyStroke> Parse(string sequence, bool importingWg2)
    {
        var output = new List<KeyStroke>();
        int index = 0;
        void Read(KeyModifiers inherited, int depth)
        {
            if (importingWg2 && depth > 64) throw new FormatException("按键组嵌套过深");
            while (index < sequence.Length)
            {
                if (importingWg2 && output.Count >= 65536) throw new FormatException("按键序列过长");
                if (sequence[index] == ')') { if (depth == 0) throw new FormatException("多余的右括号"); index++; return; }
                var mods = inherited;
                while (index < sequence.Length)
                {
                    if ("^%+".Contains(sequence[index]))
                        mods |= sequence[index++] switch { '^' => KeyModifiers.Ctrl, '%' => KeyModifiers.Alt, _ => KeyModifiers.Shift };
                    else if (sequence.AsSpan(index).StartsWith("{WINMOD}", StringComparison.OrdinalIgnoreCase))
                    { mods |= KeyModifiers.Win; index += 8; }
                    else break;
                }
                if (index == sequence.Length) throw new FormatException("修饰符后缺少按键");
                char c = sequence[index++];
                if (c == '(') { Read(mods, depth + 1); continue; }
                if (c == '{')
                {
                    int end = sequence.IndexOf('}', index);
                    if (end == index && end + 1 < sequence.Length && sequence[end + 1] == '}') end++;
                    if (end < 0) throw new FormatException("按键名缺少右花括号");
                    string token = sequence[index..end]; index = end + 1;
                    var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || parts.Length > 2) throw new FormatException("无效按键表达式");
                    int count=1;
                    if(parts.Length==2&&!int.TryParse(parts[1],out count))throw new FormatException("按键重复次数不是整数");
                    string name = parts[0].ToUpperInvariant();
                    if (name == "SLEEP" && parts.Length == 2 && count >= 0)
                    {
                        output.Add(new KeyStroke { DelayMs = Math.Min(count, 1000) });
                        continue;
                    }
                    if (count < 1 || count > 10000 || importingWg2 && output.Count + count > 65536) throw new FormatException("按键重复次数无效");
                    ushort key = name switch {
                        "ENTER" or "RETURN" => 13, "TAB" => 9, "ESC" or "ESCAPE" => 27, "SPACE" => 32,
                        "LWIN" or "WIN" => 91, "LCTRL" or "CTRL" => 162,
                        "LSHIFT" or "SHIFT" => 160, "LALT" or "ALT" => 164,
                        "VOLUME_UP" => 175, "VOLUME_DOWN" => 174, "VOLUME_MUTE" => 173,
                        "MEDIA_PLAY" => 179, "MEDIA_NEXT" => 176, "MEDIA_PREV" => 177, "MEDIA_STOP" => 178,
                        "BACKSPACE" or "BS" or "BKSP" => 8, "DELETE" or "DEL" => 46,
                        "INSERT" or "INS" => 45, "HOME" => 36, "END" => 35, "PGUP" => 33, "PGDN" => 34,
                        "LEFT" => 37, "UP" => 38, "RIGHT" => 39, "DOWN" => 40,
                        "CAPSLOCK" => 20, "NUMLOCK" => 144, "SCROLLLOCK" => 145,
                        "PRTSC" => 44, "BREAK" => 3, "HELP" => 47, "ADD" => 107,
                        "SUBTRACT" => 109, "MULTIPLY" => 106, "DIVIDE" => 111, _ => 0 };
                    if (name.StartsWith('F') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) key = (ushort)(111 + f);
                    if (key == 0 && name.Length != 1) throw new NotSupportedException($"不支持 SendKeys 按键 {name}");
                    if (count == 1 && key is 91 or 162 or 160 or 164 && index < sequence.Length && sequence[index] == '(')
                    {
                        index++;
                        if (index < sequence.Length && sequence[index] == ')') index++;
                        else
                        {
                            var held = key switch { 91 => KeyModifiers.Win, 162 => KeyModifiers.Ctrl, 160 => KeyModifiers.Shift, _ => KeyModifiers.Alt };
                            Read(mods | held, depth + 1);
                            continue;
                        }
                    }
                    for (int j = 0; j < count; j++) output.Add(new(key, key == 0 ? parts[0][0] : '\0', mods));
                }
                else if (c == '~') output.Add(new(13, '\0', mods));
                else if (c is '}' or '[' or ']') throw new NotSupportedException("特殊字符请用花括号转义");
                else output.Add(new(0, c, mods));
            }
            if (depth > 0) throw new FormatException("按键组缺少右括号");
        }
        Read(KeyModifiers.None, 0);
        return output;
    }
}
