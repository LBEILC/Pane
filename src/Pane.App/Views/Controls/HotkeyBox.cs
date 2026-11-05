using Pane.Localization;
// 热键录入（原型 .hotkey）：显示成一串键帽；点一下进入录入（焦点光环），按下组合键即保存，Esc 取消；
// 必须带修饰键；Win+V 在没打开「替换 Win+V」时不能选。
// AllowDoubleTap（054）：干净地连按两下同一个修饰键就录成双击（左右区分），显示成「左 Ctrl」键帽后跟「×2」。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Settings;
using Windows.System;
using Windows.UI.Core;

namespace Pane.Views.Controls;

public sealed partial class HotkeyBox : UserControl
{
    readonly Border box;
    readonly StackPanel keys = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock labelProto;
    readonly Border kbdProto;
    readonly Brush stroke, ring;
    Hotkey value;
    bool recording;

    /// <param name="labelProto">提示文字样板（录入中、出错时的说明）；<paramref name="kbdProto"/> 键帽样板（Border + TextBlock）。</param>
    public HotkeyBox(Hotkey value, TextBlock labelProto, Border kbdProto, Brush fill, Brush stroke, Brush accent)
    {
        this.value = value; this.labelProto = labelProto; this.kbdProto = kbdProto; this.stroke = stroke;
        ring = new SolidColorBrush(((SolidColorBrush)accent).Color with { A = 0x73 });
        IsTabStop = true; UseSystemFocusVisuals = false;
        var radius = ((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft;
        box = new Border { MinWidth = 120, Height = PaneTokens.ControlMd, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(radius), Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), Child = keys };
        Content = box;
        Show();
        Tapped += (_, _) => { if (!recording) Start(); };
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += (_, _) => { if (recording) Stop(); };
    }

    public Hotkey Value => value;
    /// <summary>录到新组合键。返回 null 表示接受；返回文字表示拒绝，并在框里显示这句原因。</summary>
    public Func<Hotkey, string?>? Validate { get; set; }
    /// <summary>允许不带修饰键的单键（手势动作里的 F5、Delete、Backspace……）。全局热键不允许。</summary>
    public bool AllowBareKeys { get; set; }
    /// <summary>允许录成「连按两下某个修饰键」（只有启动器、剪贴板热键）。</summary>
    public bool AllowDoubleTap { get; set; }
    /// <summary><see cref="Names"/> 里表示「按两下」的那一项：画成普通文字，不画键帽、前面不加「+」。</summary>
    public const string TwiceMark = "×2";
    public event EventHandler<Hotkey>? Changed;
    /// <summary>进入 / 退出录入。宿主据此暂停全局热键，否则按下正在注册的组合键会被系统先拿走。</summary>
    public event EventHandler<bool>? RecordingChanged;

    void Start()
    {
        recording = true;
        box.BorderBrush = ring; box.BorderThickness = new Thickness(2);
        tapStage = 0;
        Message(AllowBareKeys ? Strings.Common_RecordKey : AllowDoubleTap ? Strings.Common_RecordHotkeyOrDoubleTap : Strings.Common_RecordHotkey);
        Focus(FocusState.Programmatic);
        RecordingChanged?.Invoke(this, true);
    }

    void Stop()
    {
        recording = false;
        box.BorderBrush = stroke; box.BorderThickness = new Thickness(1);
        Show();
        RecordingChanged?.Invoke(this, false);
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!recording) { if (e.Key is VirtualKey.Enter or VirtualKey.Space) { Start(); e.Handled = true; } return; }
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) { Stop(); return; }
        if (IsModifier(e.Key)) { if (AllowDoubleTap) TapDown(e); return; }
        tapStage = 0;
        KeyModifiers mods = 0;
        if (Down(VirtualKey.Control)) mods |= KeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu)) mods |= KeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) mods |= KeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= KeyModifiers.Win;
        if (mods == 0 && !AllowBareKeys) { Message(Strings.Common_ModifierRequired); return; }
        Commit(new Hotkey(mods, (ushort)e.Key));
    }

    void Commit(Hotkey next)
    {
        if (Validate?.Invoke(next) is { } reason) { Message(reason); return; }
        value = next;
        Stop();
        Changed?.Invoke(this, value);
    }

    // 双击录入：按下 → 松开 → 按下 → 松开，同一个键、中间没有别的键。录入时比后台识别宽松一点（不卡按住多久），间隔仍要短。
    const long TapGap = 500;
    int tapStage;          // 0 没有，1 第一下按着，2 第一下已松开，3 第二下按着
    ushort tapKey;
    long tapAt;

    static bool IsModifier(VirtualKey k) => k is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    static readonly VirtualKey[] Sides = [VirtualKey.LeftShift, VirtualKey.RightShift, VirtualKey.LeftControl, VirtualKey.RightControl, VirtualKey.LeftMenu, VirtualKey.RightMenu];

    void TapDown(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return;   // 按住时的自动重复
        var held = Sides.Where(Down).ToArray();
        // 只有一个修饰键按着、而且不是 Win，才算一下
        if (held.Length != 1 || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) { tapStage = 0; return; }
        ushort vk = (ushort)held[0];
        long now = Environment.TickCount64;
        if (tapStage == 2 && vk == tapKey && now - tapAt <= TapGap) tapStage = 3;
        else { tapKey = vk; tapStage = 1; }
        tapAt = now;
    }

    void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!recording || !AllowDoubleTap || !IsModifier(e.Key)) return;
        e.Handled = true;
        if (tapStage is not (1 or 3) || Down((VirtualKey)tapKey)) return;
        if (tapStage == 1) { tapStage = 2; tapAt = Environment.TickCount64; return; }
        tapStage = 0;
        Commit(new Hotkey(KeyModifiers.None, tapKey) { DoubleTap = true });
    }

    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void Message(string text)
    {
        keys.Children.Clear();
        keys.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = labelProto.FontFamily, FontSize = labelProto.FontSize, Foreground = labelProto.Foreground });
    }

    void Show()
    {
        keys.Children.Clear();
        if (value.VirtualKey == 0) { Message(Strings.Common_ClickToRecord); return; }   // 还没录过
        bool first = true;
        foreach (var name in Names(value))
        {
            if (name == TwiceMark) { keys.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontFamily = labelProto.FontFamily, FontSize = labelProto.FontSize, Foreground = labelProto.Foreground }); continue; }
            if (!first) keys.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, FontFamily = labelProto.FontFamily, FontSize = labelProto.FontSize, Foreground = labelProto.Foreground });
            first = false;
            keys.Children.Add(new Border { Style = kbdProto.Style, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Style = ((TextBlock)kbdProto.Child).Style, Text = name } });
        }
    }

    /// <summary>组合键的显示名，例如 Alt、Space（原型和设计文档里写作「Alt+空格」）。</summary>
    public static IEnumerable<string> Names(Hotkey h)
    {
        if (h.DoubleTap) { yield return KeyName(h.VirtualKey); yield return TwiceMark; yield break; }
        if (h.Modifiers.HasFlag(KeyModifiers.Ctrl)) yield return "Ctrl";
        if (h.Modifiers.HasFlag(KeyModifiers.Alt)) yield return "Alt";
        if (h.Modifiers.HasFlag(KeyModifiers.Shift)) yield return "Shift";
        if (h.Modifiers.HasFlag(KeyModifiers.Win)) yield return "Win";
        yield return KeyName(h.VirtualKey);
    }

    public static string Text(Hotkey h) => h.DoubleTap ? string.Format(Strings.Common_DoubleTapKey, (object?)KeyName(h.VirtualKey)) : string.Join("+", Names(h));

    // 不用 Enum.ToString（NativeAOT 下可能拿不到名字），常用键手写
    static string KeyName(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
        0xA0 => string.Format(Strings.Common_LeftKey, (object?)"Shift"), 0xA1 => string.Format(Strings.Common_RightKey, (object?)"Shift"),
        0xA2 => string.Format(Strings.Common_LeftKey, (object?)"Ctrl"), 0xA3 => string.Format(Strings.Common_RightKey, (object?)"Ctrl"),
        0xA4 => string.Format(Strings.Common_LeftKey, (object?)"Alt"), 0xA5 => string.Format(Strings.Common_RightKey, (object?)"Alt"),
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
        _ => string.Format(Strings.Common_KeyCode, (object?)(vk)),
    };
}
