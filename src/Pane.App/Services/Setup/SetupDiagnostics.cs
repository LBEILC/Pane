namespace Pane.Services.Setup;

internal static class SetupDiagnostics
{
    public static bool Busy(Exception error) =>
        error is IOException or UnauthorizedAccessException &&
        (uint)error.HResult is 0x80070005 or 0x80070020 or 0x80070021;

    public static void Move(string source, string target, Action<string, string>? move = null, Action<int>? delay = null)
    {
        // 最多 6 次，共等待 2.5 秒；不存在、磁盘满等错误直接返回。
        for (int attempt = 0; ; attempt++)
        {
            try { (move ?? Directory.Move)(source, target); return; }
            catch (Exception error) when (Busy(error) && attempt < 5)
            { (delay ?? Thread.Sleep)(500); }
        }
    }

    public static void Write(string data, Exception error, string step, string source, string? target = null)
    {
        try
        {
            string logs = Path.Combine(data, "logs");
            SetupFiles.Check(logs);
            Directory.CreateDirectory(logs);
            string name = $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.txt";
            File.WriteAllText(Path.Combine(logs, name),
                $"UTC: {DateTime.UtcNow:O}\nStep: {step}\nSource: {source}\nTarget: {target}\nType: {error.GetType().FullName}\nHResult: 0x{error.HResult:X8}\n{error}\n");
            foreach (string file in Directory.GetFiles(logs, "setup-*.txt").OrderByDescending(Path.GetFileName).Skip(10))
                File.Delete(file);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        { /* 记录失败不能遮盖原始安装错误，也不能阻止回滚。 */ }
    }
}
