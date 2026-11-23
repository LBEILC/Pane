using Pane.Localization;
// 对话框搜索框的宿主（设计负责人，2026-11-23，issue 81）。docs/design.md「对话框搜索」。
// 服务（IDialogSearch，107）报告前台文件对话框的位置和状态；这里算搜索框贴在哪、多宽、往哪边展开，用 GlassPanel.SetBounds 跟着对话框走。
// - 贴在对话框外的下边缘（间距 8，和对话框同宽、限在 420～756、水平居中）；下面放不下（不到搜索行 + 200）且上面更宽时贴到上边缘外，结果往上展开；
// - 不激活出现；点击或对话框在前台时按启动器热键才聚焦；失去焦点回到对话框不收起，切到别的程序收起；
// - 对话框不在前台、最小化、关闭时收起。设置：FolderJumpSettings.SearchBar / OpenOnEnter（FilterByType 由服务处理）。
using Pane.Contracts;
using Pane.Hosting;
using Pane.Views;

namespace Pane;

public sealed partial class App
{
    IDialogSearch? dialogBarService;
    DialogSearchView? dialogBarView;
    GlassPanel? dialogBarPanel;
    FileDialogInfo? dialogBarInfo;

    /// <summary>文件对话框能力装配好对话框搜索服务后调用；能力停用时传 null。</summary>
    internal void AttachDialogSearch(IDialogSearch? service)
    {
        if (ReferenceEquals(dialogBarService, service)) return;
        if (dialogBarService != null) dialogBarService.DialogChanged -= OnDialogBarChanged;
        dialogBarService = service;
        if (service != null) { service.DialogChanged += OnDialogBarChanged; if (service.Current is { } now) queue.TryEnqueue(() => OnDialogBar(now)); }
        else queue.TryEnqueue(() => { HideDialogBar(); if (dialogBarPanel != null) { panels.Remove("dialog-search"); dialogBarPanel.Dispose(); dialogBarPanel = null; dialogBarView = null; } });
    }

    void OnDialogBarChanged(object? sender, FileDialogInfo? info) => queue.TryEnqueue(() => OnDialogBar(info));

    void OnDialogBar(FileDialogInfo? info)
    {
        if (closing) return;
        var f = Store.Current.FolderJump;
        dialogBarInfo = info;
        if (info is null || !f.Enabled || !f.SearchBar || info.Minimized) { HideDialogBar(); return; }
        if (!info.Foreground)
        {
            // 点进搜索框时前台短暂是 Pane 自己：照常跟着对话框，不收起
            if (dialogBarPanel is { Shown: true } p && Native.GetForegroundWindow() is var fg && (fg == p.Input || fg == p.Visual)) { PlaceDialogBar(info); return; }
            HideDialogBar();
            return;
        }
        EnsureDialogBar();
        PlaceDialogBar(info);
    }

    void EnsureDialogBar()
    {
        if (dialogBarPanel != null) return;
        var view = dialogBarView = new DialogSearchView();
        view.Entries = () => folderJump?.Entries() ?? (demo ? DemoFolders : []);
        view.Search = (q, ext, t) => dialogBarService?.SearchAsync(q, ext, 12, t) ?? Task.FromResult<IReadOnlyList<FileHit>>([]);
        view.LayoutChanged += (_, _) => { if (dialogBarInfo is { } i) PlaceDialogBar(i); };
        view.ReturnFocus += (_, _) => { if (dialogBarInfo is { } i) Native.SetForegroundWindow(i.Dialog); };
        view.Navigate += async (_, x) =>
        {
            if (dialogBarService is not { } service || dialogBarInfo is not { } i) return;
            try { await service.NavigateAsync(i.Dialog, x.Folder, x.FileName, x.Confirm, CancellationToken.None); }
            catch (Exception e) { ReportFailure(Strings.FolderJump_JumpAction, e); }
            // 保险（第三十一次发布核对时，新式「另存为」跳完后对话框没回到前台）：这时前台是 Pane 的搜索框，系统允许 Pane 把前台交还给对话框
            if (!x.Confirm && Native.GetForegroundWindow() != i.Dialog) Native.SetForegroundWindow(i.Dialog);
        };
        dialogBarPanel = new GlassPanel(Strings.DialogSearch_Title, view, view, Glass) { Placement = PanelPlacement.Exact, EscapeHides = false };
        // 焦点回到对话框不收起；切到别的程序才收起
        dialogBarPanel.HideOnBlur = () => dialogBarInfo is not { } i || Native.GetForegroundWindow() != i.Dialog;
        WatchFrames(dialogBarPanel);
        dialogBarPanel.Prepare();
        panels["dialog-search"] = dialogBarPanel;
    }

    void PlaceDialogBar(FileDialogInfo info)
    {
        if (dialogBarPanel is not { } panel || dialogBarView is not { } view) return;
        double dpi = GetDpiForWindow(info.Dialog) is var d and > 0 ? d / 96.0 : 1;
        int dlgW = info.Right - info.Left;
        double width = Math.Clamp(dlgW / dpi, 420, 756);
        var work = WorkAreaOf(info.Dialog);
        int gap = (int)Math.Round(8 * dpi), edge = (int)Math.Round(8 * dpi);
        double below = (work.Bottom - info.Bottom - gap - edge) / dpi, above = (info.Top - work.Top - gap - edge) / dpi;
        bool up = below < DialogSearchView.CollapsedHeight + 200 && above > below;
        view.OpenOnEnter = Store.Current.FolderJump.OpenOnEnter;
        view.Room = up ? above : below;
        view.Apply(info, up);
        double height = view.RequestedHeight;
        int x = info.Left + (int)Math.Round((dlgW - width * dpi) / 2);
        int y = up ? info.Top - gap - (int)Math.Round(height * dpi) : info.Bottom + gap;
        if (!panel.Shown) panel.Show(true, x, y, activate: false);
        panel.SetBounds(x, y, width, height);
    }

    void HideDialogBar()
    {
        if (dialogBarPanel is { Shown: true } p) p.Hide();
    }

    /// <summary>对话框在前台、搜索框开着时，启动器热键改为聚焦搜索框。处理了返回 true。</summary>
    bool FocusDialogBar()
    {
        if (dialogBarPanel is not { Shown: true } panel || dialogBarView is not { } view || dialogBarInfo is not { } info) return false;
        if (Native.GetForegroundWindow() != info.Dialog) return false;
        panel.Refocus();
        view.FocusSearch();
        return true;
    }

    // ---- 设计预览：PANE_DESIGN_OPEN=dialog（演示模式没有真实对话框：在屏幕上画一个假的对话框位置，看搜索框） ----
    void ShowDialogBarDemo()
    {
        var sample = new Pane.DesignData.DialogSearchSample(Environment.GetEnvironmentVariable("PANE_DESIGN_DIALOG") == "save");
        AttachDialogSearch(sample);
        EnsureDialogBar();
        OnDialogBar(sample.Current);
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_QUERY") is { } q) queue.TryEnqueue(() => { dialogBarView?.FocusSearch(); if (dialogBarView != null) dialogBarView.SetQuery(q); });
    }

    static readonly FolderJumpEntry[] DemoFolders =
    [
        new(@"D:\设计\海报", "海报", FolderJumpSource.Explorer), new(@"C:\Users\你\Downloads", "Downloads", FolderJumpSource.Explorer),   // 不翻译：演示数据
        new(@"D:\照片\2026-10 旅行", "2026-10 旅行", FolderJumpSource.Recent), new(@"D:\设计\图标", "图标", FolderJumpSource.Recent),
    ];

    static Native.Rect WorkAreaOf(nint window)
    {
        var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfoW(MonitorFromWindow(window, 2), ref info) ? info.Work : new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MonitorInfoEx { public int Size; public Native.Rect Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
}
