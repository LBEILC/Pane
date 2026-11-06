// 设置窗口：手势编辑器（添加 / 编辑一个手势）（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 手势编辑器（添加 / 编辑一个全局手势） =================
    // 画方向：在画板里按住左键画（和真实手势用同一个识别器），或点右边的 8 个方向键逐段添加。
    // 草稿只在设置窗口里，点「保存」才写进 gestures.json 并立即生效。

    sealed class GestureDraft
    {
        public int Index = -1;                                  // -1 = 新加
        public int App = -1;                                    // -1 = 全局手势；≥0 = 按程序设置里第几个程序的专用手势
        public G.GestureButton Button = G.GestureButton.Right;
        public List<G.GestureDirection> Directions = [];
        public G.GestureModifier Modifier = G.GestureModifier.None;
        public string Name = "";
        public G.GestureCommand Command = new G.HotkeyCommand([]);
        public bool ExecuteOnModifier;
        public string? Error;
    }

    GestureDraft? draft;

    static readonly (string Key, string Label)[] CommandKinds =
    [
        ("hotkey", Strings.Settings_Gestures_CommandHotkey), ("window", Strings.Settings_Gestures_CommandWindow), ("volume", Strings.Settings_Gestures_CommandVolume), ("open", Strings.Settings_Gestures_CommandOpen), ("run", Strings.Settings_Gestures_Run),
        ("text", Strings.Settings_Gestures_SendText), ("search", Strings.Settings_Gestures_CommandWebSearch), ("task", Strings.Settings_Gestures_SwitchWindow), ("pause", Strings.Settings_Gestures_Pause), ("none", Strings.Settings_Gestures_NoOp),
    ];

    static string KindOf(G.GestureCommand c) => c switch
    {
        G.HotkeyCommand => "hotkey", G.WindowCommand => "window", G.VolumeCommand => "volume", G.OpenCommand => "open",
        G.RunCommand => "run", G.SendTextCommand => "text", G.WebSearchCommand => "search", G.TaskSwitchCommand => "task",
        G.PauseGesturesCommand => "pause", _ => "none",
    };

    G.GestureCommand NewCommand(string kind) => kind switch
    {
        "hotkey" => new G.HotkeyCommand([]),
        "window" => new G.WindowCommand(G.WindowAction.MaximizeRestore),
        "volume" => new G.VolumeCommand(G.VolumeAction.Up, 5),
        "open" => new G.OpenCommand(""),
        "run" => new G.RunCommand("", false, true),
        "text" => new G.SendTextCommand(""),
        // 默认用启动器里设为默认的搜索站点
        "search" => new G.WebSearchCommand(app.Store.Current.Launcher.SearchEngines.FirstOrDefault(e => e.Keyword == app.Store.Current.Launcher.DefaultEngine)?.UrlTemplate ?? "https://www.bing.com/search?q={query}"),
        "task" => new G.TaskSwitchCommand(),
        "pause" => new G.PauseGesturesCommand(),
        _ => new G.DoNothingCommand(),
    };

    void EditGesture(int index, int appIndex = -1) { draft = NewDraft(index, appIndex); revealKey = "gesture"; Render(); }

    // 草稿所属的手势列表：全局，或某个程序的专用手势
    static IReadOnlyList<G.GestureBinding> BindingsOf(G.GestureConfig config, int appIndex) =>
        appIndex < 0 ? config.Global : appIndex < config.Apps.Count ? config.Apps[appIndex].Bindings : [];

    GestureDraft NewDraft(int index, int appIndex = -1)
    {
        var config = app.GestureConfig;
        var list = BindingsOf(config, appIndex);
        if (index < 0 || index >= list.Count) return new GestureDraft { App = appIndex, Button = FirstButton(config.Settings.EnabledButtons) };
        var g = list[index];
        return new GestureDraft
        {
            Index = index, App = appIndex, Button = g.Pattern.Button, Directions = [.. g.Pattern.Directions], Modifier = g.Pattern.Modifier,
            Name = g.Name, Command = g.Command, ExecuteOnModifier = g.ExecuteOnModifier,
        };
    }

    static G.GestureButton FirstButton(G.GestureButton enabled) =>
        new[] { G.GestureButton.Right, G.GestureButton.Middle, G.GestureButton.X1, G.GestureButton.X2 }.FirstOrDefault(b => enabled.HasFlag(b), G.GestureButton.Right);

    FrameworkElement GestureEditor(GestureDraft d, G.GestureConfig config)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(14, 14, 14, 14) };

        // ---- 画方向 ----
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
        void ShowArrows()
        {
            arrows.Children.Clear();
            if (d.Directions.Count == 0) { arrows.Children.Add(Text(d.Modifier == G.GestureModifier.None ? Strings.Settings_Gestures_DirectionMissing : Strings.Settings_Gestures_ModifierOnly, DescProto)); return; }
            foreach (var dir in d.Directions)
                arrows.Children.Add(new PaneIcon { Glyph = ArrowGlyphs[(int)dir], Width = 24, Height = 24, StrokeWidth = 2.2, Foreground = LabelProto.Foreground });
        }
        ShowArrows();

        var pad = DrawPad(d, config.Settings.AllowDiagonalMultiStroke, ShowArrows);
        var keys = new Grid { RowSpacing = 4, ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++) { keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
        // 九宫格：外圈 8 个方向（顺序同 GestureDirection 的位置），中间是退一格
        (int Row, int Col, G.GestureDirection Dir)[] layout =
        [
            (0, 0, G.GestureDirection.UpLeft), (0, 1, G.GestureDirection.Up), (0, 2, G.GestureDirection.UpRight),
            (1, 0, G.GestureDirection.Left), (1, 2, G.GestureDirection.Right),
            (2, 0, G.GestureDirection.DownLeft), (2, 1, G.GestureDirection.Down), (2, 2, G.GestureDirection.DownRight),
        ];
        foreach (var (r, c, dir) in layout)
        {
            var b = IconButton(ArrowGlyphs[(int)dir], Strings.Settings_Gestures_AddDirection, () =>
            {
                if (d.Directions.Count > 0 && d.Directions[^1] == dir) return;   // 同方向连着算一段
                d.Directions.Add(dir); d.Error = null; ShowArrows();
            });
            Grid.SetRow(b, r); Grid.SetColumn(b, c); keys.Children.Add(b);
        }
        var back = IconButton("close", Strings.Settings_Gestures_RemoveDirection, () => { if (d.Directions.Count > 0) d.Directions.RemoveAt(d.Directions.Count - 1); ShowArrows(); });
        Grid.SetRow(back, 1); Grid.SetColumn(back, 1); keys.Children.Add(back);
        var drawRow = new Grid { ColumnSpacing = 16 };
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        drawRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        drawRow.Children.Add(pad);
        Grid.SetColumn(keys, 1); drawRow.Children.Add(keys);
        panel.Children.Add(drawRow);
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Direction, arrows));

        // ---- 触发键、附加动作 ----
        panel.Children.Add(FormLine(Strings.Settings_Gestures_TriggerButton, Choices([("Right", Strings.Settings_Gestures_RightButton), ("Middle", Strings.Settings_Gestures_MiddleButton), ("X1", Strings.Settings_Gestures_BackButton), ("X2", Strings.Settings_Gestures_ForwardButton)], d.Button.ToString(),
            v => d.Button = Enum.Parse<G.GestureButton>(v))));
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Modifier, Choices([("None", Strings.Common_None), ("WheelUp", Strings.Settings_Gestures_ModifierWheelUp), ("WheelDown", Strings.Settings_Gestures_ModifierWheelDown), ("LeftClick", Strings.Settings_Gestures_ModifierLeft), ("MiddleClick", Strings.Settings_Gestures_ModifierMiddle), ("X1Click", Strings.Settings_Gestures_ModifierBack), ("X2Click", Strings.Settings_Gestures_ModifierForward)],
            d.Modifier.ToString(), v => d.Modifier = Enum.Parse<G.GestureModifier>(v))));
        if (d.Modifier != G.GestureModifier.None)
        {
            var repeat = Switch(d.ExecuteOnModifier, v => d.ExecuteOnModifier = v);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(repeat);
            line.Children.Add(Text(Strings.Settings_Gestures_ModifierDescription, DescProto));
            panel.Children.Add(FormLine("", line));
        }

        // ---- 动作 ----
        panel.Children.Add(FormLine(Strings.Settings_Gestures_Action, Choices(CommandKinds, KindOf(d.Command), v => d.Command = NewCommand(v))));
        if (CommandEditor(d) is { } editor) panel.Children.Add(FormLine("", editor));

        var name = Field(Strings.Settings_Gestures_NameDescription, 300);
        name.Text = d.Name;
        name.TextChanged += (_, _) => d.Name = name.Text;
        panel.Children.Add(FormLine(Strings.Common_Name, Wrap(name)));

        // ---- 保存 ----
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foot.Children.Add(Button(Strings.Common_Save, "check", () => SaveDraft(d)));
        foot.Children.Add(Button(Strings.Common_Cancel, null, () => Collapse("gesture", () => { draft = null; Render(); })));
        if (d.Error != null) foot.Children.Add(Status(false, d.Error));
        panel.Children.Add(FormLine("", foot));
        return panel;
    }

    // 画板：按住左键画，松开就是这次的方向（和真实手势同一个识别器；阈值比屏幕上小一点，画板不大）
    FrameworkElement DrawPad(GestureDraft d, bool diagonal, Action changed)
    {
        var line = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = Accent(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var canvas = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        canvas.Children.Add(line);
        var hint = Text(Strings.Settings_Gestures_DrawHint, DescProto);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.IsHitTestVisible = false;
        var host = new Grid { Height = 168 };
        host.Children.Add(hint);
        host.Children.Add(canvas);
        G.GestureRecognizer? recognizer = null;
        canvas.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(canvas);
            if (!p.Properties.IsLeftButtonPressed) return;
            canvas.CapturePointer(e.Pointer);
            recognizer = new G.GestureRecognizer(new((int)p.Position.X, (int)p.Position.Y), 64, diagonal);
            line.Points.Clear();
            line.Points.Add(p.Position);
            hint.Opacity = 0;
            d.Directions.Clear(); d.Error = null; changed();
            e.Handled = true;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (recognizer == null) return;
            var p = e.GetCurrentPoint(canvas).Position;
            line.Points.Add(p);
            if (recognizer.Add(new((int)p.X, (int)p.Y))) { d.Directions.Clear(); d.Directions.AddRange(recognizer.Directions); changed(); }
        };
        void End(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { if (recognizer == null) return; recognizer = null; canvas.ReleasePointerCapture(e.Pointer); }
        canvas.PointerReleased += End;
        canvas.PointerCaptureLost += (_, _) => recognizer = null;
        return new Border
        {
            CornerRadius = new CornerRadius(((CornerRadius)Application.Current.Resources["PaneRadiusControl"]).TopLeft), Background = FillProto.Background,
            BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1), Child = host,
        };
    }

    // 每种动作自己的参数
    FrameworkElement? CommandEditor(GestureDraft d)
    {
        switch (d.Command)
        {
            case G.HotkeyCommand h:
            {
                // 一个或连续几个按键（WGestures 的「连续按键」）
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var chords = h.Chords.ToList();
                for (int i = 0; i <= chords.Count; i++)
                {
                    if (i == chords.Count && chords.Count > 0) break;
                    int at = i;
                    var k = at < chords.Count ? chords[at] : default;
                    var box = new HotkeyBox(new Settings.Hotkey((KeyModifiers)(int)k.Modifiers, k.VirtualKey), SmallProto, KbdProto, FillProto.Background, FillProto.BorderBrush, Accent()) { AllowBareKeys = true };
                    box.RecordingChanged += (_, on) => app.SetHotkeysSuspended(on);
                    box.Changed += (_, v) =>
                    {
                        var next = new G.KeyChord((G.KeyModifiers)(int)v.Modifiers, v.VirtualKey);
                        var list = ((G.HotkeyCommand)d.Command).Chords.ToList();
                        if (at < list.Count) list[at] = next; else list.Add(next);
                        d.Command = new G.HotkeyCommand(list); d.Error = null;
                    };
                    row.Children.Add(box);
                }
                if (chords.Count > 0)
                {
                    row.Children.Add(IconButton("plus", Strings.Settings_Gestures_AddChord, () => { d.Command = new G.HotkeyCommand([.. ((G.HotkeyCommand)d.Command).Chords, default]); Render(); }));
                    if (chords.Count > 1) row.Children.Add(IconButton("close", Strings.Settings_Gestures_RemoveChord, () => { d.Command = new G.HotkeyCommand(((G.HotkeyCommand)d.Command).Chords.SkipLast(1).ToArray()); Render(); }));
                }
                return row;
            }
            case G.WindowCommand w:
                return Choices([("MaximizeRestore", Strings.Settings_Gestures_Maximize), ("Minimize", Strings.Settings_Gestures_Minimize), ("Close", Strings.Settings_Gestures_Close), ("ToggleTopmost", Strings.Settings_Gestures_ToggleTopmost), ("SnapLeft", Strings.Settings_Gestures_LeftHalf), ("SnapRight", Strings.Settings_Gestures_RightHalf)],
                    w.Action.ToString(), v => d.Command = new G.WindowCommand(Enum.Parse<G.WindowAction>(v)));
            case G.VolumeCommand v:
            {
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Choices([("Up", Strings.Settings_Gestures_IncreaseVolume), ("Down", Strings.Settings_Gestures_DecreaseVolume), ("ToggleMute", Strings.Settings_Gestures_ToggleMute), ("WheelAdjust", Strings.Settings_Gestures_FollowWheel)], v.Action.ToString(),
                    x => d.Command = (G.VolumeCommand)d.Command with { Action = Enum.Parse<G.VolumeAction>(x) }));
                if (v.Action != G.VolumeAction.ToggleMute)
                    stack.Children.Add(Choices([("2", Strings.Settings_Gestures_VolumeStepTwo), ("5", Strings.Settings_Gestures_VolumeStepFive), ("10", Strings.Settings_Gestures_VolumeStepTen)], v.Step.ToString(),
                        x => d.Command = (G.VolumeCommand)d.Command with { Step = int.Parse(x) }));
                return stack;
            }
            case G.OpenCommand o:
            {
                var box = Field(Strings.Settings_Gestures_OpenPlaceholder, 300);
                box.Text = o.Target;
                box.TextChanged += (_, _) => d.Command = new G.OpenCommand(box.Text.Trim());
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Wrap(box));
                row.Children.Add(Button(Strings.Common_ChooseFile, "folder", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if ((await picker.PickSingleFileAsync())?.Path is { } path) { d.Command = new G.OpenCommand(path); if (d.Name.Length == 0) d.Name = "打开 " + Path.GetFileNameWithoutExtension(path); Render(); } // 不翻译：用户数据
                }));
                return row;
            }
            case G.RunCommand r:
            {
                var box = Field(Strings.Settings_Gestures_RunPlaceholder, 300);
                box.Text = r.CommandLine;
                box.TextChanged += (_, _) => d.Command = (G.RunCommand)d.Command with { CommandLine = box.Text.Trim() };
                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(Wrap(box));
                var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                opts.Children.Add(Switch(r.InExplorerFolder, x => d.Command = (G.RunCommand)d.Command with { InExplorerFolder = x }));
                opts.Children.Add(Text(Strings.Settings_Gestures_RunInExplorer, DescProto));
                opts.Children.Add(Switch(r.ShowWindow, x => d.Command = (G.RunCommand)d.Command with { ShowWindow = x }));
                opts.Children.Add(Text(Strings.Settings_Gestures_ShowCommandWindow, DescProto));
                stack.Children.Add(opts);
                return stack;
            }
            case G.SendTextCommand t:
            {
                var box = Field(Strings.Settings_Gestures_TextPlaceholder, 300);
                box.Text = t.Sequence;
                box.TextChanged += (_, _) => d.Command = new G.SendTextCommand(box.Text);
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(Wrap(box));
                stack.Children.Add(Text(Strings.Settings_Gestures_SendKeysDescription, DescProto));
                return stack;
            }
            case G.WebSearchCommand s:
            {
                var box = Field(Strings.Settings_Gestures_UrlPlaceholder, 360);
                box.Text = s.UrlTemplate;
                box.TextChanged += (_, _) => d.Command = new G.WebSearchCommand(box.Text.Trim());
                return Wrap(box);
            }
        }
        return null;
    }

    void SaveDraft(GestureDraft d)
    {
        var config = app.GestureConfig;
        var pattern = new G.GesturePattern(d.Button, d.Directions.ToArray(), d.Modifier);
        string? error =
            d.Directions.Count == 0 && d.Modifier == G.GestureModifier.None ? Strings.Settings_Gestures_PatternRequired
            : d.Command switch
            {
                G.HotkeyCommand h when h.Chords.Count == 0 || h.Chords.Any(k => k.VirtualKey == 0) => Strings.Settings_Gestures_ChordRequired,
                G.OpenCommand o when o.Target.Length == 0 => Strings.Settings_Gestures_TargetRequired,
                G.RunCommand r when r.CommandLine.Length == 0 => Strings.Settings_Gestures_CommandRequired,
                G.SendTextCommand t when t.Sequence.Length == 0 => Strings.Settings_Gestures_TextRequired,
                G.WebSearchCommand w when !w.UrlTemplate.Contains("{query}") => Strings.Settings_Gestures_QueryRequired,
                G.VolumeCommand { Action: G.VolumeAction.WheelAdjust } when d.Modifier is not (G.GestureModifier.WheelUp or G.GestureModifier.WheelDown) => Strings.Settings_Gestures_WheelRequired,
                _ => null,
            }
            // 只在同一个列表里查重：程序专用手势和全局一样是允许的（这个程序里用专用的）
            ?? (BindingsOf(config, d.App).Select((g, i) => (g, i)).FirstOrDefault(x => x.i != d.Index && G.GestureMatcher.Same(x.g.Pattern, pattern)) is { g: { } same } ? string.Format(Strings.Settings_Gestures_DuplicatePattern, (object?)(same.Name)) : null);
        if (error != null) { d.Error = error; Render(); return; }
        var name = d.Name.Trim().Length > 0 ? d.Name.Trim() : CommandText(d.Command);
        var binding = new G.GestureBinding(name, pattern, d.Command, d.Modifier != G.GestureModifier.None && d.ExecuteOnModifier);
        var (index, appIndex) = (d.Index, d.App);
        draft = null;
        static IReadOnlyList<G.GestureBinding> Put(IReadOnlyList<G.GestureBinding> list, int index, G.GestureBinding b) =>
            index < 0 ? [.. list, b] : list.Select((g, i) => i == index ? b : g).ToArray();
        if (appIndex < 0) SaveGestures(x => x with { Global = Put(x.Global, index, binding) });
        else SaveGestures(x => x with { Apps = x.Apps.Select((r, j) => j == appIndex ? r with { Bindings = Put(r.Bindings, index, binding) } : r).ToArray() });
    }

    // 单选的小标签（复用 ToggleChip：选中的那个点了不响应）
    FrameworkElement Choices((string Key, string Label)[] options, string value, Action<string> pick)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var (key, label) in options)
            wrap.Children.Add(ToggleChip(label, null, key == value, true, on => { if (on) pick(key); }));
        return wrap;
    }

    // 编辑器里的一行：左边 72px 的标签，右边控件
    FrameworkElement FormLine(string label, FrameworkElement control)
    {
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Text(label, SmallProto);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, 5, 0, 0);
        line.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        line.Children.Add(control);
        return line;
    }
}
