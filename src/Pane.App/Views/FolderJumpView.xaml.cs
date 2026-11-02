using Pane.Localization;
// 文件对话框跳转菜单逻辑（设计负责人，038）。行在每次弹出前按 IFolderJump.Entries() 重建；
// 选中高亮滑动（SlidingHighlight），鼠标移到哪行就选哪行；Enter / 数字键 / 点一下：先收起菜单，再让宿主跳。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views.Controls;
using Pane.Views.Files;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class FolderJumpView : UserControl, IPanelView
{
    const double RowHeight = 40, LabelHeight = 26, Width0 = 340;

    /// <summary>选了哪个文件夹（菜单已经收起）。</summary>
    public event EventHandler<string>? Jump;
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion => null;

    readonly SlidingHighlight highlight;
    readonly List<(Grid Row, string Path)> rows = [];
    double height = 12;
    int selected = -1;

    public FolderJumpView()
    {
        InitializeComponent();
        highlight = new SlidingHighlight(HighlightLayer);
        KeyDown += OnKeyDown;
    }

    /// <summary>弹出前填好菜单内容；宿主据此量尺寸、算位置。</summary>
    public void Apply(IReadOnlyList<FolderJumpEntry> entries)
    {
        Root.Children.Clear();
        rows.Clear();
        height = 12;
        if (entries.Count == 0)
        {
            Root.Children.Add(new TextBlock
            {
                Text = Strings.FolderJump_Empty, Margin = new Thickness(10, 10, 10, 10), TextWrapping = TextWrapping.Wrap,
                FontFamily = NameProto.FontFamily, FontSize = NameProto.FontSize, Foreground = PathProto.Foreground,
            });
            height += 44;
        }
        FolderJumpSource? group = null;
        foreach (var e in entries)
        {
            if (e.Source != group)
            {
                group = e.Source;
                Root.Children.Add(new TextBlock
                {
                    Text = e.Source == FolderJumpSource.Explorer ? Strings.FolderJump_Explorer : Strings.FolderJump_Recent, Height = LabelHeight, Padding = new Thickness(10, 8, 10, 0),
                    FontFamily = LabelProto.FontFamily, FontSize = LabelProto.FontSize, Foreground = LabelProto.Foreground,
                });
                height += LabelHeight;
            }
            var row = Row(e, rows.Count);
            rows.Add((row, e.Path));
            Root.Children.Add(row);
            height += RowHeight;
        }
        selected = -1;
        highlight.Hide();
        SizeRequested?.Invoke(this, PreferredSize);
    }

    Grid Row(FolderJumpEntry e, int index)
    {
        var g = new Grid { Height = RowHeight, Padding = new Thickness(8, 0, 10, 0), ColumnSpacing = 10, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center, Background = FileKinds.Brush(FileCategory.Folder),
            Child = new PaneIcon { Glyph = "folder", Width = 13, Height = 13, StrokeWidth = 2, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        text.Children.Add(new TextBlock { Text = e.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = NameProto.FontFamily, FontSize = NameProto.FontSize, Foreground = NameProto.Foreground });
        text.Children.Add(new TextBlock { Text = e.Path, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = PathProto.FontFamily, FontSize = PathProto.FontSize, Foreground = PathProto.Foreground });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (index < 9)
        {
            var kbd = new Border { Style = KbdProto.Style, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Style = ((TextBlock)KbdProto.Child).Style, Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            Grid.SetColumn(kbd, 2);
            g.Children.Add(kbd);
        }
        ToolTipService.SetToolTip(g, e.Path);
        g.PointerEntered += (_, _) => Select(index);
        g.Tapped += (_, _) => Run(index);
        return g;
    }

    public Size PreferredSize => new(Width0, height);

    void Select(int index)
    {
        if (index < 0 || index >= rows.Count) return;
        selected = index;
        highlight.MoveTo(rows[index].Row);
    }

    void Run(int index)
    {
        if (index < 0 || index >= rows.Count) return;
        var path = rows[index].Path;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        Jump?.Invoke(this, path);
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down when rows.Count > 0: Select(selected < 0 ? 0 : (selected + 1) % rows.Count); break;
            case VirtualKey.Up when rows.Count > 0: Select(selected < 0 ? rows.Count - 1 : (selected - 1 + rows.Count) % rows.Count); break;
            case VirtualKey.Enter when selected >= 0: Run(selected); break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9: Run(e.Key - VirtualKey.Number1); break;
            case >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9: Run(e.Key - VirtualKey.NumberPad1); break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }

    // 弹出时直接选中第一项：Ctrl+Shift+G 之后按 Enter 就跳到最近激活的那个资源管理器文件夹
    public void OnShowing(PanelShowContext context) { selected = -1; highlight.Hide(); }
    public void OnShown()
    {
        Focus(FocusState.Programmatic);
        if (rows.Count > 0) { UpdateLayout(); Select(0); }
    }
    public void OnHidden() { selected = -1; highlight.Hide(); }
}
