using Pane.Settings;
namespace Pane.Hosting;

internal static class ShellHotkeyBindings
{
    internal const uint NoRepeat=0x4000;
    internal static string? Command(nuint id)=>id switch{1=>"hotkey-launcher",2 or 3=>"hotkey-clipboard",_=>null};
    // Shown 表示动画的目标状态；收起过程中再次按下应重新显示。
    internal static bool ShouldHide(bool shown,bool foreground)=>shown&&foreground;
    internal static (uint Launcher,uint Clipboard) DoubleTapKeys(Hotkey launcher,Hotkey clipboard,bool enabled,bool suspended,bool launcherEnabled=true)
    {
        if(suspended)return (0,0);
        uint a=launcherEnabled&&launcher.IsValidDoubleTap?launcher.VirtualKey:0u;
        uint b=enabled&&clipboard.IsValidDoubleTap?clipboard.VirtualKey:0u;
        return (a,b==a?0:b);
    }
    internal static bool KeepClipboardHotkey(bool enabled,bool suspended,bool keepHotkeyWithWinV,bool winVRegistered)
        =>enabled&&!suspended&&(keepHotkeyWithWinV||!winVRegistered);
    internal static Hotkey ParseOverride(string text)
    {
        if(text.StartsWith("2x",StringComparison.OrdinalIgnoreCase)){
            ushort vk=text[2..].ToLowerInvariant() switch{"lshift"=>0xA0,"rshift"=>0xA1,"lctrl"=>0xA2,"rctrl"=>0xA3,"lalt"=>0xA4,"ralt"=>0xA5,_=>throw new FormatException("开发热键格式无效")}; // 不翻译：异常
            return new(KeyModifiers.None,vk){DoubleTap=true};
        }
        var keys=text.Split('+');KeyModifiers mods=0;foreach(var k in keys[..^1])mods|=Enum.Parse<KeyModifiers>(k,true);
        string last=keys[^1];ushort key=last.Equals("Space",StringComparison.OrdinalIgnoreCase)?(ushort)32:last.Length==1?(ushort)char.ToUpperInvariant(last[0]):throw new FormatException("开发热键格式无效"); // 不翻译：异常
        return new(mods,key);
    }
    internal static bool Rebind(List<int> registered,Action<int> unregister,Action<int,string,Hotkey> register,
        Hotkey launcher,Hotkey clipboard,bool enabled,bool suspended,bool launcherEnabled=true,
        bool keepHotkeyWithWinV=true,Func<bool>? registerWinV=null)
    {
        foreach(int id in registered)unregister(id);
        registered.Clear();
        bool active=registerWinV?.Invoke()??false;
        if(suspended)return active;
        if(launcherEnabled&&!launcher.IsValidDoubleTap)register(1,"launcher",launcher);
        if(KeepClipboardHotkey(enabled,suspended,keepHotkeyWithWinV,active)&&!clipboard.IsValidDoubleTap
            &&!(active&&clipboard==new Hotkey(KeyModifiers.Win,86)))register(2,"clipboard",clipboard);
        return active;
    }
}
