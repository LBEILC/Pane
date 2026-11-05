// 测试在 bin/…/results 下建的随机目录（模拟数据库、浏览器配置等）：
// 用 NewDirectory 取路径并登记，全部通过后调用 Pass 删掉本次登记的；失败时不删，留作现场。
// 首次使用时清掉 7 天没动过的残留（多半是旧的失败现场）。固定名字的目录每次覆盖，不在这里管。
static class TestResults
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "results");
    static readonly List<string> Created = [];

    static TestResults()
    {
        if (!Directory.Exists(Root)) return;
        DateTime stale = DateTime.UtcNow.AddDays(-7);
        foreach (var dir in new DirectoryInfo(Root).GetDirectories())
            if (dir.Name.Length >= 32 && dir.LastWriteTimeUtc < stale && Guid.TryParseExact(dir.Name[^32..], "N", out _)) Delete(dir.FullName);
    }

    public static string NewDirectory(string prefix = "")
    {
        string path = Path.Combine(Root, prefix + Guid.NewGuid().ToString("N"));
        lock (Created) Created.Add(path);
        return path;
    }

    public static void Pass()
    {
        string[] paths;
        lock (Created) { paths = [.. Created]; Created.Clear(); }
        foreach (string path in paths) Delete(path);
    }

    // Directory.Delete 遇到链接只删链接本身；浏览器子进程退出慢会占着目录，稍等重试，仍删不掉只提示。
    static void Delete(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9) { Console.WriteLine($"结果目录没删掉：{path}：{e.Message}"); return; }
                Thread.Sleep(300);
            }
        }
    }
}
