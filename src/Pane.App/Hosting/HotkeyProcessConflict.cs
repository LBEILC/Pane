using Pane.Settings;
namespace Pane.Hosting;
internal static class HotkeyProcessConflict
{
    internal static bool IsUTools(Hotkey key,IEnumerable<string> names)=>key.Modifiers==KeyModifiers.Alt&&key.VirtualKey==32&&names.Any(n=>string.Equals(n,"uTools",StringComparison.OrdinalIgnoreCase));
}
