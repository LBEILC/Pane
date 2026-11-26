using Pane.Contracts;

namespace Pane.Services.Launcher;

internal sealed class OpenTargetService(ILauncherExecutor executor) : IOpenTargets
{
    readonly Dictionary<string, Task<OpenTarget?>> pending = new(StringComparer.OrdinalIgnoreCase);
    internal Func<string, OpenTargetKind?> Probe { get; set; } = path =>
        File.GetAttributes(path).HasFlag(FileAttributes.Directory) ? OpenTargetKind.Folder : OpenTargetKind.File;

    internal static string SingleLine(string text)
    {
        if (text.AsSpan().IndexOfAny("\r\n\v\f\u0085\u2028\u2029") >= 0) return "";
        text = text.Trim();
        return text.Length >= 2 && (text[0], text[^1]) is ('"', '"') or ('\'', '\'') or ('<', '>')
            ? text[1..^1] : text;
    }

    internal static string? Url(string text)
    {
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
        return !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Host.Length > 0 ? uri.AbsoluteUri : null;
    }

    internal static string? LocalPath(string text)
    {
        try
        {
            text = Environment.ExpandEnvironmentVariables(text);
            if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
                text = uri.LocalPath;
            }
            text = text.Replace('/', '\\');
            if (text.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int serverEnd = text.IndexOf('\\', 2);
                if (serverEnd <= 2 || serverEnd + 1 >= text.Length || text[serverEnd + 1] == '\\' ||
                    text[2..serverEnd] is "." or "?" || text.Contains(':')) return null;
            }
            else if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' ||
                     text[2] != '\\' || text.IndexOf(':', 2) >= 0) return null;
            return Path.GetFullPath(text);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public Task<OpenTarget?> DetectAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string input = SingleLine(text);
        return Url(input) is {} url ? Task.FromResult<OpenTarget?>(new(OpenTargetKind.Url, url))
            : DetectPathAsync(input, cancellationToken);
    }

    public Task<OpenTarget?> DetectAsync(ClipEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return entry.Kind switch
        {
            ClipKind.Files when entry.Files is [var path] => DetectPathAsync(SingleLine(path), cancellationToken),
            ClipKind.Files or ClipKind.Image => Task.FromResult<OpenTarget?>(null),
            _ => DetectAsync(entry.Text ?? "", cancellationToken)
        };
    }

    async Task<OpenTarget?> DetectPathAsync(string input, CancellationToken token)
    {
        if (LocalPath(input) is not {} path) return null;
        Task<OpenTarget?> check;
        lock (pending)
        {
            if (!pending.TryGetValue(path, out check!))
            {
                // 等待者超时或取消后，仍由真正完成探测的线程移除任务。
                check = Task.Run(() =>
                {
                    try { return Probe(path) is {} kind ? new OpenTarget(kind, path) : null; }
                    catch (Exception) { return null; }
                    finally { lock (pending) pending.Remove(path); }
                });
                pending.Add(path, check);
            }
        }
        try { return await check.WaitAsync(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    public Task OpenAsync(OpenTarget target, bool reveal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return reveal && target.Kind != OpenTargetKind.Url ? executor.RevealAsync(target.Value, cancellationToken)
            : executor.OpenAsync(target.Value, false, cancellationToken);
    }
}
