// 设置窗口：数据（设计负责人，2026-11-08，082）。规则见 docs/design.md「数据分区」。
// 三组：存储占用（统计在后台做，清理缓存、清空日志要点两次）、剪贴板图片上限、大文件位置（更改 / 恢复默认 / 重新使用，带进度）。
// 最后是导出、导入两组（083），在 SettingsView.DataTransfer.cs。
using Pane.Localization;
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.DesignData;
using Pane.Settings;

namespace Pane.Views;

public sealed partial class SettingsView
{
    // ================= 数据 =================

    /// <summary>存储服务。工程在启动时设（082）；没设时（演示模式）用假数据。</summary>
    static IDataStorage Data => DataStorage ?? DataStorageSample.Instance;

    StorageReport? dataReport;
    bool dataMeasuring;
    StorageCategory? dataArmed;      // 点了一次「清理 / 清空」，等第二次确认
    string? usageNote, largeNote;     // 组说明临时换成结果（清理释放了多少、移动到了哪里、失败原因）
    double? moveProgress;             // 正在移动大文件时的进度

    void RenderData()
    {
        if (dataReport is null && !dataMeasuring) _ = MeasureData();

        // 存储占用
        var usage = new List<FrameworkElement>
        {
            Row(Strings.Settings_Data_Total, null, Chip(dataReport is { } r ? SizeText(r.Total) : Strings.Settings_Data_Measuring)),
        };
        foreach (var category in new[] { StorageCategory.Settings, StorageCategory.Usage, StorageCategory.Clipboard, StorageCategory.Cache, StorageCategory.Logs })
        {
            var item = dataReport?.Items.FirstOrDefault(i => i.Category == category);
            var sub = item is null ? Strings.Settings_Data_Measuring : string.Format(Strings.Settings_Data_SizeFiles, SizeText(item.Bytes), item.Files);
            var controls = new List<FrameworkElement>();
            if (category is StorageCategory.Cache or StorageCategory.Logs)
            {
                bool armed = dataArmed == category;
                var text = category == StorageCategory.Cache
                    ? armed ? Strings.Settings_Data_ConfirmClean : Strings.Settings_Data_Clean
                    : armed ? Strings.Settings_Clipboard_ConfirmClear : Strings.Common_Clear;
                var clear = Button(text, null, () => _ = ClearData(category));
                Enable(clear, item is { Bytes: > 0 });
                controls.Add(clear);
            }
            if (item?.Folder is { } folder) controls.Add(IconButton("folder", Strings.Settings_Data_OpenFolder, () => OpenFolder(folder)));
            usage.Add(Row(CategoryName(category), sub, controls));
        }
        Group(Strings.Settings_Data_Usage, usage.ToArray(), usageNote ?? Strings.Settings_Data_UsageNote);

        // 剪贴板图片上限
        var limit = app.Store.Current.Clipboard.ImageLimitMegabytes;
        Group(null,
            Row(Strings.Settings_Data_ImageLimit, Strings.Settings_Data_ImageLimitDescription,
                Seg([("0", Strings.Settings_Data_Unlimited), ("500", "500 MB"), ("1024", "1 GB"), ("2048", "2 GB"), ("5120", "5 GB")], limit.ToString(),
                    v => SaveClipboard(x => x with { ImageLimitMegabytes = long.Parse(v) }))));

        // 大文件位置
        var location = Data.LargeFiles;
        var where = location.Unavailable ? string.Format(Strings.Settings_Data_Unavailable, (object?)location.Configured)
            : location.IsDefault ? string.Format(Strings.Settings_Data_DefaultLocation, (object?)location.Path)
            : location.Path;
        var buttons = new List<FrameworkElement>();
        if (moveProgress is { } p) buttons.Add(Chip(string.Format(Strings.Settings_Data_Moving, p)));
        else
        {
            if (location.Unavailable) buttons.Add(Button(Strings.Settings_Data_Reuse, "refresh", () => _ = MoveLargeFiles(reuse: true, null)));
            buttons.Add(Button(Strings.Common_Change, "folder", () => _ = PickLargeFiles()));
            if (!location.IsDefault || location.Unavailable) buttons.Add(Button(Strings.Settings_Data_RestoreDefault, null, () => _ = MoveLargeFiles(reuse: false, null)));
        }
        Group(Strings.Settings_Data_LargeFiles, [Row(Strings.Settings_Data_ClipboardImages, where, buttons)], largeNote ?? Strings.Settings_Data_LargeFilesNote);

        // 导出、导入（083）
        RenderDataTransfer();
    }

    static string CategoryName(StorageCategory c) => c switch
    {
        StorageCategory.Settings => Strings.Settings_Data_CategorySettings,
        StorageCategory.Usage => Strings.Settings_Data_CategoryUsage,
        StorageCategory.Clipboard => Strings.Settings_Data_CategoryClipboard,
        StorageCategory.Cache => Strings.Settings_Data_CategoryCache,
        _ => Strings.Settings_Data_CategoryLogs,
    };

    // 1024 进位，和服务的上限、空间提示一致
    static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    async Task MeasureData()
    {
        dataMeasuring = true;
        try { dataReport = await Data.MeasureAsync(CancellationToken.None); }
        catch (Exception) { dataReport = new([]); }   // 统计不到就显示 0，不弹错误
        finally { dataMeasuring = false; }
        if (current.Id == "data") Render();
    }

    async Task ClearData(StorageCategory category)
    {
        if (dataArmed != category) { dataArmed = category; Render(); _ = DisarmData(category); return; }
        dataArmed = null;
        try
        {
            long freed = await Data.ClearAsync(category, CancellationToken.None);
            usageNote = string.Format(Strings.Settings_Data_Cleaned, SizeText(freed));
        }
        catch (Exception e) { usageNote = App.FailureReason(e) ?? e.Message; }
        dataReport = null;
        Render();
        _ = ResetNote(() => usageNote = null);
    }

    async Task DisarmData(StorageCategory category)
    {
        await Task.Delay(3000);
        if (dataArmed != category) return;
        dataArmed = null;
        if (current.Id == "data") Render();
    }

    async Task PickLargeFiles()
    {
        if (await PickFavorite(true) is { } picked) await MoveLargeFiles(reuse: false, picked.Path);
    }

    // folder 为 null 且不是 reuse = 搬回默认位置
    async Task MoveLargeFiles(bool reuse, string? folder)
    {
        moveProgress = 0;
        largeNote = null;
        Render();
        var progress = new Progress<double>(v => { moveProgress = v; if (current.Id == "data") Later(0); });
        try
        {
            if (reuse) await Data.ReuseConfiguredAsync(progress, CancellationToken.None);
            else await Data.MoveLargeFilesAsync(folder, progress, CancellationToken.None);
            largeNote = string.Format(Strings.Settings_Data_Moved, (object?)Data.LargeFiles.Path);
        }
        catch (DataStorageException e) { largeNote = e.Message; }
        catch (Exception e) { largeNote = App.FailureReason(e) ?? e.Message; }
        moveProgress = null;
        dataReport = null;
        Render();
        _ = ResetNote(() => largeNote = null, 6000);
    }

    async Task ResetNote(Action clear, int ms = 4000)
    {
        await Task.Delay(ms);
        clear();
        if (current.Id == "data") Render();
    }

    static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    // 占用、位置有变化（别处清了缓存、启动时退回默认位置）：在数据页就重新统计
    void OnDataChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (current.Id == "data" && moveProgress is null) { dataReport = null; Later(0); } });
}
