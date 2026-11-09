// 设置窗口：能力（055）（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 能力（055，原型 settings.js renderModules） =================

    // 每块能力：名字、图标、一句话说明，以及和别的能力搭配时多出来的用法（软依赖）
    static (string Name, string Glyph, string Desc, (string Other, string Text)[] Links) ModuleCopy(string id) => id switch
    {
        ModuleIds.Launcher => (Strings.Common_Launcher, "search", Strings.Module_Launcher_Description,
            [(ModuleIds.Ai, Strings.Module_Launcher_WithAi), (ModuleIds.Clipboard, Strings.Module_Launcher_WithClipboard)]),
        ModuleIds.Clipboard => (Strings.Common_Clipboard, "clipboard", Strings.Module_Clipboard_Description,
            [(ModuleIds.Ai, Strings.Module_Clipboard_WithAi), (ModuleIds.Launcher, Strings.Module_Clipboard_WithLauncher)]),
        ModuleIds.Gestures => (Strings.Module_Gestures_Name, "gesture", Strings.Module_Gestures_Description, []),
        ModuleIds.SuperPanel => (Strings.Common_SuperPanel, "pointer", Strings.Module_SuperPanel_Description,
            [(ModuleIds.Ai, Strings.Module_Super_WithAi), (ModuleIds.Rename, Strings.Module_Super_WithRename)]),
        ModuleIds.Ai => ("AI", "ai", Strings.Module_Ai_Description, []),
        ModuleIds.FolderJump => (Strings.Module_FolderJump_Name, "folder", Strings.Module_FolderJump_Description, []),
        ModuleIds.Rename => (Strings.Common_Rename, "pencil", Strings.Module_Rename_Description,
            [(ModuleIds.Launcher, Strings.Module_Rename_FromLauncher), (ModuleIds.SuperPanel, Strings.Module_Rename_FromSuper)]),
        ModuleIds.PowerTimer => (Strings.Module_PowerTimer_Name, "timer", Strings.Module_PowerTimer_Description,
            [(ModuleIds.Launcher, Strings.Module_PowerTimer_WithLauncher)]),
        ModuleIds.Words => (Strings.Module_Words_Name, "words", Strings.Module_Words_Description,
            [(ModuleIds.Clipboard, Strings.Module_Words_WithClipboard), (ModuleIds.Launcher, Strings.Module_Words_WithLauncher)]),
        _ => (id, "app", "", []),   // 以后加的能力还没配文案时：至少能开关
    };

    string? moduleConfirm;   // 停用前要先确认的那块能力（剪贴板替换着 Win+V 时）

    // 能力状态变了（可能在后台线程）：导航增减分区；等开关动画播完再整页重建
    void OnModulesChanged(object? sender, string id) => DispatcherQueue.TryEnqueue(() =>
    {
        var shown = navItems.Select(i => (Section)i.Tag).ToList();
        if (!sections.Where(Visible).SequenceEqual(shown)) BuildNav();
        Later();
    });

    void RenderModules()
    {
        var catalog = app.Modules;
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = catalog.Modules;
        for (int i = 0; i < list.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = CapCard(list[i], catalog.StateOf(list[i].Id));
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            grid.Children.Add(card);
        }
        bool none = list.All(m => !catalog.StateOf(m.Id).Enabled);
        var group = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        group.Children.Add(grid);
        group.Children.Add(new TextBlock { Text = none ? Strings.Settings_Modules_NoneNote : Strings.Settings_Modules_Note, Margin = new Thickness(2, 10, 2, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = DescProto.Foreground });
        Body.Children.Add(group);
    }

    // 能力卡片（组件库 .cap）：图标块 + 名字 + 开关；一句话说明；搭配用法；出错 / 待重启 / 停用确认
    FrameworkElement CapCard(ModuleInfo info, ModuleState state)
    {
        var (name, glyph, desc, links) = ModuleCopy(info.Id);
        bool on = state.Enabled;
        var tile = new Border
        {
            Width = PaneTokens.ControlMd, Height = PaneTokens.ControlMd, CornerRadius = PaneTokens.RadiusControl,
            Background = SelectedProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1), Opacity = on ? 1 : 0.4,
            Child = new PaneIcon { Glyph = glyph, Width = 17, Height = 17, Foreground = LabelProto.Foreground },
        };
        var title = Text(name, LabelProto);
        title.FontWeight = FontWeights.SemiBold;
        if (!on) title.Foreground = SmallProto.Foreground;
        var toggle = Switch(on, v => SetModule(info.Id, v));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, string.Format(Strings.Module_Enable, (object?)name));
        var head = new Grid { ColumnSpacing = 10, MinHeight = PaneTokens.ControlMd };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(tile);
        Grid.SetColumn(title, 1); head.Children.Add(title);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 2); head.Children.Add(toggle);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = SmallProto.Foreground });
        foreach (var (other, text) in links)
            body.Children.Add(CapLine("link", catalog(other) ? text : text + string.Format(Strings.Module_PartnerOff, (object?)ModuleCopy(other).Name), false));
        // 启动器和超级面板都停用时，批量重命名改从托盘菜单打开
        if (info.Id == ModuleIds.Rename && !catalog(ModuleIds.Launcher) && !catalog(ModuleIds.SuperPanel))
            body.Children.Add(CapLine("link", Strings.Module_Rename_FromTray, false));
        if (state.Status == ModuleStatus.Failed && state.Problem is { Length: > 0 } problem)
            body.Children.Add(CapLine("warn", problem, true));
        if (state.Status == ModuleStatus.RestartPending)
        {
            body.Children.Add(CapLine("warn", state.Problem ?? Strings.Module_RestartPending, true));
            body.Children.Add(Button(Strings.Common_RestartNow, "refresh", app.Restart));
        }
        if (moduleConfirm == info.Id)
        {
            body.Children.Add(CapLine("warn", Strings.Module_ClipboardRestoresWinV, true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
            buttons.Children.Add(Button(Strings.Module_DisableAndRestore, null, () => { moduleConfirm = null; _ = ApplyModule(app.Modules.Preview(info.Id, false)); }));
            buttons.Children.Add(Button(Strings.Common_Cancel, null, () => { moduleConfirm = null; Render(); }));
            body.Children.Add(buttons);
        }
        return new Border
        {
            CornerRadius = RowsProto.CornerRadius, Background = RowsProto.Background, BorderBrush = RowsProto.BorderBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 12, 12), Child = body,
        };

        bool catalog(string id) => app.Modules.StateOf(id).Enabled;
    }

    FrameworkElement CapLine(string glyph, string text, bool warn)
    {
        var line = new Grid { ColumnSpacing = 5 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new PaneIcon { Glyph = glyph, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0),
            Foreground = warn ? PaneTokens.Warning : DescProto.Foreground });
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 18, FontFamily = DescProto.FontFamily, FontSize = DescProto.FontSize, Foreground = warn ? SmallProto.Foreground : DescProto.Foreground };
        Grid.SetColumn(t, 1);
        line.Children.Add(t);
        return line;
    }

    void SetModule(string id, bool enable)
    {
        var change = app.Modules.Preview(id, enable);
        // 停用会先恢复系统设置（剪贴板替换着 Win+V）：卡片里先说明，用户点「停用并恢复」才执行；开关弹回去
        if (change.RestoresSystemSetting) { moduleConfirm = id; Later(); return; }
        moduleConfirm = null;
        _ = ApplyModule(change);
    }

    async Task ApplyModule(ModuleChange change)
    {
        try { await app.Modules.ApplyAsync(change, CancellationToken.None); }
        catch (Exception) { /* 宿主把失败记在能力状态里（Failed + 原因），卡片会显示 */ }
        Later();
    }
}
