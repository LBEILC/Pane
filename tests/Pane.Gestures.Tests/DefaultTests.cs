using Pane.Gestures;
static partial class Program
{
    static void Defaults(string output)
    {
        var config = GestureConfigStore.Defaults;
        GestureConfigStore.Validate(config);
        Check(config.Global.Count == 8 && config.Apps.Count == 0, "eight global defaults");
        GestureDirection[][] directions = [[GestureDirection.Left], [GestureDirection.Right], [GestureDirection.Up], [GestureDirection.Down], [GestureDirection.Down, GestureDirection.Right], [GestureDirection.Left, GestureDirection.Up], [], []];
        string[] names = ["后退", "前进", "回到顶部", "到底部", "关闭标签页", "重新打开标签页", "上一个标签页", "下一个标签页"];
        KeyChord[] chords = [new(KeyModifiers.Alt, 0x25), new(KeyModifiers.Alt, 0x27), new(0, 0x24), new(0, 0x23), new(KeyModifiers.Ctrl, 0x57), new(KeyModifiers.Ctrl | KeyModifiers.Shift, 0x54), new(KeyModifiers.Ctrl | KeyModifiers.Shift, 9), new(KeyModifiers.Ctrl, 9)];
        for (int i = 0; i < 8; i++) {
            var modifier = i == 6 ? GestureModifier.WheelUp : i == 7 ? GestureModifier.WheelDown : GestureModifier.None;
            var matched = GestureMatcher.Match(config, new(GestureButton.Right, directions[i], modifier), Target.ExePath, Target.WindowClass);
            Check(matched?.Name == names[i] && matched.ExecuteOnModifier == (i >= 6) && matched.Command is HotkeyCommand h && h.Chords.SequenceEqual([chords[i]]), "default match " + i);
            var session = new GestureSession(config, GestureButton.Right, Target with { Start = new(0, 0) }, 96);
            var commands = new List<CommandRequest>(); session.Command += commands.Add;
            int x = 0, y = 0;
            foreach (var direction in directions[i]) { x += direction == GestureDirection.Left ? -100 : direction == GestureDirection.Right ? 100 : 0; y += direction == GestureDirection.Up ? -100 : direction == GestureDirection.Down ? 100 : 0; session.Move(new(x, y)); }
            if (i >= 6) { session.Modify(modifier); session.Modify(modifier); }
            Check(commands.Count == (i >= 6 ? 2 : 0), "default execution timing " + i);
            session.End();
            Check(commands.Count == (i >= 6 ? 2 : 1) && commands.All(c => c.Binding.Name == names[i]), "default release " + i);
        }
        var path = Path.Combine(output, Guid.NewGuid() + "-defaults.json");
        Check(GestureConfigStore.LoadOrDefaults(path).Global.Count == 8 && !File.Exists(path), "missing file defaults");
        GestureConfigStore.Save(path, GestureConfigStore.Empty);
        var old = File.ReadAllBytes(path);
        Check(GestureConfigStore.LoadOrDefaults(path).Global.Count == 0 && old.SequenceEqual(File.ReadAllBytes(path)), "existing empty preserved");
        File.WriteAllText(path, "broken");
        Throws<InvalidDataException>(() => GestureConfigStore.LoadOrDefaults(path), "corrupt file reported");
        Check(File.ReadAllText(path) == "broken", "corrupt file preserved");
    }
}
