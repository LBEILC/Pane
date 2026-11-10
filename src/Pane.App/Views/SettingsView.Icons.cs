// 设置窗口：应用图标（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 应用图标 =================

    string? iconQuery;                        // null = 没在添加；否则是「添加应用」搜索框里的文字
    StackPanel? iconResults;

    void RenderIcons()
    {
        var s = app.Store.Current.Icons;
        var catalog = app.AppIcons;
        var rows = new List<FrameworkElement>
        {
            Row(Strings.Settings_Icons_DefaultMask, Strings.Settings_Icons_DefaultMaskDescription,
                Switch(s.MaskByDefault, v => { SaveIcons(x => x with { MaskByDefault = v }); Later(); })),
        };
        if (catalog.Rebuilding) rows.Add(Row(Strings.Settings_Icons_Rebuilding, Strings.Settings_Icons_RebuildingDescription, Status(true, Strings.Settings_Icons_Generating)));
        Group(Strings.Settings_Icons_Mask, rows.ToArray());

        // 文件图标（036，评论区建议）：默认仍是 Pane 的彩色类型图标；选「系统」和资源管理器一样。只影响文件窗口和启动器里的文件行
        Group(Strings.Settings_Icons_FileIcons, Row(Strings.Settings_Icons_Files, s.SystemFileIcons ? Strings.Settings_Icons_SystemDescription : Strings.Settings_Icons_PaneDescription,
            Seg([("pane", Strings.Settings_Icons_Pane), ("system", Strings.Settings_Icons_System)], s.SystemFileIcons ? "system" : "pane", v => { SaveIcons(x => x with { SystemFileIcons = v == "system" }); Later(); })));

        var apps = catalog.Apps().ToDictionary(a => a.Id);
        var ids = s.Overrides.Keys.Union(s.CustomIcons.Keys)
            .OrderBy(id => apps.TryGetValue(id, out var a) ? a.Name : id, StringComparer.CurrentCultureIgnoreCase).ToList();
        var list = new List<FrameworkElement>();
        foreach (var id in ids)
        {
            apps.TryGetValue(id, out var info);
            var mode = s.Overrides.GetValueOrDefault(id);
            bool custom = s.CustomIcons.ContainsKey(id);
            var sub = info == null ? Strings.Settings_Icons_AppMissing : custom ? string.Format(Strings.Settings_Icons_CustomDescription, KindText(s.MaskByDefault ? mode : IconMode.Raw, info.Kind)) : KindText(s.MaskByDefault ? mode : IconMode.Raw, info.Kind);
            var seg = Seg([("Auto", Strings.Common_Auto), ("ForceMask", Strings.Settings_Icons_ForceMask), ("Raw", Strings.Settings_Icons_Raw)], mode.ToString(),
                v => { SaveIcons(x => x with { Overrides = new(x.Overrides) { [id] = Enum.Parse<IconMode>(v) } }); Later(); });
            seg.MinWidth = 220;
            Enable(seg, s.MaskByDefault);
            list.Add(ListRow(AppIcon(info?.Icon), Text(info?.Name ?? id, LabelProto), sub, false, seg,
                IconButton("image", Strings.Settings_Icons_ChangeImage, async () => { if (await PickIcon(id)) Render(); }),
                IconButton("close", Strings.Common_ResetDefaults, () => { ResetIcon(id); Render(); })));
        }
        list.Add(iconQuery == null
            ? AddRow(Button(Strings.Settings_Icons_AddApp, "plus", () => { iconQuery = ""; Render(); }))
            : IconSearch(apps.Values, ids));
        Group(Strings.Settings_Icons_PerApp, list.ToArray(), Strings.Settings_Icons_PerAppDescription);
    }

    static string KindText(IconMode mode, AppIconKind kind)
    {
        var text = mode == IconMode.Raw ? Strings.Settings_Icons_Original : kind switch
    {
        AppIconKind.Full => Strings.Settings_Icons_FullBleed,
        AppIconKind.Plate => Strings.Settings_Icons_ColoredPlate,
        AppIconKind.Neutral => Strings.Settings_Icons_NeutralPlate,
        AppIconKind.Raw => Strings.Settings_Icons_Original,
        _ => Strings.Settings_Icons_NotGenerated,
    };
        return mode == IconMode.ForceMask ? string.Format(Strings.Settings_Icons_ForcedDescription, text) : text;
    }

    // 行首：应用现在的图标（和启动器里一样大小比例）；没有图就用占位图块
    FrameworkElement AppIcon(string? path)
    {
        if (path != null && File.Exists(path))
            return new Image { Width = 30, Height = 30, Stretch = Stretch.Uniform, Source = new BitmapImage { DecodePixelWidth = 30, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) } };
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = FillProto.Background, BorderBrush = FillProto.BorderBrush, BorderThickness = new Thickness(1),
            Child = new PaneIcon { Glyph = "app", Width = 16, Height = 16, Foreground = IconProto.Foreground },
        };
    }

    // 「添加应用」：就地搜索框 + 最多 6 个结果；只刷新结果，不重建整页（否则输入框会丢焦点）
    FrameworkElement IconSearch(IEnumerable<AppIconInfo> all, List<string> taken)
    {
        var box = Field(Strings.Settings_Icons_SearchPlaceholder, 260);
        box.Text = iconQuery ?? "";
        iconResults = new StackPanel { Spacing = 2 };
        var candidates = all.Where(a => !taken.Contains(a.Id)).ToList();
        void Fill()
        {
            iconResults!.Children.Clear();
            var q = (iconQuery ?? "").Trim();
            if (q.Length == 0) return;
            foreach (var a in candidates.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(6))
            {
                var pick = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                pick.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = AppIcon(a.Icon);
                icon.Width = icon.Height = 24;
                pick.Children.Add(icon);
                var name = Text(a.Name, LabelProto);
                Grid.SetColumn(name, 1);
                pick.Children.Add(name);
                pick.PointerEntered += (_, _) => pick.Background = SelectedProto.Background;
                pick.PointerExited += (_, _) => pick.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                pick.Tapped += (_, _) =>
                {
                    iconQuery = null;
                    SaveIcons(x => x with { Overrides = new(x.Overrides) { [a.Id] = IconMode.Auto } });
                    Render();
                };
                iconResults.Children.Add(pick);
            }
            if (iconResults.Children.Count == 0) iconResults.Children.Add(Text(Strings.Common_NotFound, DescProto));
        }
        box.TextChanged += (_, _) => { iconQuery = box.Text; Fill(); };
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { iconQuery = null; Render(); e.Handled = true; } };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.StartBringIntoView(); };
        Fill();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Wrap(box));
        head.Children.Add(Button(Strings.Common_Cancel, null, () => { iconQuery = null; Render(); }));
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(14, 10, 14, 10) };
        panel.Children.Add(head);
        panel.Children.Add(iconResults);
        return panel;
    }

    // 换图标：复制一份到数据目录（原图移走、删掉也不影响），文件名带时间，内容变了缓存键跟着变
    async Task<bool> PickIcon(string id)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        if ((await picker.PickSingleFileAsync())?.Path is not { } source) return false;
        var dir = Path.Combine(app.Store.DirectoryPath, "custom-icons");
        Directory.CreateDirectory(dir);
        var stem = IconFileStem(id);
        var target = Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, true);
        DeleteCustomIcons(stem, keep: target);
        SaveIcons(x => x with { CustomIcons = new(x.CustomIcons) { [id] = target } });
        return true;
    }

    void ResetIcon(string id)
    {
        SaveIcons(x =>
        {
            var overrides = new Dictionary<string, IconMode>(x.Overrides);
            var customs = new Dictionary<string, string>(x.CustomIcons);
            overrides.Remove(id);
            customs.Remove(id);
            return x with { Overrides = overrides, CustomIcons = customs };
        });
        DeleteCustomIcons(IconFileStem(id), keep: null);
    }

    static string IconFileStem(string id) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];

    // 只删 Pane 自己复制进数据目录的那几份
    void DeleteCustomIcons(string stem, string? keep)
    {
        var dir = Path.Combine(app.Store.DirectoryPath, "custom-icons");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, stem + "-*"))
            if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void SaveIcons(Func<IconSettings, IconSettings> change) =>
        app.Store.Save(app.Store.Current with { Icons = change(app.Store.Current.Icons) });

    void OnIconsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "icons" && iconQuery == null) Later(0); });
}
