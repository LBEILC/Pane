// 设置窗口：数据 → 导出、导入（设计负责人，2026-11-08，083）。规则见 docs/design.md「数据分区」第 4 组。
// 导出：选内容（设置、使用记录开关，剪贴板 不包含 / 仅文字 / 含图片）→ 选保存位置 → 进度 → 结果写在行说明里。
// 导入：选 zip → 读出里面有什么（版本、导出时间、各类是否包含、剪贴板条数）→ 选导入哪几类和合并 / 覆盖
//      → 「导入并重启」：服务放进待导入区，重启后在读数据之前备份并应用；重启后自动打开这一页，顶部显示上次导入的结果。
// 演示模式不重启，直接显示结果。设计预览：PANE_DESIGN_IMPORT=preview 打开一份导入预览，=done / failed 显示上次导入的结果。
using Pane.Localization;
using Microsoft.UI.Xaml;
using Pane.Contracts;
using Pane.DesignData;

namespace Pane.Views;

public sealed partial class SettingsView
{
    static IDataTransfer? Transfer => Data as IDataTransfer;

    // 导出选项只在本次打开期间记住，不进设置
    bool exportSettings = true, exportUsage = true;
    ClipboardExport exportClipboard = ClipboardExport.TextOnly;
    double? exportProgress;
    string? exportNote, exportedPath;   // 导出结果或失败原因；成功时记下文件位置，给「打开文件夹」用

    string? importPath;
    DataImportPreview? importInfo;
    bool importReading, importSettings, importUsage, importClipboard;
    DataImportMode importMode = DataImportMode.Merge;
    double? stageProgress;
    string? importNote;                 // 读取或准备失败的原因，临时换掉组说明

    void RenderDataTransfer()
    {
        if (Transfer is not { } transfer) return;
        if (importInfo is null && importPath is null && DataStorage is null && Environment.GetEnvironmentVariable("PANE_DESIGN_IMPORT") == "preview")
            OpenImportPreview(@"D:\备份\Pane 数据 2026-11-08.zip", DataStorageSample.Preview); // 不翻译：演示数据

        // 导出
        bool anything = exportSettings || exportUsage || exportClipboard != ClipboardExport.None;
        FrameworkElement exportAction;
        if (exportProgress is { } p) exportAction = Chip(string.Format(Strings.Settings_Data_Exporting, p));
        else
        {
            var button = Button(Strings.Settings_Data_Export, "download", () => _ = ExportData());
            Enable(button, anything);
            exportAction = button;
        }
        var exportTail = new List<FrameworkElement>();
        if (exportProgress is null && exportedPath is { } done) exportTail.Add(IconButton("folder", Strings.Settings_Data_OpenFolder, () => OpenFolder(Path.GetDirectoryName(done)!)));
        exportTail.Add(exportAction);
        Group(Strings.Settings_Data_Export,
        [
            Row(Strings.Settings_Data_CategorySettings, Strings.Settings_Data_ExportSettingsDescription, Switch(exportSettings, v => { exportSettings = v; Render(); })),
            Row(Strings.Settings_Data_CategoryUsage, Strings.Settings_Data_UsageDescription, Switch(exportUsage, v => { exportUsage = v; Render(); })),
            Row(Strings.Settings_Data_CategoryClipboard, Strings.Settings_Data_ClipboardExportDescription,
                Seg([(nameof(ClipboardExport.None), Strings.Settings_Data_ClipboardNone), (nameof(ClipboardExport.TextOnly), Strings.Settings_Data_ClipboardTextOnly), (nameof(ClipboardExport.WithImages), Strings.Settings_Data_ClipboardWithImages)],
                    exportClipboard.ToString(), v => { exportClipboard = Enum.Parse<ClipboardExport>(v); Render(); })),
            Row(Strings.Settings_Data_ExportToFile, exportNote ?? Strings.Settings_Data_ExportToFileDescription, exportTail),
        ], Strings.Settings_Data_ExportNote);

        // 导入
        var rows = new List<FrameworkElement>();
        DataImportOutcome? last = null;
        try { last = transfer.LastImport; } catch (Exception) { }   // 结果文件读不出就当没有
        if (last is { } outcome)
            rows.Add(Row(Strings.Settings_Data_LastImport, outcome.Error ?? string.Format(Strings.Settings_Data_ImportDone, (object?)outcome.Backup),
                IconButton("folder", Strings.Settings_Data_OpenFolder, () => OpenFolder(Path.GetDirectoryName(outcome.Backup)!)),
                IconButton("close", Strings.Common_Close, () => { try { transfer.AcknowledgeImport(); } catch (Exception) { } Render(); })));

        if (importInfo is { } info && importPath is { } path)
        {
            bool busy = stageProgress is not null;
            rows.Add(Row(Path.GetFileName(path), string.Format(Strings.Settings_Data_ImportSource, info.AppVersion, info.ExportedAt.ToLocalTime().ToString("g")),
                Disabled(Button(Strings.Common_Cancel, null, CloseImportPreview), busy)));
            rows.Add(ImportPart(Strings.Settings_Data_CategorySettings, Strings.Settings_Data_ImportSettingsDescription, info.HasSettings, importSettings, v => importSettings = v, busy));
            rows.Add(ImportPart(Strings.Settings_Data_CategoryUsage, Strings.Settings_Data_UsageDescription, info.HasUsage, importUsage, v => importUsage = v, busy));
            rows.Add(ImportPart(Strings.Settings_Data_CategoryClipboard, string.Format(Strings.Settings_Data_ImportClipboardCount, info.ClipboardEntries, info.ClipboardImages),
                info.ClipboardEntries > 0, importClipboard, v => importClipboard = v, busy));
            // 合并 / 覆盖只影响使用记录和剪贴板；两者都没选时变灰
            var mode = Seg([(nameof(DataImportMode.Merge), Strings.Settings_Data_Merge), (nameof(DataImportMode.Replace), Strings.Settings_Data_Replace)], importMode.ToString(),
                v => { importMode = Enum.Parse<DataImportMode>(v); Render(); });
            Enable(mode, !busy && (importUsage || importClipboard));
            rows.Add(Row(Strings.Settings_Data_ImportMode, importMode == DataImportMode.Merge ? Strings.Settings_Data_MergeDescription : Strings.Settings_Data_ReplaceDescription, mode));
            FrameworkElement start = busy ? Chip(string.Format(Strings.Settings_Data_Preparing, stageProgress))
                : Disabled(Button(Strings.Settings_Data_ImportRestart, "restart", () => _ = StartImport()), !(importSettings || importUsage || importClipboard));
            rows.Add(Row(Strings.Settings_Data_ImportStart, Strings.Settings_Data_ImportStartDescription, start));
        }
        else
        {
            FrameworkElement pick = importReading ? Chip(Strings.Settings_Data_Reading) : Button(Strings.Common_ChooseFile, "folder", () => _ = PickImport());
            rows.Add(Row(Strings.Settings_Data_ImportFromFile, Strings.Settings_Data_ImportFromFileDescription, pick));
        }
        Group(Strings.Settings_Data_Import, rows.ToArray(), importNote ?? Strings.Settings_Data_ImportNote);
    }

    // 预览里的一类：文件里没有就关掉、变灰，说明换成「文件中不包含此项」
    FrameworkElement ImportPart(string label, string desc, bool present, bool value, Action<bool> set, bool busy)
    {
        var toggle = Switch(present && value, v => { set(v); Render(); });
        Enable(toggle, present && !busy);
        return Row(label, present ? desc : Strings.Settings_Data_ImportAbsent, toggle);
    }

    static T Disabled<T>(T c, bool off) where T : Microsoft.UI.Xaml.Controls.Control { if (off) Enable(c, false); return c; }

    nint SettingsWindow => Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);

    async Task ExportData()
    {
        string path;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = string.Format(Strings.Settings_Data_ExportFileName, DateTime.Now.ToString("yyyy-MM-dd")) };
            picker.FileTypeChoices.Add(Strings.Settings_Data_ZipFileType, [".zip"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, SettingsWindow);
            if (await picker.PickSaveFileAsync() is not { } file) return;
            path = file.Path;
        }
        catch (Exception e) { exportNote = string.Format(Strings.Data_ExportFailed, App.FailureReason(e) ?? e.Message); exportedPath = null; Render(); return; }

        exportProgress = 0;
        exportNote = exportedPath = null;
        Render();
        var progress = new Progress<double>(v => { exportProgress = v; if (current.Id == "data") Later(0); });
        try
        {
            var result = await Transfer!.ExportAsync(path, new(exportSettings, exportUsage, exportClipboard), progress, CancellationToken.None);
            exportNote = string.Format(Strings.Settings_Data_Exported, result.Path, SizeText(result.Bytes));
            exportedPath = result.Path;
        }
        catch (Exception e)
        {
            exportNote = e is DataStorageException ? e.Message : string.Format(Strings.Data_ExportFailed, App.FailureReason(e) ?? e.Message);
            // 保存对话框可能先建了一个空文件；导出失败时把它删掉
            try { if (new FileInfo(path) is { Exists: true, Length: 0 } empty) empty.Delete(); } catch (Exception) { }
        }
        exportProgress = null;
        dataReport = null;
        Render();
    }

    async Task PickImport()
    {
        string path;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, SettingsWindow);
            if (await picker.PickSingleFileAsync() is not { } file) return;
            path = file.Path;
        }
        catch (Exception e) { importNote = App.FailureReason(e) ?? e.Message; Render(); return; }

        importReading = true;
        importNote = null;
        Render();
        try { OpenImportPreview(path, await Transfer!.InspectAsync(path, CancellationToken.None)); }
        catch (Exception e) { importNote = e is DataStorageException ? e.Message : App.FailureReason(e) ?? e.Message; }
        importReading = false;
        Render();
    }

    // 默认勾上文件里有的每一类，方式默认合并
    void OpenImportPreview(string path, DataImportPreview info)
    {
        importPath = path;
        importInfo = info;
        importSettings = info.HasSettings;
        importUsage = info.HasUsage;
        importClipboard = info.ClipboardEntries > 0;
        importMode = DataImportMode.Merge;
    }

    void CloseImportPreview()
    {
        importPath = null;
        importInfo = null;
        Render();
    }

    async Task StartImport()
    {
        if (importPath is not { } path) return;
        stageProgress = 0;
        importNote = null;
        Render();
        var progress = new Progress<double>(v => { stageProgress = v; if (current.Id == "data") Later(0); });
        try { await Transfer!.StageImportAsync(path, new(importSettings, importUsage, importClipboard, importMode), progress, CancellationToken.None); }
        catch (Exception e)
        {
            importNote = e is DataStorageException ? e.Message : App.FailureReason(e) ?? e.Message;
            stageProgress = null;
            Render();
            return;
        }
        stageProgress = null;
        importPath = null;
        importInfo = null;
        if (DataStorage is null) { Render(); return; }   // 演示模式不重启
        app.Restart();
    }
}
