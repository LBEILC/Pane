// 系统文件图标的演示服务（设计负责人，036）：演示数据里的路径都是假的，系统取不到图标。
// 按扩展名在临时目录建一个空的样例文件（文件夹用临时目录本身），交给真实的取图标服务，好了再按原来的假路径通知界面。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class FileIconsSample : IFileIcons, IDisposable
{
    readonly IFileIcons real;
    readonly string samples = Path.Combine(Path.GetTempPath(), "pane-design-icons");
    readonly Dictionary<string, HashSet<string>> waiting = new(StringComparer.OrdinalIgnoreCase);   // 样例路径 → 等它的假路径
    readonly object sync = new();

    public FileIconsSample(string dataDirectory)
    {
        real = new Pane.Services.Icons.FileIcons(dataDirectory);
        real.Ready += (_, sample) =>
        {
            string[] paths;
            lock (sync) { if (!waiting.Remove(sample, out var set)) return; paths = [.. set]; }
            foreach (var p in paths) Ready?.Invoke(this, p);
        };
    }

    public event EventHandler<string>? Ready;

    public string? TryGet(string path, bool folder)
    {
        var sample = folder ? samples : Path.Combine(samples, "样例" + Path.GetExtension(path));
        try
        {
            Directory.CreateDirectory(samples);
            if (!folder && !File.Exists(sample)) File.WriteAllBytes(sample, []);
        }
        catch (IOException) { return null; }
        var png = real.TryGet(sample, folder);
        if (png is null) lock (sync) { if (!waiting.TryGetValue(sample, out var set)) waiting[sample] = set = new(StringComparer.OrdinalIgnoreCase); set.Add(path); }
        return png;
    }

    public void Dispose() => (real as IDisposable)?.Dispose();
}
