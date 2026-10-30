using Pane.Settings;
namespace Pane.Hosting;

internal static class ShellHotkeyBindings
{
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
