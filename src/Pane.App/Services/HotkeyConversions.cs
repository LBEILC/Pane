namespace Pane.Services;
internal static class HotkeyConversions
{
    internal static Pane.Gestures.KeyChord ToGesture(Pane.Settings.Hotkey key)=>new((Pane.Gestures.KeyModifiers)(int)key.Modifiers,key.VirtualKey);
}
