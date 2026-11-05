// 呼出时的提示条（设计负责人，issue 25、062）：启动器、剪贴板面板最上面的「新版本已下载好」「已更新到」。docs/design.md「更新说明与更新提示」。
// 已下载好：服务 Ready、不是忽略过的版本；更新后：运行的版本比看过的新、这一版有重点条目，最多在 3 次呼出里出现。两种都满足时只显示已下载好。
using System.Reflection;
using Pane.Contracts;
using Pane.Views;
using Pane.Views.Controls;
namespace Pane;
public sealed partial class App
{
    /// <summary>正在运行的版本（开发版 / 演示时用程序集版本）。</summary>
    string RunningVersion => Updates?.CurrentVersion is { Length: > 0 } v && char.IsDigit(v[0]) ? v
        : typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

    /// <summary>面板呼出时问一次：现在要显示哪条提示（没有就是 null）。演示：PANE_DESIGN_NOTICE=ready|updated 强制显示。</summary>
    internal PanelNotice? CurrentNotice()
    {
        var u = Store.Current.Update;
        switch (Environment.GetEnvironmentVariable("PANE_DESIGN_NOTICE"))
        {
            case "ready": return new(PanelNoticeKind.UpdateReady, "0.1.380", "新增 3 项、修复 2 项"); // 不翻译：演示数据
            case "updated": return new(PanelNoticeKind.Updated, RunningVersion, "设置 → 关于里能直接看到每个版本的更新内容"); // 不翻译：演示数据
        }
        if (Updates is { State: UpdateState.Ready, Latest: { } latest } && !(u.IgnoredVersion is { } ignored && SettingsView.SameVersion(ignored, latest.Version)))
            return new(PanelNoticeKind.UpdateReady, latest.Version, latest.Details is { } d ? SettingsView.Summary(d) : "");
        string running = RunningVersion;
        if (u.NewsShown < 3 && Newer(running, u.SeenVersion) && ReleaseHistory?.Find(running) is { } notes && notes.Items.FirstOrDefault(i => i.Highlight) is { } first)
            return new(PanelNoticeKind.Updated, running, first.Text);
        return null;
    }

    /// <summary>提示条真的显示出来了一次（「更新后」计次）。</summary>
    internal void NoticeShown(PanelNotice notice)
    {
        if (notice.Kind != PanelNoticeKind.Updated || Environment.GetEnvironmentVariable("PANE_DESIGN_NOTICE") != null) return;
        var u = Store.Current.Update;
        int shown = u.NewsShown + 1;
        Store.Save(Store.Current with { Update = shown >= 3 ? u with { SeenVersion = notice.Version, NewsShown = 0 } : u with { NewsShown = shown } });
    }

    /// <summary>重启更新 / 查看。</summary>
    internal void NoticePrimary(PanelNotice notice)
    {
        if (notice.Kind == PanelNoticeKind.UpdateReady) { if (Updates is { State: UpdateState.Ready } u) u.InstallAndRestart(); return; }
        MarkNewsSeen(notice.Version);
        SettingsView.PendingChangelog = notice.Version;
        OpenSettings("about");
    }

    /// <summary>忽略这个版本 / 不再提示。</summary>
    internal void NoticeDismiss(PanelNotice notice)
    {
        if (notice.Kind == PanelNoticeKind.UpdateReady) { if (Environment.GetEnvironmentVariable("PANE_DESIGN_NOTICE") == null) Store.Save(Store.Current with { Update = Store.Current.Update with { IgnoredVersion = notice.Version } }); return; }
        MarkNewsSeen(notice.Version);
    }

    void MarkNewsSeen(string version)
    {
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_NOTICE") != null) return;
        Store.Save(Store.Current with { Update = Store.Current.Update with { SeenVersion = version, NewsShown = 0 } });
    }

    // seen 为 null（工程迁移前）当作已看过，避免新装的用户也看到「已更新到」
    static bool Newer(string running, string? seen) =>
        seen != null && Version.TryParse(running, out var r) && (seen == "0" || Version.TryParse(seen, out var s) && r > s);
}
