using Pane.Settings;
namespace Pane.Hosting;

internal static class ShellHotkeyBindings
{
    internal const uint NoRepeat=0x4000;
    internal static string? Command(nuint id)=>id switch{1=>"hotkey-launcher",2 or 3=>"hotkey-clipboard",_=>null};
    // Shown 表示动画的目标状态；收起过程中再次按下应重新显示。
    internal static bool ShouldHide(bool shown,bool foreground)=>shown&&foreground;
    internal static void Rebind(List<int> registered,Action<int> unregister,Action<int,string,Hotkey> register,
        Hotkey launcher,Hotkey clipboard,bool enabled,bool suspended)
    {
        foreach(int id in registered)unregister(id);
        registered.Clear();
        if(suspended)return;
        register(1,"launcher",launcher);
        if(enabled)register(2,"clipboard",clipboard);
    }
}
