// 超级面板按场景变（设计负责人，148，issue 88，2026-11-26 用户定；docs/design.md「超级面板按场景变」，原型 ui/super.js showDialog / showHome）。
// 选中文件、选中文字照旧（SuperView.xaml.cs）；这里是另外两种：
// - 文件对话框在前台：文件夹列表。开着的资源管理器在前、最近用过的在后，各有小标题；最多 8 行，↑ ↓ 选、1～8 / Enter / 点一下跳过去。
// - 没选中、读不到：常用面板。常用应用 4 × 2（1～8 直接开）+ 一排小工具（剪贴板、新建备忘录、文件搜索、启动器）；格子之间 ← → ↑ ↓ 走。
//   读不到（管理员窗口、超时、剪贴板被占用）时顶上一行小字说明；没选中、程序不给读不写。
using Pane.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Contracts;
using Pane.Views.Controls;
using Windows.System;

namespace Pane.Views;

public sealed partial class SuperView
{
    enum Scene { Selection, Dialog, Home }
    Scene scene;
    readonly SlidingHighlight sceneHighlight;
    readonly List<(FrameworkElement Element, Func<Task> Run)> sceneItems = [];
    int sceneSelected;
    int appCount;

    /// <summary>
    /// 常用面板的小工具和「添加」（148）：clipboard / memo（新建一条）/ files（文件窗口）/ launcher / settings（设置 → 超级面板）。
    /// 面板先收起，宿主再打开对应的窗口。
    /// </summary>
    public event Action<string>? OpenTool;

    /// <summary>
    /// 宿主按场景选按键接管方式（148）：选中文件、选中文字用 <see cref="PanelKeyMode.Super"/>，文件夹列表和常用面板用 <see cref="PanelKeyMode.SuperGrid"/>。
    /// 在 <see cref="SetSelection"/> 之后、显示之前读。
    /// </summary>
    public PanelKeyMode KeyMode => selection is { } s && (s.Files is { Count: > 0 } || s.Dialog is null && !string.IsNullOrWhiteSpace(s.Text))
        ? PanelKeyMode.Super : PanelKeyMode.SuperGrid;

    // 设计预览：PANE_DESIGN_SELECTION=dialog
    static readonly FileDialogInfo DemoDialog = new(0, 0, 0, 0, 0, true, false, true, null, null, false, "记事本");   // 不翻译：演示数据
    static readonly FolderJumpEntry[] DemoFolders =   // 不翻译：演示数据
    [
        new(@"D:\工作\汇报", "汇报", FolderJumpSource.Explorer),
        new(@"C:\Users\你\Downloads", "下载", FolderJumpSource.Explorer),
        new(@"D:\项目\Pane\docs", "docs", FolderJumpSource.Recent),
        new(@"E:\照片\2026", "2026", FolderJumpSource.Recent),
        new(@"D:\个人\账本", "账本", FolderJumpSource.Recent),
    ];

    static App Host => (App)Application.Current;

    void SetScene(Scene value)
    {
        scene = value;
        bool sel = value == Scene.Selection;
        ActionsArea.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        SceneArea.Visibility = sel ? Visibility.Collapsed : Visibility.Visible;
        DialogHead.Visibility = value == Scene.Dialog ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Visibility = Visibility.Collapsed;
        if (sel) return;
        QuoteBox.Visibility = FileBox.Visibility = AutoBox.Visibility = LongBox.Visibility = ResultArea.Visibility = Visibility.Collapsed;
        autoRun?.Cancel();
        autoMode = false;
        files = null;
        selectionTarget = null;
        showVersion++;
        sceneItems.Clear();
        sceneHighlight.Hide();
        sceneSelected = -1;
    }

    // ---------------- 文件对话框：文件夹列表 ----------------

    void ShowDialogScene(FileDialogInfo d)
    {
        SetScene(Scene.Dialog);
        var kind = d.FoldersOnly ? Strings.Files_DialogFolder : d.Save ? Strings.Files_DialogSave : Strings.Files_DialogOpen;
        DialogText.Text = d.AppName is { Length: > 0 } app ? string.Format(Strings.Files_DialogTarget, (object?)kind, (object?)app) : string.Format(Strings.Files_DialogTargetNoApp, (object?)kind);
        IReadOnlyList<FolderJumpEntry> entries;
        try { entries = ReferenceEquals(d, DemoDialog) ? DemoFolders : Host.FolderJump?.Entries() ?? []; }
        catch (Exception) { entries = []; }
        var list = new StackPanel();
        FolderJumpSource? last = null;
        foreach (var entry in entries.Take(8))
        {
            if (entry.Source != last)
            {
                last = entry.Source;
                list.Children.Add(new TextBlock { Text = entry.Source == FolderJumpSource.Explorer ? Strings.FolderJump_Explorer : Strings.FolderJump_Recent, Padding = new Thickness(10, 8, 10, 4),
                    FontFamily = Text3Proto.FontFamily, FontSize = Text3Proto.FontSize, Foreground = Text3Proto.Foreground, CharacterSpacing = 20 });
            }
            var row = FolderRow(entry, sceneItems.Count + 1);
            var path = entry.Path;
            AddSceneItem(row, async () =>
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                if (Host.FolderJump is not { } jump || d.Dialog == 0) return;
                try { await jump.JumpAsync(d.Dialog, path, CancellationToken.None); }
                catch (Exception e) { Host.ReportFailure(Strings.FolderJump_JumpAction, e); }
            });
            list.Children.Add(row);
        }
        if (sceneItems.Count == 0)
            list.Children.Add(new TextBlock { Text = Strings.FolderJump_Empty, Padding = new Thickness(10, 14, 10, 14), TextWrapping = TextWrapping.Wrap,
                FontFamily = Text2Proto.FontFamily, FontSize = Text2Proto.FontSize, Foreground = Text3Proto.Foreground });
        SceneHost.Children.Clear();
        SceneHost.Children.Add(list);
        FinishScene();
    }

    // 一行：文件夹图标 18、名字 md、完整路径 xs text-3，右边数字键帽
    Grid FolderRow(FolderJumpEntry entry, int number)
    {
        var row = new Grid { Height = 44, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 12, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new PaneIcon { Glyph = "folder", Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Text2Proto.Foreground });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Name, LineHeight = 20, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Text1Proto.FontFamily, FontSize = Text1Proto.FontSize, Foreground = Text1Proto.Foreground });
        text.Children.Add(new TextBlock { Text = entry.Path, LineHeight = 15, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Text3Proto.FontFamily, FontSize = Text3Proto.FontSize, Foreground = Text3Proto.Foreground });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var cap = new Border
        {
            MinWidth = 18, Height = 18, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
            Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontFamily = Text3Proto.FontFamily, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Text3Proto.Foreground },
        };
        Grid.SetColumn(cap, 2);
        row.Children.Add(cap);
        return row;
    }

    // ---------------- 常用面板 ----------------

    static readonly (string Id, string Module, string Glyph)[] Tools =
        [("clipboard", ModuleIds.Clipboard, "clipboard"), ("memo", ModuleIds.Memo, "note"), ("files", ModuleIds.Launcher, "folder"), ("launcher", ModuleIds.Launcher, "search")];

    static string ToolName(string id) => id switch
    {
        "clipboard" => Strings.Common_Clipboard,
        "memo" => Strings.SuperPanel_NewMemo,
        "files" => Strings.SuperPanel_FileSearch,
        _ => Strings.Common_Launcher,
    };

    void ShowHomeScene(SelectionFailure failure)
    {
        SetScene(Scene.Home);
        // 读不到才说明；没选中（Empty）、程序不给读（Unsupported）分不清是不是真的没选，不写
        string? note = failure switch
        {
            SelectionFailure.Elevated => Strings.SuperPanel_SelectionPermission,
            SelectionFailure.Timeout => Strings.SuperPanel_SelectionTimeout,
            SelectionFailure.ClipboardUnavailable => Strings.SuperPanel_ClipboardUnavailable,
            _ => null,
        };
        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

        var grid = new Grid();
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        var apps = HomeApps();
        appCount = apps.Count;
        int rowIndex = 0;
        for (int i = 0; i < apps.Count; i++)
        {
            if (i % 4 == 0) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); rowIndex = grid.RowDefinitions.Count - 1; }
            var (id, name, icon) = apps[i];
            var tile = AppTile(name, icon);
            Grid.SetRow(tile, rowIndex);
            Grid.SetColumn(tile, i % 4);
            grid.Children.Add(tile);
            AddSceneItem(tile, async () =>
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                if (Host.AppLauncher is not { } launcher) return;
                try { await launcher.LaunchAsync(id, CancellationToken.None); }
                catch (Exception e) { Host.ReportFailure(Strings.Common_Open, e); }
            });
        }
        if (apps.Count == 0)
        {
            // 一个常用应用都没有：一行说明 + 胶囊「添加」（打开设置 → 超级面板）
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = new Grid { Height = 52, Padding = new Thickness(10, 0, 6, 0), ColumnSpacing = 10 };
            empty.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            empty.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            empty.Children.Add(new TextBlock { Text = Strings.SuperPanel_NoApps, VerticalAlignment = VerticalAlignment.Center, FontFamily = Text2Proto.FontFamily, FontSize = Text2Proto.FontSize, Foreground = Text3Proto.Foreground });
            var add = new Button { Content = Strings.Common_Add, Height = PaneTokens.ControlSm, CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2), VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current.Resources.TryGetValue("PaneTextButton", out var style) && style is Style s) add.Style = s;
            add.Click += (_, _) => { CloseRequested?.Invoke(this, EventArgs.Empty); OpenTool?.Invoke("settings"); };
            Grid.SetColumn(add, 1);
            empty.Children.Add(add);
            Grid.SetColumnSpan(empty, 4);
            grid.Children.Add(empty);
        }
        var tools = Tools.Where(t => ModuleGate.On(t.Module)).ToList();
        if (tools.Count > 0)
        {
            // 分隔线（上下 6），小工具总从新的一行开始
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6), Background = FillProto.BorderBrush };
            Grid.SetRow(line, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(line, 4);
            grid.Children.Add(line);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int r = grid.RowDefinitions.Count - 1;
            for (int i = 0; i < tools.Count; i++)
            {
                var id = tools[i].Id;
                var tool = new StackPanel { Padding = new Thickness(2, 8, 2, 6), Spacing = 5, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                tool.Children.Add(new PaneIcon { Glyph = tools[i].Glyph, Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Text1Proto.Foreground });
                tool.Children.Add(new TextBlock { Text = ToolName(id), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Text3Proto.FontFamily, FontSize = Text3Proto.FontSize, Foreground = Text1Proto.Foreground });
                Grid.SetRow(tool, r);
                Grid.SetColumn(tool, i);
                grid.Children.Add(tool);
                AddSceneItem(tool, () => { CloseRequested?.Invoke(this, EventArgs.Empty); OpenTool?.Invoke(id); return Task.CompletedTask; });
            }
        }
        SceneHost.Children.Clear();
        SceneHost.Children.Add(grid);
        FinishScene();
    }

    // 设置里的常用应用（SuperPanelSettings.Apps），按应用目录换成名字和图标；找不到的跳过。设计预览 PANE_DESIGN_SUPER_APPS=1 用目录前 8 个
    List<(string Id, string Name, string? Icon)> HomeApps()
    {
        Dictionary<string, AppIconInfo> catalog;
        try { catalog = Host.AppIcons.Apps().GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First()); }
        catch (Exception) { catalog = []; }
        IEnumerable<string> ids = Host.Store.Current.SuperPanel.Apps ?? [];
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_SUPER_APPS") == "1" && !ids.Any()) ids = catalog.Keys;
        return [.. ids.Where(catalog.ContainsKey).Select(id => (id, catalog[id].Name, catalog[id].Icon)).Take(Settings.SuperPanelSettings.MaxApps)];
    }

    // 应用格子：高 76，图标 36（没有图标文件时用占位图块）、名字 xs 一行
    StackPanel AppTile(string name, string? icon)
    {
        var tile = new StackPanel { Height = 76, Padding = new Thickness(4, 10, 4, 0), Spacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        FrameworkElement image = icon != null && System.IO.File.Exists(icon)
            ? new Image { Width = 36, Height = 36, Stretch = Stretch.Uniform, Source = new BitmapImage { DecodePixelWidth = 36, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(icon) } }
            : new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
                Child = new PaneIcon { Glyph = "app", Width = 20, Height = 20, Foreground = Text1Proto.Foreground } };
        image.HorizontalAlignment = HorizontalAlignment.Center;
        tile.Children.Add(image);
        tile.Children.Add(new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            FontFamily = Text3Proto.FontFamily, FontSize = Text3Proto.FontSize, Foreground = Text1Proto.Foreground });
        ToolTipService.SetToolTip(tile, name);
        return tile;
    }

    // ---------------- 选中、执行、按键 ----------------

    void AddSceneItem(FrameworkElement element, Func<Task> run)
    {
        int index = sceneItems.Count;
        sceneItems.Add((element, run));
        element.PointerEntered += (_, _) => { if (index != sceneSelected) SceneSelect(index); };
        element.Tapped += (_, _) => _ = SceneRun(index);
    }

    void FinishScene()
    {
        UpdateLayout();
        if (sceneItems.Count > 0) SceneSelect(0, animate: false);
        RequestHeight();
    }

    void SceneSelect(int index, bool animate = true)
    {
        if (sceneItems.Count == 0) return;
        sceneSelected = (index % sceneItems.Count + sceneItems.Count) % sceneItems.Count;
        sceneHighlight.MoveTo(sceneItems[sceneSelected].Element, animate);
    }

    async Task SceneRun(int index)
    {
        if (index < 0 || index >= sceneItems.Count) return;
        SceneSelect(index);
        await sceneItems[index].Run();
    }

    // 常用面板 ↑ ↓：在应用格子和小工具之间按列走；文件夹列表一行一行走（不回绕）
    void SceneVertical(int dir)
    {
        int n = sceneItems.Count, i = Math.Max(0, sceneSelected);
        if (n == 0) return;
        if (scene == Scene.Dialog) { if (i + dir >= 0 && i + dir < n) SceneSelect(i + dir); return; }
        int appRows = (appCount + 3) / 4;
        int row = i < appCount ? i / 4 : appRows, col = i < appCount ? i % 4 : i - appCount;
        int to = row + dir;
        if (to < 0 || to > appRows || to == appRows && n == appCount) return;
        SceneSelect(to == appRows ? appCount + Math.Min(col, n - appCount - 1) : Math.Min(to * 4 + col, appCount - 1));
    }

    bool SceneKey(VirtualKey key, bool ctrl)
    {
        switch (key)
        {
            case VirtualKey.Left when scene == Scene.Home: SceneSelect(sceneSelected - 1); break;
            case VirtualKey.Right when scene == Scene.Home: SceneSelect(sceneSelected + 1); break;
            case VirtualKey.Up: SceneVertical(-1); break;
            case VirtualKey.Down: SceneVertical(1); break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number8 when !ctrl:
            {
                int i = key - VirtualKey.Number1;
                if (scene == Scene.Dialog || i < appCount) _ = SceneRun(i);
                break;
            }
            case VirtualKey.Enter: _ = SceneRun(sceneSelected); break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); break;
            default: return false;
        }
        return true;
    }
}
