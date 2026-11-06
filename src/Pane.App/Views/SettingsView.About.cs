// 设置窗口：关于、更新（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 关于 =================

    void RenderAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? Strings.Update_DevelopmentVersion;
        Group(null,
            Row("Pane", Strings.Settings_About_DescriptionTagline, Chip(version)));
        if (app.Updates is { } updates) RenderUpdate(updates);
        Group(Strings.Settings_About_Information,
            Row(Strings.Settings_About_License, Strings.Settings_About_LicenseDescription, Button(Strings.Common_Open, "external", () => OpenUrl(SourceUrl))),
            Row(Strings.Settings_About_DataLocation, app.Store.DirectoryPath, Button(Strings.Common_Open, "external", () =>
            {
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(app.Store.DirectoryPath);
                System.Diagnostics.Process.Start(start)?.Dispose();
            })),
            UninstallRow());
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        var noticeButton = Button(Strings.Settings_About_ViewAll, "external", () => OpenUrl(notices));
        Enable(noticeButton, File.Exists(notices));
        Group(Strings.Settings_About_Components,
            ListRow("app", Strings.Settings_About_Lucide, Strings.Settings_About_LucideLicense, false),
            ListRow("app", "Windows App SDK", Strings.Settings_About_WinUiLicense, false),
            ListRow("text", "MiSans", Strings.Settings_About_FontLicense, false),
            Row(Strings.Settings_About_AllComponents, Strings.Settings_About_NoticesLocation, noticeButton));
    }

    // 卸载 Pane（2026-11-02，issue 11 / 051）：放在「数据位置」下面，按钮打开卸载程序的窗口（那里再确认、选是否删数据）；开发版灰掉并说明
    FrameworkElement UninstallRow()
    {
        bool can = app.CanUninstall;
        var button = Button(Strings.Settings_About_UninstallButton, "trash", app.StartUninstall);
        Enable(button, can);
        return Row(Strings.Settings_About_Uninstall, can ? Strings.Settings_About_UninstallDescription : Strings.Settings_About_UninstallUnavailable, button);
    }

    const string SourceUrl = "https://github.com/LBEILC/Pane";

    static void OpenUrl(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    // 软件内更新（2026-10-30，023）：状态一行 + 按钮；有新版时下面列出更新内容；「自动更新」开关
    CancellationTokenSource? updateWork;
    bool latestNotesOpen, changelogOpen;
    string? changelogVersion;
    /// <summary>提示条「已更新到 x」点了「查看」：打开关于页时展开更新日志并选中这一版（062）。</summary>
    internal static string? PendingChangelog { get; set; } = Environment.GetEnvironmentVariable("PANE_DESIGN_CHANGELOG");   // 设计预览：直接展开更新日志的某一版

    internal static bool SameVersion(string a, string b) => System.Version.TryParse(a, out var x) && System.Version.TryParse(b, out var y) ? x == y : a == b;

    // 更新说明（组件库 .release-notes）：按 新增 / 改进 / 修复 分组，小标题带图标，下面是条目；limit 之外收成「还有 N 条」
    FrameworkElement NotesBlock(ReleaseNotes notes, int limit, Action? more)
    {
        var block = new StackPanel { Spacing = 12 };
        int shown = 0;
        foreach (var (kind, glyph, title) in new[] { (ReleaseNoteKind.New, "plus", Strings.Update_KindNew), (ReleaseNoteKind.Improved, "improve", Strings.Update_KindImproved), (ReleaseNoteKind.Fixed, "fix", Strings.Update_KindFixed) })
        {
            var items = notes.Items.Where(i => i.Kind == kind).ToList();
            if (items.Count == 0 || shown >= limit) continue;
            var section = new StackPanel { Spacing = 5 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 1) };
            head.Children.Add(new PaneIcon { Glyph = glyph, Width = 13, Height = 13, Foreground = GroupTitleProto.Foreground, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = title, FontFamily = GroupTitleProto.FontFamily, FontSize = DescProto.FontSize, FontWeight = GroupTitleProto.FontWeight, Foreground = GroupTitleProto.Foreground });
            section.Children.Add(head);
            foreach (var item in items.Take(limit - shown)) { section.Children.Add(NoteLine(item.Text)); shown++; }
            block.Children.Add(section);
        }
        if (notes.Items.Count > shown && more != null)
        {
            var rest = Button(string.Format(Strings.Update_MoreItems, (object?)(notes.Items.Count - shown)), "chev-d", more);
            rest.HorizontalAlignment = HorizontalAlignment.Left;
            block.Children.Add(rest);
        }
        return block;
    }

    // 一条说明：圆点 + 正文（可选中复制）
    FrameworkElement NoteLine(string text)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new TextBlock { Text = "•", FontFamily = SmallProto.FontFamily, FontSize = SmallProto.FontSize, Foreground = DescProto.Foreground });
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21, IsTextSelectionEnabled = true, FontFamily = SmallStrongProto.FontFamily, FontSize = SmallStrongProto.FontSize, Foreground = SmallStrongProto.Foreground };
        Grid.SetColumn(body, 1);
        line.Children.Add(body);
        return line;
    }

    // 「新增 3 项、修复 2 项」（关于页更新日志的版本行、呼出时的提示条共用）
    internal static string Summary(ReleaseNotes notes) => string.Join(Strings.Update_SummarySeparator,
        new[] { (ReleaseNoteKind.New, Strings.Update_KindNew), (ReleaseNoteKind.Improved, Strings.Update_KindImproved), (ReleaseNoteKind.Fixed, Strings.Update_KindFixed) }
            .Select(k => (Label: k.Item2, Count: notes.Items.Count(i => i.Kind == k.Item1))).Where(k => k.Count > 0)
            .Select(k => string.Format(Strings.Update_KindCount, (object?)k.Label, (object?)k.Count)));

    void RenderUpdate(IUpdateService u)
    {
        void Run(Func<CancellationToken, Task> work)
        {
            updateWork?.Cancel();
            updateWork = new CancellationTokenSource();
            _ = Guard(work(updateWork.Token));
            static async Task Guard(Task t) { try { await t; } catch (Exception) { } }   // 失败由服务放进 State / Error
        }
        string Ago(DateTimeOffset? t) => t is not { } v ? Strings.Update_NeverChecked
            : DateTimeOffset.Now - v < TimeSpan.FromMinutes(1) ? Strings.Update_JustChecked
            : DateTimeOffset.Now - v < TimeSpan.FromHours(1) ? string.Format(Strings.Update_CheckedMinutesAgo, (object?)((int)(DateTimeOffset.Now - v).TotalMinutes))
            : DateTimeOffset.Now - v < TimeSpan.FromDays(1) ? string.Format(Strings.Update_CheckedHoursAgo, (object?)((int)(DateTimeOffset.Now - v).TotalHours)) : string.Format(Strings.Update_CheckedOn, (object?)(v.LocalDateTime));
        string Size(long b) => $"{b / 1024.0 / 1024:0.#} MB";
        var check = Button(u.State == UpdateState.Checking ? Strings.Update_Checking : Strings.Update_CheckNow, "refresh", () => Run(u.CheckAsync));
        Enable(check, u.State is not (UpdateState.Checking or UpdateState.Downloading));
        var rows = new List<FrameworkElement>();
        var latest = u.Latest;
        switch (u.State)
        {
            case UpdateState.Available when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_AvailableVersion, (object?)(latest.Version)), string.Format(Strings.Update_ReleaseDetails, (object?)(Size(latest.SizeBytes)), (object?)(latest.PublishedAt.LocalDateTime)),
                    Button(Strings.Update_Download, "plus", () => Run(u.DownloadAsync))));
                break;
            case UpdateState.Downloading when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_DownloadingVersion, (object?)(latest.Version)), string.Format(Strings.Update_DownloadProgress, (object?)(u.DownloadProgress), (object?)(Size(latest.SizeBytes))), Chip($"{u.DownloadProgress:P0}")));
                break;
            case UpdateState.Ready when latest is not null:
                rows.Add(Row(string.Format(Strings.Update_ReadyVersion, (object?)(latest.Version)), Strings.Update_InstallDescription,
                    Button(Strings.Update_RestartAndUpdate, "refresh", () => u.InstallAndRestart())));
                break;
            case UpdateState.Failed:
                rows.Add(Row(Strings.Update_Failed, null, Status(false, u.Error ?? Strings.Common_UnexpectedProblem), Button(Strings.Common_Retry, "refresh", () => Run(u.FailedStep == UpdateStep.Download ? u.DownloadAsync : u.CheckAsync))));
                break;
            default:
                rows.Add(Row(u.State == UpdateState.UpToDate ? Strings.Update_UpToDate : Strings.Update_CheckNow, string.Format(Strings.Update_CurrentVersion, (object?)(u.CurrentVersion), (object?)(Ago(u.LastChecked))), check));
                break;
        }
        // 新版本的更新内容（issue 25、062）：服务解析出结构就按 新增 / 改进 / 修复 分组，多于 8 条先露 8 条；旧格式的正文照原来逐行列出
        if (u.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready && latest is not null)
        {
            FrameworkElement? block = latest.Details is { Items.Count: > 0 } d ? NotesBlock(d, latestNotesOpen ? int.MaxValue : 8, () => { latestNotesOpen = true; Render(); }) : null;
            if (block == null && latest.Notes.Length > 0)
            {
                var plain = new StackPanel { Spacing = 4 };
                foreach (var line in latest.Notes.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•').Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Take(12))
                    plain.Children.Add(NoteLine(line));
                block = plain;
            }
            var tail = new StackPanel { Spacing = 10, Padding = new Thickness(14, 2, 14, 12) };
            if (block != null) tail.Children.Add(block);
            var link = Button(Strings.Update_ViewOnGitHub, "external", () => OpenUrl(latest.PageUrl));
            link.HorizontalAlignment = HorizontalAlignment.Left;
            tail.Children.Add(link);
            rows.Add(tail);
        }
        var update = app.Store.Current.Update;
        rows.Add(Row(Strings.Update_Automatic, Strings.Update_AutomaticDescription,
            Switch(update.AutoUpdate, v => { app.Store.Save(app.Store.Current with { Update = app.Store.Current.Update with { AutoUpdate = v } }); Render(); })));
        var onStart = Switch(update.InstallOnStartup, v => app.Store.Save(app.Store.Current with { Update = app.Store.Current.Update with { InstallOnStartup = v } }));
        Enable(onStart, update.AutoUpdate);
        rows.Add(Row(Strings.Update_InstallOnStartup, Strings.Update_InstallOnStartupDescription, onStart));
        // 更新日志：随安装包带着的历代说明，版本列表，点一行展开那一版（默认展开当前运行的版本）
        if (app.ReleaseHistory is { All.Count: > 0 } history)
        {
            if (PendingChangelog is { } wanted) { PendingChangelog = null; changelogOpen = true; changelogVersion = history.Find(wanted)?.Version ?? history.All[0].Version; }
            rows.Add(Row(Strings.Update_Changelog, string.Format(Strings.Update_ChangelogDescription, (object?)history.All[^1].Version),
                Button(changelogOpen ? Strings.Common_Collapse : Strings.Update_ChangelogOpen, null, () =>
                {
                    changelogOpen = !changelogOpen;
                    changelogVersion ??= history.Find(u.CurrentVersion)?.Version ?? history.All[0].Version;
                    Render();
                })));
            if (changelogOpen)
                foreach (var n in history.All)
                {
                    bool current = SameVersion(n.Version, u.CurrentVersion), open = changelogVersion == n.Version;
                    var label = current ? string.Format(Strings.Update_CurrentTag, (object?)n.Version) : n.Version;
                    var desc = n.Date is { } date ? $"{date:yyyy-MM-dd} · {Summary(n)}" : Summary(n);
                    var toggle = IconButton("chev-d", open ? Strings.Common_Collapse : Strings.Update_ChangelogOpen, () => { changelogVersion = open ? "" : n.Version; Render(); });
                    if (open) toggle.Content = new PaneIcon { Glyph = "chev-d", Width = 16, Height = 16, Foreground = IconProto.Foreground, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = 180 } };
                    rows.Add(Row(label, desc, toggle));
                    if (open) rows.Add(new Border { Padding = new Thickness(14, 0, 14, 12), Child = NotesBlock(n, int.MaxValue, null) });
                }
        }
        Group(Strings.Update_Title, rows.ToArray());
    }
}
