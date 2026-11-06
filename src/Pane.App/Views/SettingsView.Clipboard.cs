// 设置窗口：剪贴板（设计负责人）。从 SettingsView.xaml.cs 按分区拆出（2026-11-06），外壳和公共状态在主文件。
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
    // ================= 剪贴板 =================

    void RenderClipboard()
    {
        var s = app.Store.Current;
        var c = s.Clipboard;
        // 总开关（033）挪到「能力」页（055）；停用时宿主先把 Win+V 还给系统
        var conflict = app.HotkeyConflicts.FirstOrDefault(x => x.Target == "clipboard");
        var hotkey = Hotkey(c.Hotkey, s.Launcher.Hotkey, h => SaveClipboard(x => x with { Hotkey = h }), doubleTap: true);
        var winv = app.WinV;
        var call = new List<FrameworkElement>
        {
            conflict != null ? Row(Strings.Settings_Hotkey, Strings.Settings_Hotkey_ConflictDescription, Status(false, ConflictText(conflict)), hotkey)
                             : Row(Strings.Settings_Hotkey, DevHotkey("clipboard") ?? (winv?.State == WinVState.On ? Strings.Settings_Clipboard_WinVActive : c.Hotkey.DoubleTap ? Strings.Settings_Hotkey_DoubleTapHint : null), hotkey),
        };
        if (winv != null) call.AddRange(WinVRows(winv));
        Group(Strings.Settings_Clipboard_Activation, call.ToArray());

        // 三种类型至少留一种（设置模型要求）：只剩一种时点它不响应，说明里写清楚
        int kinds = (c.RecordText ? 1 : 0) + (c.RecordImages ? 1 : 0) + (c.RecordFiles ? 1 : 0);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ToggleChip(Strings.Common_Text, "text", c.RecordText, kinds == 1, v => SaveClipboard(x => x with { RecordText = v })));
        chips.Children.Add(ToggleChip(Strings.Common_Image, "image", c.RecordImages, kinds == 1, v => SaveClipboard(x => x with { RecordImages = v })));
        chips.Children.Add(ToggleChip(Strings.Common_File, "folder", c.RecordFiles, kinds == 1, v => SaveClipboard(x => x with { RecordFiles = v })));
        Group(Strings.Settings_Clipboard_Recording,
            Row(Strings.Settings_Clipboard_Formats, kinds == 1 ? Strings.Settings_Clipboard_OneFormatRequired : Strings.Settings_Clipboard_FormatsDescription, chips),
            Row(Strings.Settings_Clipboard_MaxItems, Strings.Settings_Clipboard_MaxItemsDescription,
                Seg([("200", Strings.Settings_Clipboard_LimitTwoHundred), ("500", Strings.Settings_Clipboard_LimitFiveHundred), ("1000", Strings.Settings_Clipboard_LimitThousand), ("5000", Strings.Settings_Clipboard_LimitFiveThousand)], c.Limit.ToString(),
                    v => SaveClipboard(x => x with { Limit = int.Parse(v) }))),
            Row(Strings.Settings_Clipboard_Deduplicate, Strings.Settings_Clipboard_DeduplicateDescription, Switch(c.Dedupe, v => SaveClipboard(x => x with { Dedupe = v }))),
            Row(Strings.Settings_Clipboard_MovePastedFirst, Strings.Settings_Clipboard_MovePastedFirstDescription, Switch(c.MoveToTopAfterPaste, v => SaveClipboard(x => x with { MoveToTopAfterPaste = v }))));

        var excluded = c.ExcludedPrograms.Select(name => ListRow("shield", name, Strings.Settings_Clipboard_ExcludedProgram, false,
            IconButton("close", Strings.Common_Remove, () => { SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Where(v => !string.Equals(v, name, StringComparison.OrdinalIgnoreCase)).ToArray() }); Render(); }))).ToList();
        excluded.Insert(0, Row(Strings.Settings_Clipboard_RespectHistoryFlag, Strings.Settings_Clipboard_RespectHistoryFlagDescription, Switch(c.HonorExcludeMarker, v => SaveClipboard(x => x with { HonorExcludeMarker = v }))));
        excluded.Add(AddRow(Button(Strings.Settings_Clipboard_AddExcludedProgram, "plus", async () =>
        {
            // 服务按程序文件名匹配（不区分大小写），只存文件名
            if (await PickProgram() is not { } exe) return;
            var name = Path.GetFileName(exe);
            SaveClipboard(x => x with { ExcludedPrograms = x.ExcludedPrograms.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ? x.ExcludedPrograms : [.. x.ExcludedPrograms, name] });
            Render();
        })));
        Group(Strings.Settings_Clipboard_Privacy, excluded.ToArray());
        RenderClipboardData();
    }

    void RenderClipboardData()
    {
        // 清空要点两次：第一次按钮变成「再点一次清空」，3 秒内不点就复原（同启动器里关机 / 重启的确认）
        var clear = Button(clearArmed ? Strings.Settings_Clipboard_ConfirmClear : Strings.Common_Clear, "trash", () =>
        {
            if (!clearArmed) { clearArmed = true; Render(); _ = DisarmClear(); return; }
            clearArmed = false;
            app.ClipboardHistory.ClearUnpinned();
            clearedAt = DateTime.Now;
            Render();
        });
        var cleared = (DateTime.Now - clearedAt).TotalSeconds < 4;
        var rows = new List<FrameworkElement>
        {
            cleared ? Row(Strings.Settings_Clipboard_ClearHistory, null, Status(true, Strings.Settings_Clipboard_Cleared), clear)
                    : Row(Strings.Settings_Clipboard_ClearHistory, clearArmed ? Strings.Settings_Clipboard_ClearWarning : Strings.Settings_Clipboard_PinnedRemain, clear),
        };
        // 历史存不进硬盘时（045，用户 2026-11-01 遇到：重启后历史退回早上）在这里说清楚，不要等重启丢了才发现
        if (app.ClipboardHistory.StorageProblem is { } problem)
            rows.Insert(0, Row(Strings.Settings_Clipboard_StorageFailed, Strings.Settings_Clipboard_StorageFailedDescription, Status(false, problem)));
        Group(Strings.Settings_Clipboard_Data, rows.ToArray());
    }

    // 用 Pane 替换 Win+V（2026-10-30，原型 settings.js winvDialog）：打开 / 关闭都先就地说明要改什么、要不要现在重启资源管理器，
    // 再点按钮才动手。下面跟一行「同时关闭 Windows 自带的剪贴板历史」，只在已替换时可用
    bool winvConfirm, winvBusy;
    IEnumerable<FrameworkElement> WinVRows(IWinVReplacement w)
    {
        var state = w.State;
        bool replaced = state is WinVState.On or WinVState.Pending;
        void Run(Func<CancellationToken, Task> work)
        {
            winvConfirm = false; winvBusy = true; Render();
            _ = Go();
            async Task Go()
            {
                try { await work(CancellationToken.None); } catch (Exception) { }   // 失败原因由服务放进 Error
                winvBusy = false; Render();
            }
        }
        var label = Strings.Settings_WinV_Replace;
        if (winvBusy)
        {
            yield return Row(label, null, Status(true, Strings.Settings_WinV_Working));
            yield break;
        }
        if (winvConfirm)
        {
            // 说明一行占满宽度，问题和按钮另起一行（挤在一行时说明被压成很窄的一列）
            yield return Row(replaced ? Strings.Settings_WinV_Restore : label, replaced
                ? Strings.Settings_WinV_RestoreDescription
                : Strings.Settings_WinV_RegistryDescription);
            yield return Row(Strings.Settings_WinV_RestartPrompt, Strings.Settings_WinV_RestartDescription,
                Button(Strings.Common_Cancel, null, () => { winvConfirm = false; Render(); }),
                Button(Strings.Settings_WinV_NextLogin, null, () => Run(t => replaced ? w.DisableAsync(false, t) : w.EnableAsync(false, t))),
                Button(Strings.Settings_WinV_RestartNow, "refresh", () => Run(t => replaced ? w.DisableAsync(true, t) : w.EnableAsync(true, t))));
            yield break;
        }
        // 关闭 Pending（资源管理器还没放开 Win+V）不用重启，直接恢复；其余先就地确认
        var toggle = Switch(replaced, _ => { if (state == WinVState.Pending) Run(t => w.DisableAsync(false, t)); else { winvConfirm = true; Render(); } });
        FrameworkElement? status = w.Error is { } err ? Status(false, err) : state switch
        {
            WinVState.On => Status(true, Strings.Settings_WinV_Active),
            WinVState.Pending => Status(false, Strings.Settings_WinV_PendingEnable),
            WinVState.RestorePending => Status(false, Strings.Settings_WinV_PendingRestore),
            _ => null,
        };
        var now = state is WinVState.Pending or WinVState.RestorePending
            ? Button(Strings.Settings_WinV_RestartExplorer, "refresh", () => Run(t => state == WinVState.Pending ? w.EnableAsync(true, t) : w.DisableAsync(true, t)))
            : null;
        yield return Row(label, Strings.Settings_WinV_ReplaceDescription,
            new[] { status, now, toggle }.OfType<FrameworkElement>());
        var history = Switch(w.SystemHistoryDisabled, v => Run(t => w.SetSystemHistoryDisabledAsync(v, t)));
        Enable(history, replaced);
        yield return Row(Strings.Settings_WinV_DisableSystemHistory, replaced ? Strings.Settings_WinV_DisableSystemHistoryDescription : Strings.Settings_WinV_EnableFirst, history);
    }

    void OnWinVChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "clipboard" && !winvBusy) Later(0); });

    bool clearArmed;
    DateTime clearedAt;

    async Task DisarmClear()
    {
        await Task.Delay(3000);
        if (!clearArmed) return;
        clearArmed = false;
        if (current.Id == "clipboard") Render();
    }

    // 可多选的小标签（原型 .chipc）：选中是选中底色 + 主题色描边。onlyOne：至少要留一个时，最后一个点了不响应
    Border ToggleChip(string text, string? glyph, bool on, bool onlyOne, Action<bool> changed)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null) content.Children.Add(new PaneIcon { Glyph = glyph, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = (on ? SmallStrongProto : SmallProto).Foreground });
        content.Children.Add(Text(text, on ? SmallStrongProto : SmallProto));
        var chip = new Border
        {
            Height = PaneTokens.ControlSm, CornerRadius = new CornerRadius(PaneTokens.ControlSm / 2), Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(1),
            Background = on ? SelectedProto.Background : FillProto.Background, BorderBrush = on ? Accent() : FillProto.BorderBrush,
            Opacity = on ? 1 : 0.7, Child = content,
        };
        chip.Tapped += (_, _) =>
        {
            if (on && onlyOne) return;
            changed(!on);
            Render();
        };
        return chip;
    }

    void SaveClipboard(Func<ClipboardSettings, ClipboardSettings> change) =>
        app.Store.Save(app.Store.Current with { Clipboard = change(app.Store.Current.Clipboard) });
}
