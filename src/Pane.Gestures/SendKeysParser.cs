namespace Pane.Gestures;

public readonly record struct KeyStroke(ushort Key, char Character, KeyModifiers Modifiers);

// 解析与发送分离；导入预览和假系统测试使用同一份解析结果。
public static class SendKeysParser
{
    public static IReadOnlyList<KeyStroke> Parse(string sequence)
    {
        var output = new List<KeyStroke>();
        int index = 0;
        void Read(KeyModifiers inherited, bool group)
        {
            while (index < sequence.Length)
            {
                if (sequence[index] == ')') { if (!group) throw new FormatException("多余的右括号"); index++; return; }
                var mods = inherited;
                while (index < sequence.Length && "^%+".Contains(sequence[index]))
                    mods |= sequence[index++] switch { '^' => KeyModifiers.Ctrl, '%' => KeyModifiers.Alt, _ => KeyModifiers.Shift };
                if (index == sequence.Length) throw new FormatException("修饰符后缺少按键");
                char c = sequence[index++];
                if (c == '(') { Read(mods, true); continue; }
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
                    if (count < 1 || count > 10000) throw new FormatException("按键重复次数无效");
                    string name = parts[0].ToUpperInvariant();
                    ushort key = name switch {
                        "ENTER" or "RETURN" => 13, "TAB" => 9, "ESC" or "ESCAPE" => 27,
                        "BACKSPACE" or "BS" or "BKSP" => 8, "DELETE" or "DEL" => 46,
                        "INSERT" or "INS" => 45, "HOME" => 36, "END" => 35, "PGUP" => 33, "PGDN" => 34,
                        "LEFT" => 37, "UP" => 38, "RIGHT" => 39, "DOWN" => 40,
                        "CAPSLOCK" => 20, "NUMLOCK" => 144, "SCROLLLOCK" => 145,
                        "PRTSC" => 44, "BREAK" => 3, "HELP" => 47, "ADD" => 107,
                        "SUBTRACT" => 109, "MULTIPLY" => 106, "DIVIDE" => 111, _ => 0 };
                    if (name.StartsWith('F') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) key = (ushort)(111 + f);
                    if (key == 0 && name.Length != 1) throw new NotSupportedException($"不支持 SendKeys 按键 {name}");
                    for (int j = 0; j < count; j++) output.Add(new(key, key == 0 ? parts[0][0] : '\0', mods));
                }
                else if (c == '~') output.Add(new(13, '\0', mods));
                else if (c is '}' or '[' or ']') throw new NotSupportedException("特殊字符请用花括号转义");
                else output.Add(new(0, c, mods));
            }
            if (group) throw new FormatException("按键组缺少右括号");
        }
        Read(KeyModifiers.None, false);
        return output;
    }
}
