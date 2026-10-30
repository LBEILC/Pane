using System.Globalization;
namespace Pane.Gestures;

public sealed record GestureTarget(nint Window, uint ProcessId, string ExePath, string WindowClass,
    GesturePoint Start, GesturePoint End);

public interface ISystemOperations
{
    IDisposable? AcquireInput()=>null;
    void Activate(GestureTarget target);
    bool IsKeyDown(ushort key);
    void Key(ushort key, bool down);
    void Character(char character, bool down);
    (ushort Key, KeyModifiers Modifiers) MapCharacter(char character);
    string ReadSelection(GestureTarget target);
    string? ExplorerFolder(GestureTarget target);
    void Run(string command, bool showWindow, string directory, IReadOnlyDictionary<string, string> variables);
    void Window(GestureTarget target, WindowAction action);
    void Open(string target);
    void Pause();
}

public sealed class CommandExecutor(ISystemOperations system) : IDisposable
{
    static readonly ushort[] ModifierKeys = [160, 161, 162, 163, 164, 165, 91, 92];
    bool taskActive;
    ushort[] taskHeld=[];
    public void Execute(GestureCommand command, GestureTarget target)
    {
        switch (command)
        {
            case HotkeyCommand hotkey: WithTarget(target,()=>WithKeys(() => { foreach (var chord in hotkey.Chords) {Chord(chord);Thread.Sleep(10);} })); break;
            case SendTextCommand text:
                var strokes = SendKeysParser.Parse(text.Sequence);
                WithTarget(target,()=>WithKeys(() => { foreach (var s in strokes) {
                    if (s.Key != 0) Chord(new(s.Modifiers, s.Key));
                    else if (s.Modifiers != KeyModifiers.None) { var mapped = system.MapCharacter(s.Character); Chord(new(s.Modifiers | mapped.Modifiers, mapped.Key)); }
                    else { try { system.Character(s.Character, true); } finally { system.Character(s.Character, false); } }
                } })); break;
            case WebSearchCommand web:
                system.Activate(target);
                string selection = system.ReadSelection(target);
                if (string.IsNullOrEmpty(selection)) throw new InvalidOperationException("没有读到选中文字");
                system.Open(web.UrlTemplate.Replace("{query}", Uri.EscapeDataString(selection))); break;
            case RunCommand run:
                string directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (run.InExplorerFolder) directory = system.ExplorerFolder(target) ?? Path.GetDirectoryName(target.ExePath) ?? directory;
                system.Run(run.CommandLine, run.ShowWindow, directory, Variables(target)); break;
            case WindowCommand window: WithTarget(target,()=>{});system.Window(target, window.Action);break;
            case VolumeCommand volume: Volume(volume.Action, volume.Step); break;
            case TaskSwitchCommand: WithKeys(() => Chord(new(KeyModifiers.Alt, 9))); break;
            case OpenCommand open: system.Open(open.Target); break;
            case PauseGesturesCommand: system.Pause(); break;
            case DoNothingCommand: break;
            default: throw new NotSupportedException("未知命令");
        }
    }
    void WithTarget(GestureTarget target,Action action){using var lease=system.AcquireInput();system.Activate(target);action();}
    public static IReadOnlyDictionary<string, string> Variables(GestureTarget t) => new Dictionary<string, string> {
        ["WG_PROCID"] = t.ProcessId.ToString(CultureInfo.InvariantCulture), ["WG_WINID"] = t.Window.ToInt64().ToString(CultureInfo.InvariantCulture),
        ["WG_STARTPOINT_X"] = t.Start.X.ToString(CultureInfo.InvariantCulture), ["WG_STARTPOINT_Y"] = t.Start.Y.ToString(CultureInfo.InvariantCulture),
        ["WG_ENDPOINT_X"] = t.End.X.ToString(CultureInfo.InvariantCulture), ["WG_ENDPOINT_Y"] = t.End.Y.ToString(CultureInfo.InvariantCulture) };
    void WithKeys(Action action)
    {
        using var lease=system.AcquireInput();
        var held = ModifierKeys.Where(system.IsKeyDown).ToArray();
        try { foreach (var key in held) system.Key(key, false); action(); }
        finally { Exception? failure=null;foreach (var key in held)try{system.Key(key, true);}catch(Exception e){failure??=e;}if(failure!=null)throw failure; }
    }
    void Chord(KeyChord chord)
    {
        var keys = new List<ushort>();
        if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl)) keys.Add(162);
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) keys.Add(164);
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) keys.Add(160);
        if (chord.Modifiers.HasFlag(KeyModifiers.Win)) keys.Add(91);
        if (chord.VirtualKey != 0) keys.Add(chord.VirtualKey);
        int pressed = 0;
        try { foreach (var key in keys) { pressed++; system.Key(key, true); } }
        finally { Exception? failure=null;for (int i = pressed - 1; i >= 0; i--)try{system.Key(keys[i], false);}catch(Exception e){failure??=e;}if(failure!=null)throw failure; }
    }
    void Volume(VolumeAction action, int step) => WithKeys(() => {
        ushort key = action == VolumeAction.ToggleMute ? (ushort)173 : action == VolumeAction.Up ? (ushort)175 : (ushort)174;
        for (int i = 0; i < (action == VolumeAction.ToggleMute ? 1 : Math.Clamp(step, 1, 100)); i++) Chord(new(0, key));
    });
    public void BeginAware(GestureCommand command)
    {
        using var lease=system.AcquireInput();
        if (command is VolumeCommand) { Volume(VolumeAction.Up, 1); Volume(VolumeAction.Down, 1); }
        else if (command is TaskSwitchCommand) {
            taskHeld=ModifierKeys.Where(system.IsKeyDown).ToArray();taskActive = true;
            try { foreach(var key in taskHeld)system.Key(key,false);system.Key(164, true); Chord(new(0, 9)); } catch { EndAware(); throw; }
        }
    }
    public void ModifyAware(GestureCommand command, GestureModifier modifier)
    {
        using var lease=system.AcquireInput();
        if (command is VolumeCommand v) {
            if (modifier == GestureModifier.WheelUp) Volume(VolumeAction.Up, v.Step);
            if (modifier == GestureModifier.WheelDown) Volume(VolumeAction.Down, v.Step);
            if (modifier == GestureModifier.MiddleClick) Volume(VolumeAction.ToggleMute, 1);
        }
        if (command is TaskSwitchCommand && taskActive && modifier is GestureModifier.WheelUp or GestureModifier.WheelDown)
            try { Chord(new(modifier == GestureModifier.WheelUp ? KeyModifiers.Shift : 0, 9)); } catch { EndAware(); throw; }
    }
    public void EndAware() { if (taskActive) { using var lease=system.AcquireInput();Exception? failure=null;try{system.Key(164,false);}catch(Exception e){failure=e;}foreach(var key in taskHeld)try{system.Key(key,true);}catch(Exception e){failure??=e;}if(failure!=null)throw failure;taskActive = false;taskHeld=[]; } }
    public void Dispose() => EndAware();
}
