using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Pane.Gestures;

static partial class Program
{
    static JsonObject Key(string key) => new() { ["$type"] = "KeyDown", ["Key"] = key };
    static JsonObject Stroke(params int[] points) => new() { ["$type"] = "Stroke",
        ["P"] = new JsonArray(points.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
    static JsonObject Cmd(string type, string field = "", string value = "")
    {
        var command = new JsonObject { ["$type"] = type };
        if (field.Length > 0) command[field] = value;
        return command;
    }
    static JsonObject Intent(JsonArray gesture, JsonObject? cmd = null, string name = "合成") => new()
    {
        ["Name"] = name, ["Gesture"] = gesture, ["Command"] = cmd?.DeepClone() ?? Cmd("SendKeys", "Keys", "{F8}")
    };

    static void ImportWG2(string output)
    {
        int before = checks;
        string path = Path.Combine(output, "synthetic.bundle");
        var importer = new WGesturesImporter();
        void Archive(params (string Name, string Content)[] entries)
        {
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(true));
                writer.Write(content);
            }
        }

        ImportPreview Preview(JsonObject doc, string? prefs = null)
        {
            (string, string)[] entries = [("Version", "2.9.1"), ("gestures.json", doc.ToJsonString())];
            if (prefs != null) entries = [..entries, ("prefs.json", prefs)];
            Archive(entries);
            return importer.Preview(path)!;
        }
        JsonObject General(params JsonNode[] intents) => new() { ["General"] = new JsonObject { ["Intents"] = new JsonArray(intents) } };
        JsonArray PathGesture(params JsonNode[] elements) => new([Key("MOUSE:1"), .. elements]);
        void Shape(string expected, bool exact, params JsonNode[] strokes)
        {
            var preview = Preview(General(Intent(PathGesture(strokes))));
            var binding = importer.Import(preview, false).Global.Single();
            string directions = string.Concat(binding.Pattern.Directions.Select(d => "↑↗→↘↓↙←↖"[(int)d]));
            Check(directions == expected && preview.Unsupported.Count == 0, "方向 " + expected);
            Check(exact ? preview.Approximated!.Count == 0 : preview.Approximated is [{ Where: "全局", Name: "合成" } a]
                && GestureMatcher.Same(a.Pattern, binding.Pattern), "近似预览 " + expected);
        }
        Shape("→", true, Stroke(0,0,100,0));
        Shape("↓", true, Stroke(0,0,0,100));
        Shape("↗", true, Stroke(0,100,100,0));
        Shape("↓←", true, Stroke(100,0,100,100), Stroke(100,100,0,100));
        Shape("↓→↑", false, Stroke(1,1,3,91,12,99,88,98,97,90,99,1));
        Shape("↓↑↓↑", false, Stroke(2,3,22,92,43,8,64,94,83,2));
        Shape("→", false, Stroke(1,12,20,11,45,13,80,10,104,12));
        Shape("↓←", false, Stroke(1,1,34,81,-30,98));

        GestureBinding BindingFor(JsonArray gesture, JsonObject? cmd = null)
        {
            var preview = Preview(General(Intent(gesture, cmd)));
            return importer.Import(preview, true).Global.Single();
        }
        foreach (var (key, modifier) in new[] { ("MOUSE:0", GestureModifier.LeftClick), ("MOUSE:2", GestureModifier.MiddleClick),
            ("MOUSE:3", GestureModifier.X1Click), ("MOUSE:4", GestureModifier.X2Click), ("VSCROLL:120", GestureModifier.WheelUp) })
        {
            var b = BindingFor(PathGesture(Stroke(0,0,50,0), Key(key)));
            Check(b.Pattern.Modifier == modifier && b.ExecuteOnModifier == (modifier == GestureModifier.WheelUp), "修饰 " + key);
        }
        var wheel = BindingFor(PathGesture(Key("VSCROLL:-120")));
        Check(wheel.Pattern is { Directions.Count: 0, Modifier: GestureModifier.WheelDown } && wheel.ExecuteOnModifier, "直接滚轮");
        void Skip(JsonArray gesture, JsonObject cmd, string reason) =>
            Check(Preview(General(Intent(gesture, cmd))).Unsupported.Single().Reason.Contains(reason), "跳过 " + reason);
        var dummy = Cmd("SendKeys", "Keys", "v");
        Skip(PathGesture(new JsonObject { ["$type"] = "MoveToEdgeCorner" }), dummy, "热角 / 贴边");
        Skip(PathGesture(Key("KEYBOARD:65")), dummy, "手势里按键盘键");
        Skip(new JsonArray(Key("MOUSE:0")), dummy, "左键起手");
        Skip(PathGesture(Key("MOUSE:0"), Stroke(0,0,20,0)), dummy, "还有笔画");
        Skip(PathGesture(Key("MOUSE:0"), Key("MOUSE:2")), dummy, "多个修饰");
        Skip(PathGesture(), Cmd("Script"), "Lua 脚本");
        var mapping = Cmd("SendKeys", "Mode", "Mapping");
        Skip(PathGesture(), mapping, "映射");
        Skip(PathGesture(), Cmd("SendKeys"), "按键为空");
        foreach (string type in new[] { "RunApp", "Cmd" })
        {
            var admin = Cmd(type);
            admin["AsAdmin"] = true;
            Skip(PathGesture(), admin, "以管理员身份运行");
        }
        Skip(PathGesture(), Cmd("UnknownTest"), "UnknownTest");
        Skip(PathGesture(), Cmd("WindowControl","Command","OTHER"), "OTHER");
        Skip(PathGesture(), Cmd("Cmd","Code","echo %WG_UNKNOWN%"), "%WG_UNKNOWN%");

        GestureCommand ConvertCmd(JsonObject c) => BindingFor(PathGesture(), c).Command;
        void Chords(string keys, params KeyChord[] expected) =>
            Check(ConvertCmd(Cmd("SendKeys", "Keys", keys)) is HotkeyCommand h && h.Chords.SequenceEqual(expected), keys);
        Chords("{LCtrl}({LShift}(p))", new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Shift, 80));
        Chords("^+p%{F24}", new(KeyModifiers.Ctrl | KeyModifiers.Shift, 80), new(KeyModifiers.Alt, 135));
        Chords("{VOLUME_UP 2}{MEDIA_PLAY}", new(0,175), new(0,175), new(0,179));
        var win = ConvertCmd(Cmd("SendKeys","Keys","{LWIN}()"));
        var fake = new Fake();
        using var executor = new CommandExecutor(fake);
        executor.Execute(win, Target);
        Check(fake.Log.SequenceEqual(["activate","down:91","up:91"]), "单按 Win");
        var delayed = ConvertCmd(Cmd("SendKeys","Keys","+{F9}{SLEEP 50}b{SLEEP 1500}{ENTER}"));
        Check(delayed is SendTextCommand t && SendKeysParser.Parse(t.Sequence) is
            [{ Key: 120, Modifiers: KeyModifiers.Shift }, { DelayMs: 50 }, _, { DelayMs: 1000 }, { Key: 13 }], "延时顺序和上限");
        fake.Clear();
        executor.Execute(ConvertCmd(Cmd("SendKeys","Keys","{F8}{SLEEP 50}b")), Target);
        Check(fake.Log.SequenceEqual(["activate","down:119","up:119","down:66","up:66"]), "延时后发送菜单助记键");
        const string exe = @"D:\示例\工具.exe";
        Check(ConvertCmd(Cmd("RunApp","Path",exe)) == new OpenCommand(exe), "打开路径");
        Check(ConvertCmd(Cmd("RunApp","Cmd", '"' + exe + '"')) == new OpenCommand(exe), "引号路径");
        string line = '"' + exe + "\" --demo";
        Check(ConvertCmd(Cmd("RunApp","Cmd",line)) is RunCommand r && r.CommandLine == line, "保留参数");
        foreach (var (value, action) in new[] { ("WINDOW_MINIMIZE",WindowAction.Minimize), ("WINDOW_CLOSE",WindowAction.Close),
            ("WINDOW_TOGGLE_MAXIMIZE",WindowAction.MaximizeRestore), ("WINDOW_TOGGLE_TOPMOST",WindowAction.ToggleTopmost) })
            Check(ConvertCmd(Cmd("WindowControl","Command",value)) == new WindowCommand(action), value);
        Check(ConvertCmd(Cmd("Cmd","Code","echo %WG_TARGET_PID% %WG_TARGET_WID%")) is RunCommand
            { CommandLine: "echo %WG_PROCID% %WG_WINID%" }, "目标变量");

        JsonObject First() => Intent(PathGesture(Stroke(0,0,50,0)), name:"先");
        var doc = General(First(), Intent(PathGesture(Stroke(1,1,23,1)), name:"后"));
        JsonObject App(params JsonObject[] intents) => new() { ["Path"] = exe, ["$type"] = "WinAppTarget", ["Intents"] = new JsonArray(intents) };
        doc["Apps"] = new JsonArray(App(First()), App(First()), new JsonObject { ["$type"]="WinAppTarget", ["Path"]=exe+"2" });
        doc["Specials"] = new JsonArray(App(First()));
        doc["Groups"] = new JsonArray(App(First()));
        doc["General"]!["Triggers"] = new JsonArray(new JsonObject { ["Def"] = new JsonArray(Key("MOUSE:2")) },
            new JsonObject { ["Def"] = new JsonArray(Key("MOUSE:4")) }, new JsonObject { ["Def"] = PathGesture(Key("MOUSE:0")) });
        var preview = Preview(doc, """{"DisableInFullscreen":false,"ShowPath":false,"FadeOutExecuted":false,"ShowGestureName":false,"PauseResumeHotkey":"#({ESC})","PathColorRecognized":"#AB345678"}""");
        var config = importer.Import(preview, true);
        Check(preview.SourceVersion == "2.9.1" && preview.GlobalCount == 2 && config.Global.Single().Name == "先", "改扩展名及保留首条");
        Check(config.Apps is [{ Mode: AppRuleMode.InheritAndOverride, Bindings.Count: 1 }] && preview.DisabledAppCount == 0, "程序范围隔离及空规则");
        Check(preview.Unsupported.Select(u=>u.Reason).SequenceEqual(["转换后和「先」相同","转换后和「先」相同","桌面 / 任务栏专属手势","分组"])
            && preview.Approximated!.Count == 0, "逐条诊断");
        Check(config.Settings is { EnabledButtons: GestureButton.Middle | GestureButton.X2, PauseInFullscreen:false,
            ShowTrail:false, FadeTrail:false, ShowCommandName:false, AllowDiagonalMultiStroke:false }
            && config.Settings.PauseHotkey == new KeyChord(KeyModifiers.Win,27), "导入设置");
        Check(preview.HasCustomTrailColors && config.Settings.TrailColors.Values.All(c=>c=="#ab3456")
            && importer.Import(preview,false).Settings.TrailColors.Values.All(c=>c=="#5874c9"), "颜色选择");
        preview = Preview(General());
        var defaults = importer.Import(preview,true).Settings;
        Check(defaults == GestureConfigStore.Empty.Settings with { TrailColors=defaults.TrailColors }, "缺少设置");
        preview = Preview(General(), """{"PauseResumeHotkey":"{NOT_A_KEY}","PathColorRecognized":"#5874C900"}""");
        Check(importer.Import(preview,true).Settings.PauseHotkey == null && !preview.HasCustomTrailColors, "未知热键及默认颜色");

        void Bad(string expected)
        {
            try { importer.Preview(path); }
            catch (InvalidDataException e) { Check(e.Message.Contains(expected), expected); return; }
            throw new Exception("预期拒绝归档");
        }
        Archive(("Version","3.0"),("gestures.json","{}"));
        Bad("不支持的 WGestures 2 版本 3.0");
        Archive(("Version","2.9.1"));
        Bad("不是 WGestures 导出的文件");
        Archive(("Version","2.9.1"),("gestures.json","{}"),("prefs.json",new string(' ',32*1024*1024+1)));
        Bad("不是 WGestures 导出的文件");
        File.WriteAllBytes(path,[80,75,3,4,0]);
        Bad("不是 WGestures 导出的文件");
        Console.WriteLine($"PASS WG2 assertions={checks-before}");
    }
}
