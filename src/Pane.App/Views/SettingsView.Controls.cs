// 设置窗口：控件（开关、按钮、输入框等小部件）（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
using Pane.Localization;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Hosting;
using Pane.Settings;
using Pane.Views.Controls;
using Windows.Foundation;
using G = Pane.Gestures;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= 控件 =================

    static TextBlock Text(string text, TextBlock proto) => new()
    {
        Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = proto.FontFamily, FontSize = proto.FontSize, Foreground = proto.Foreground,
    };

    SolidColorBrush Accent()
    {
        uint argb = Convert.ToUInt32(app.Glass.Current.AccentColor[1..], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    PaneSwitch Switch(bool value, Action<bool> changed)
    {
        var s = new PaneSwitch(value, FillProto.Background, FillProto.BorderBrush, Accent());
        s.Toggled += (_, v) => changed(v);
        return s;
    }

    PaneSegmented Seg((string, string)[] options, string value, Action<string> changed)
    {
        var s = new PaneSegmented(options, value, SmallStrongProto, SmallProto, FillProto.Background, FillProto.BorderBrush) { MinWidth = options.Length > 2 ? 220 : 180 };
        s.Changed += (_, v) => changed(v);
        return s;
    }

    HotkeyBox Hotkey(Settings.Hotkey value, Settings.Hotkey other, Action<Settings.Hotkey> changed, bool doubleTap = false)
    {
        var box = new HotkeyBox(value, SmallProto, KbdProto, FillProto.Background, FillProto.BorderBrush, Accent())
        {
            AllowDoubleTap = doubleTap,
            Validate = h => h == other ? Strings.Settings_Hotkey_Duplicate
                : h.Modifiers == KeyModifiers.Win && h.VirtualKey == 'V' ? (app.WinV is null ? Strings.Settings_Hotkey_WinVOccupied : Strings.Settings_Hotkey_WinVReplacement)
                : null,
        };
        box.Changed += (_, h) => { changed(h); Later(400); };   // 宿主在后台线程重新注册，稍后刷新占用提示
        box.RecordingChanged += (_, on) => app.SetHotkeysSuspended(on);
        return box;
    }

    // 注册失败 = 被占用
    static string ConflictText(HotkeyConflict c) => string.Format(Strings.Settings_Hotkey_Occupied, (object?)(HotkeyBox.Text(c.Hotkey).Replace("Space", Strings.Common_SpaceKey)));

    // 开发和试用时 PANE_DEV_HOTKEYS 会改掉实际注册的键，照实说明
    static string? DevHotkey(string name)
    {
        var text = Environment.GetEnvironmentVariable("PANE_DEV_HOTKEYS");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var pair = text.Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == name);
        return pair == null ? null : string.Format(Strings.Settings_Hotkey_DevelopmentOverride, (object?)(pair[1]));
    }

    Border Swatch(string color, string name, bool selected)
    {
        uint argb = Convert.ToUInt32(color[1..], 16);
        var c = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        // 原型：选中时外面隔 2px 再套一圈 2px 同色环
        var ring = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2), BorderBrush = selected ? new SolidColorBrush(c) : null, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ring.Child = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1) };
        ToolTipService.SetToolTip(ring, name);
        ring.Tapped += (_, _) => SaveGeneral(x => x with { AccentColor = color });
        return ring;
    }

    Border Chip(string text) => new()
    {
        Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = SelectedProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = SmallProto.Foreground },
    };

    FrameworkElement Status(bool ok, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new PaneIcon { Glyph = ok ? "check" : "warn", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(ok ? Windows.UI.Color.FromArgb(255, 0x30, 0xa4, 0x6c) : Windows.UI.Color.FromArgb(255, 0xf5, 0xa5, 0x24)) });
        row.Children.Add(Text(text, SmallProto));
        return row;
    }

    Button Button(string text, string? glyph, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph != null) content.Children.Add(new PaneIcon { Glyph = glyph, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = LabelProto.Foreground });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = ButtonProto.Style, Height = PaneTokens.ControlSm, Content = content };
        b.Click += (_, _) => click();
        return b;
    }

    Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = IconButtonProto.Style, Content = new PaneIcon { Glyph = glyph, Width = 15, Height = 15, Foreground = IconProto.Foreground } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // 不靠 IsEnabledChanged：元素还没进树时它不一定触发
    static void Enable(Control c, bool on) { c.IsEnabled = on; c.Opacity = on ? 1 : 0.4; }
}
