// 热键录入（原型 .hotkey）：显示成一串键帽；点一下进入录入（焦点光环），按下组合键即保存，Esc 取消；
// 必须带修饰键；Win+V 在没打开「替换 Win+V」时不能选。
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
        box = new Border { MinWidth = 120, Height = 32, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(radius), Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), Child = keys };
        Content = box;
        Show();
        Tapped += (_, _) => { if (!recording) Start(); };
        KeyDown += OnKeyDown;
        LostFocus += (_, _) => { if (recording) Stop(); };
    }

    public Hotkey Value => value;
    /// <summary>录到新组合键。返回 null 表示接受；返回文字表示拒绝，并在框里显示这句原因。</summary>
    public Func<Hotkey, string?>? Validate { get; set; }
    /// <summary>允许不带修饰键的单键（手势动作里的 F5、Delete、Backspace……）。全局热键不允许。</summary>
    public bool AllowBareKeys { get; set; }
    public event EventHandler<Hotkey>? Changed;
    /// <summary>进入 / 退出录入。宿主据此暂停全局热键，否则按下正在注册的组合键会被系统先拿走。</summary>
    public event EventHandler<bool>? RecordingChanged;

    void Start()
    {
        recording = true;
        box.BorderBrush = ring; box.BorderThickness = new Thickness(2);
        Message(AllowBareKeys ? "按下要发送的键…" : "按下新的组合键…");
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
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        KeyModifiers mods = 0;
        if (Down(VirtualKey.Control)) mods |= KeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu)) mods |= KeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) mods |= KeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= KeyModifiers.Win;
        if (mods == 0 && !AllowBareKeys) { Message("至少要带一个 Ctrl / Alt / Shift / Win"); return; }
        var next = new Hotkey(mods, (ushort)e.Key);
        if (Validate?.Invoke(next) is { } reason) { Message(reason); return; }
        value = next;
        Stop();
        Changed?.Invoke(this, value);
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
        if (value.VirtualKey == 0) { Message("点这里，然后按键"); return; }   // 还没录过
        bool first = true;
        foreach (var name in Names(value))
        {
            if (!first) keys.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, FontFamily = labelProto.FontFamily, FontSize = labelProto.FontSize, Foreground = labelProto.Foreground });
            first = false;
            keys.Children.Add(new Border { Style = kbdProto.Style, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Style = ((TextBlock)kbdProto.Child).Style, Text = name } });
        }
    }

    /// <summary>组合键的显示名，例如 Alt、Space（原型和设计文档里写作「Alt+空格」）。</summary>
    public static IEnumerable<string> Names(Hotkey h)
    {
        if (h.Modifiers.HasFlag(KeyModifiers.Ctrl)) yield return "Ctrl";
        if (h.Modifiers.HasFlag(KeyModifiers.Alt)) yield return "Alt";
        if (h.Modifiers.HasFlag(KeyModifiers.Shift)) yield return "Shift";
        if (h.Modifiers.HasFlag(KeyModifiers.Win)) yield return "Win";
        yield return KeyName(h.VirtualKey);
    }

    public static string Text(Hotkey h) => string.Join("+", Names(h));

    // 不用 Enum.ToString（NativeAOT 下可能拿不到名字），常用键手写
    static string KeyName(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
        _ => $"键 {vk:X2}",
    };
}
