// 「数据」分区的假数据（设计负责人，082）。真实服务没接上时（演示模式，SettingsView.DataStorage 为空）设置页用它。
// 设计预览：PANE_DESIGN_DATA=unavailable 显示「用户选的位置不可用」，=moved 显示已改到别的盘。
using Pane.Contracts;

namespace Pane.DesignData;

// 导入导出（083）：PANE_DESIGN_IMPORT=done / failed 显示上次导入的结果；=preview 设置页直接打开一份导入预览。
public sealed class DataStorageSample : IDataStorage, IDataTransfer
{
    public static readonly DataStorageSample Instance = new();

    const string Default = @"C:\Users\你\AppData\Roaming\Pane\clipboard"; // 不翻译：演示数据
    const string Other = @"D:\Pane 数据\Pane"; // 不翻译：演示数据

    LargeFilesLocation location = Environment.GetEnvironmentVariable("PANE_DESIGN_DATA") switch
    {
        "unavailable" => new(Default, true, Other, true),
        "moved" => new(Other, false, Other, false),
        _ => new(Default, true, null, false),
    };
    long cache = 186L << 20, logs = 3L << 20;

    public async Task<StorageReport> MeasureAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);
        return new([
            new(StorageCategory.Settings, 46_080, 2, @"C:\Users\你\AppData\Roaming\Pane"), // 不翻译：演示数据
            new(StorageCategory.Usage, 21_504, 2, @"C:\Users\你\AppData\Roaming\Pane"), // 不翻译：演示数据
            new(StorageCategory.Clipboard, 1_342_177_280, 1_284, Default),
            new(StorageCategory.Cache, cache, cache > 0 ? 932 : 0, @"C:\Users\你\AppData\Roaming\Pane"), // 不翻译：演示数据
            new(StorageCategory.Logs, logs, logs > 0 ? 6 : 0, @"C:\Users\你\AppData\Roaming\Pane\logs"), // 不翻译：演示数据
        ]);
    }

    public async Task<long> ClearAsync(StorageCategory category, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        long freed;
        if (category == StorageCategory.Cache) { freed = cache; cache = 0; }
        else if (category == StorageCategory.Logs) { freed = logs; logs = 0; }
        else throw new ArgumentException(null, nameof(category));
        Changed?.Invoke(this, EventArgs.Empty);
        return freed;
    }

    public LargeFilesLocation LargeFiles => location;

    public async Task MoveLargeFilesAsync(string? folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (int i = 1; i <= 20; i++) { await Task.Delay(60, cancellationToken); progress?.Report(i / 20.0); }
        location = folder is null ? new(Default, true, null, false) : new(Path.Combine(folder, "Pane"), false, Path.Combine(folder, "Pane"), false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReuseConfiguredAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (location.Configured is not { } configured) return;
        await MoveLargeFilesAsync(Path.GetDirectoryName(configured), progress, cancellationToken);
    }

    public event EventHandler? Changed;

    // ---------------- 导入导出（083）----------------

    const string Backup = @"C:\Users\你\AppData\Roaming\Pane\backups\before-import-20261108-080000.zip"; // 不翻译：演示数据

    DataImportOutcome? lastImport = Environment.GetEnvironmentVariable("PANE_DESIGN_IMPORT") switch
    {
        "done" => new(Backup, null),
        "failed" => new(Backup, "导入失败，数据已恢复原状：IOException (0x80070020)"), // 不翻译：演示数据
        _ => null,
    };

    public static readonly DataImportPreview Preview = new("0.1.443", new DateTimeOffset(2026, 11, 8, 8, 30, 0, TimeSpan.Zero), true, true, 1284, 96);

    public async Task<DataExportResult> ExportAsync(string path, DataExportOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (int i = 1; i <= 20; i++) { await Task.Delay(50, cancellationToken); progress?.Report(i / 20.0); }
        return new(path, options.Clipboard == ClipboardExport.WithImages ? 412L << 20 : 2L << 20);
    }

    public async Task<DataImportPreview> InspectAsync(string path, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        return Preview;
    }

    public async Task StageImportAsync(string path, DataImportOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (int i = 1; i <= 20; i++) { await Task.Delay(50, cancellationToken); progress?.Report(i / 20.0); }
        lastImport = new(Backup, null);   // 演示模式不重启，直接显示结果
    }

    public DataImportOutcome? LastImport => lastImport;

    public void AcknowledgeImport() => lastImport = null;
}
