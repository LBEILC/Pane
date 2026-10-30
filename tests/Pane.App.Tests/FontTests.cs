using Pane.Services;
using System.Diagnostics;
static class FontTests
{
    public static async Task Run()
    {
        using var process = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh(); long privateBefore = process.PrivateMemorySize64, managedBefore = GC.GetTotalMemory(true);
        var catalog = new FontCatalog();
        var watch = Stopwatch.StartNew();
        var fonts = await catalog.GetFamiliesAsync(default); double first = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var cached = await catalog.GetFamiliesAsync(default); double second = watch.Elapsed.TotalMilliseconds;
        if (!fonts.Any(f => f.Name == "Microsoft YaHei UI" && f.HasChinese) || !fonts.Any(f => f.Name == "Segoe UI" && !f.HasChinese) || fonts.Any(f => f.Name.Contains("Wingdings"))) throw new Exception("字体覆盖验收失败: "+string.Join(";",fonts.Where(f=>f.Name.Contains("YaHei")||f.Name.Contains("Segoe UI")||f.Name.Contains("Wingdings"))));
        if (!ReferenceEquals(fonts, cached)) throw new Exception("字体缓存未复用");
        if (fonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fonts.Count || !fonts.SequenceEqual(fonts.OrderBy(f => f.DisplayName, StringComparer.CurrentCulture))) throw new Exception("字体排序去重失败");
        Console.WriteLine($"PASS fonts assertions=5; families={fonts.Count}; chinese={fonts.Count(f => f.HasChinese)}; firstMs={first:F3}; cachedMs={second:F3}");
        catalog.ClearCache();
        fonts = cached = null!;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        Console.WriteLine($"Font harness memory: privateBeforeMiB={privateBefore / 1048576.0:F3}; privateAfterMiB={process.PrivateMemorySize64 / 1048576.0:F3}; managedDeltaBytes={GC.GetTotalMemory(true) - managedBefore}");
    }
}
