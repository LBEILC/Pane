// 设置窗口：数据 →「同步」组（设计负责人，2026-11-24）。照原型 prototype/settings.html#data，规则见 docs/cloud.md「同步什么」「剪贴板同步」，
// 接口 Contracts/Cloud.cs（ISyncService、SyncOptions）。只在「Pane 云」能力启用时出现，排在数据分区最前面。
// 第一行：总开关 + 状态（上次同步 / 正在同步 / 失败重试 / 暂停原因）+「立即同步」；换账号要合并时多一行三个选择；
// 打开后：设置、使用记录、剪贴板三类各自开关；剪贴板开着时再给范围、不同步的程序、写进本机剪贴板、其他电脑条目的显示方式。
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.Localization;
using Pane.Views.Controls;

namespace Pane.Views;

public sealed partial class SettingsView
{
    ISyncService? syncHooked;
    bool syncBusy;

    void RenderSync()
    {
        if (!app.Modules.IsActive(ModuleIds.Cloud)) return;
        var account = Account;
        if (account is null || account.State != AccountState.SignedIn)
        {
            var go = Button(Strings.Settings_Account_Login, "login", () => ShowSection("account"));
            var off = Switch(false, _ => { });
            Enable(off, false);
            Group(Strings.Settings_Sync, Row(Strings.Settings_Sync_Cloud, Strings.Settings_Sync_SignedOut, go, off));
            return;
        }
        if (app.Sync is not { } sync) return;
        if (!ReferenceEquals(syncHooked, sync))
        {
            if (syncHooked != null) syncHooked.Changed -= OnSyncChanged;
            sync.Changed += OnSyncChanged;
            syncHooked = sync;
        }
        var o = sync.Options;
        void Apply(Func<SyncOptions, SyncOptions> change) => _ = sync.ApplyAsync(change(sync.Options), CancellationToken.None);

        // 状态说明和右侧动作
        string desc;
        bool warn = false;
        var actions = new List<FrameworkElement>();
        if (!o.Enabled) desc = Strings.Settings_Sync_OffDescription;
        else switch (sync.State)
        {
            case SyncState.Syncing:
                desc = Strings.Settings_Sync_Syncing;
                break;
            case SyncState.Error:
                desc = string.Format(Strings.Settings_Sync_ErrorRetry, AcErrorText(new CloudResult(sync.ErrorCode ?? "network")));
                warn = true;
                actions.Add(SyncNowButton(sync));
                break;
            case SyncState.Paused:
                warn = true;
                (desc, var button) = sync.PauseReason switch
                {
                    SyncPauseReason.NoSubscription => (string.Format(Strings.Settings_Sync_PausedExpired, AcDate(account.Profile?.Subscription.RetainUntil)), Button(Strings.Settings_Account_Renew, null, () => ShowSection("account"))),
                    SyncPauseReason.NeedsSignIn => (Strings.Settings_Sync_PausedSignIn, Button(Strings.Settings_Account_Login, "login", () => ShowSection("account"))),
                    SyncPauseReason.ClientOutdated => (Strings.Settings_Sync_PausedOutdated, Button(Strings.Settings_About, null, () => ShowSection("about"))),
                    _ => (Strings.Settings_Sync_PausedMerge, (FrameworkElement?)null),
                };
                if (button != null) actions.Add(button);
                break;
            default:
                desc = sync.LastSuccessAt is { } at ? string.Format(Strings.Settings_Sync_LastSuccess, AcAgo(at)) : Strings.Settings_Sync_Waiting;
                actions.Add(SyncNowButton(sync));
                break;
        }
        actions.Add(Switch(o.Enabled, v => Apply(x => x with { Enabled = v })));
        var first = Row(Strings.Settings_Sync_Cloud, warn ? null : desc, actions);
        if (warn)   // 出问题时说明换成带警告图标的一句，位置同普通说明
            ((Microsoft.UI.Xaml.Controls.StackPanel)first.Children[0]).Children.Add(new Microsoft.UI.Xaml.Controls.Border { Margin = new Thickness(0, 3, 0, 0), Child = Status(false, desc) });
        var rows = new List<FrameworkElement> { first };

        if (o.Enabled && sync.State == SyncState.Paused && sync.PauseReason == SyncPauseReason.NeedsMergeChoice)
        {
            void Resolve(SyncMergeChoice c) { syncBusy = true; Later(0); _ = Task.Run(async () => { await sync.ResolveMergeAsync(c, CancellationToken.None); syncBusy = false; DispatcherQueue.TryEnqueue(() => Later(0)); }); }
            var merge = RowPrimary(Strings.Settings_Sync_MergeMerge, () => Resolve(SyncMergeChoice.Merge));
            var cloud = Button(Strings.Settings_Sync_MergeUseCloud, null, () => Resolve(SyncMergeChoice.UseCloud));
            var local = Button(Strings.Settings_Sync_MergeUseLocal, null, () => Resolve(SyncMergeChoice.UseLocal));
            foreach (var b in new[] { merge, cloud, local }) Enable(b, !syncBusy);
            rows.Add(Row(Strings.Settings_Sync_MergeTitle, Strings.Settings_Sync_MergeDescription, merge, cloud, local));
        }

        if (o.Enabled)
        {
            rows.Add(Row(Strings.Settings_Sync_Settings, Strings.Settings_Sync_SettingsDescription, Switch(o.Settings, v => Apply(x => x with { Settings = v }))));
            rows.Add(Row(Strings.Settings_Sync_Usage, Strings.Settings_Sync_UsageDescription, Switch(o.Usage, v => Apply(x => x with { Usage = v }))));
            rows.Add(Row(Strings.Settings_Sync_Clipboard, Strings.Settings_Sync_ClipboardDescription, Switch(o.Clipboard, v => { Apply(x => x with { Clipboard = v }); Later(); })));
            if (o.Clipboard)
            {
                rows.Add(Row(Strings.Settings_Sync_ClipScope, null,
                    Seg([(nameof(ClipSyncScope.Text), Strings.Settings_Sync_ClipScopeText), (nameof(ClipSyncScope.TextAndImages), Strings.Settings_Sync_ClipScopeImages)], o.ClipScope.ToString(),
                        v => Apply(x => x with { ClipScope = Enum.Parse<ClipSyncScope>(v) }))));
                var excluded = o.ExcludedApps ?? [];
                rows.Add(Row(Strings.Settings_Sync_Excluded, excluded.Count > 0 ? Strings.Settings_Sync_ExcludedDescription : Strings.Settings_Sync_ExcludedNone));
                foreach (var name in excluded)
                    rows.Add(ListRow("shield", name, Strings.Settings_Sync_ExcludedItem, false,
                        IconButton("close", Strings.Common_Remove, () => { Apply(x => x with { ExcludedApps = (x.ExcludedApps ?? []).Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)).ToArray() }); Later(); })));
                rows.Add(AddRow(Button(Strings.Settings_Sync_ExcludedAdd, "plus", async () =>
                {
                    // 同剪贴板的「不记录的程序」：只存程序文件名，不区分大小写
                    if (await PickProgram() is not { } exe) return;
                    var file = Path.GetFileName(exe);
                    Apply(x => x with { ExcludedApps = (x.ExcludedApps ?? []).Any(v => string.Equals(v, file, StringComparison.OrdinalIgnoreCase)) ? x.ExcludedApps : [.. x.ExcludedApps ?? [], file] });
                    Later();
                })));
                rows.Add(Row(Strings.Settings_Sync_WriteSystem, Strings.Settings_Sync_WriteSystemDescription, Switch(o.WriteToSystemClipboard, v => Apply(x => x with { WriteToSystemClipboard = v }))));
                rows.Add(Row(Strings.Settings_Sync_DeviceView, Strings.Settings_Sync_DeviceViewDescription,
                    Seg([(nameof(ClipDeviceView.Mixed), Strings.Settings_Sync_DeviceViewMixed), (nameof(ClipDeviceView.FilterOnly), Strings.Settings_Sync_DeviceViewFilter)], o.DeviceView.ToString(),
                        v => Apply(x => x with { DeviceView = Enum.Parse<ClipDeviceView>(v) }))));
            }
        }
        Group(Strings.Settings_Sync, rows.ToArray(), o.Enabled ? Strings.Settings_Sync_Note : null);
    }

    void OnSyncChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "data") Later(0); });

    FrameworkElement SyncNowButton(ISyncService sync)
    {
        var b = Button(Strings.Settings_Sync_Now, "refresh", () => _ = sync.SyncNowAsync(CancellationToken.None));
        Enable(b, sync.State != SyncState.Syncing);
        return b;
    }
}
