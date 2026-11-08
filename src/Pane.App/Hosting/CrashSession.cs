using System.Globalization;

namespace Pane.Hosting;

// 内部诊断格式不依赖 JSON 反射，JIT 和 AOT 使用相同的精确 FILETIME。
internal sealed record CrashSession(int ProcessId, long CreationTime, string Executable, string Version, string Runtime)
{
    public string Identity => $"{ProcessId}:{CreationTime}";
    public string Serialize() => $"{ProcessId}\n{CreationTime}\n{Executable}\n{Version}\n{Runtime}\n";
    public static CrashSession? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length != 6 || lines[5] != "" ||
            !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out long time) || time <= 0 ||
            lines[2] != Path.GetFileName(lines[2]) || string.IsNullOrWhiteSpace(lines[2]) ||
            lines[4] is not ("JIT" or "AOT")) return null;
        try { _ = DateTime.FromFileTimeUtc(time); } catch (ArgumentOutOfRangeException) { return null; }
        return new(pid, time, lines[2], lines[3], lines[4]);
    }
}

internal sealed class CrashSessionStore(string dataDirectory)
{
    public string MarkerPath => Path.Combine(dataDirectory, "logs", "running-session");
    public CrashSession? TakePrevious()
    {
        try { return File.Exists(MarkerPath) ? CrashSession.Parse(File.ReadAllText(MarkerPath)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { Clear(); }
    }
    public void Write(CrashSession session)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath + ".tmp", session.Serialize());
            File.Move(MarkerPath + ".tmp", MarkerPath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Clear()
    {
        try { File.Delete(MarkerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
