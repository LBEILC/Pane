using Pane.Contracts;
using Pane.Services.Launcher;

namespace Pane.Services;

internal sealed class DialogSearchService : IDialogSearch
{
    readonly FolderJumpService? folders;
    readonly IEverythingIpc ipc;
    FileDialogInfo? current;
    Task delivery = Task.CompletedTask;
    long generation;

    internal DialogSearchService(IEverythingIpc ipc, FolderJumpService? folders = null)
    {
        this.ipc = ipc;
        this.folders = folders;
        if (folders != null) folders.DialogObserver = Update;
    }

    public FileDialogInfo? Current => Volatile.Read(ref current);
    public event EventHandler<FileDialogInfo?>? DialogChanged;

    internal static (string? Name, IReadOnlyList<string>? Extensions) ParseType(string label)
    {
        label = label.Trim();
        if (label.Length == 0) return (null, null);
        int pattern = label.IndexOf("*.", StringComparison.Ordinal);
        if (pattern < 0) return (label, null);
        var name = label[..pattern].TrimEnd(' ', '(', '（');
        var tokens = label[pattern..].TrimEnd(')', '）').Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var extensions = new List<string>();
        foreach (var token in tokens)
        {
            if (!token.StartsWith("*.") || !ValidExtension(token[2..])) return (name.Length > 0 ? name : null, null);
            extensions.Add(token[2..].ToLowerInvariant());
        }
        return (name.Length > 0 ? name : null, extensions.Distinct().ToArray());
    }

    static bool ValidExtension(string ext) => ext.Length > 0 && ext.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '+');

    // 原生回调在同一消息线程串行。订阅者可能重新应用设置，不可在原生回调栈内执行。
    void Update(nint dialog, FolderJumpService.Bounds bounds, int flags, string type)
    {
        var (name, extensions) = ParseType(type);
        var next = dialog == 0 ? null : new FileDialogInfo(dialog, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, extensions, name);
        Volatile.Write(ref current, next);
        if (flags < 0) { Interlocked.Increment(ref generation); return; }
        long version = Interlocked.Read(ref generation);
        var handler = DialogChanged;
        if (handler != null)
            delivery = delivery.ContinueWith(_ =>
            {
                if (Interlocked.Read(ref generation) == version) handler(this, next);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public async Task<IReadOnlyList<FileHit>> SearchAsync(string text, IReadOnlyList<string>? extensions, int maxFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        if (string.IsNullOrWhiteSpace(text)) return [];
        text = text.Trim();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(1));
        async Task<IReadOnlyList<FileHit>> Group(string query, int limit)
        {
            if (limit == 0) return [];
            try
            {
                var page = await ipc.QueryAsync(query, 0, limit, 1, budget.Token, 1000).ConfigureAwait(false);
                return page.Items.Take(limit).Select(hit => hit with { Highlights = FileSearch.Highlights(hit.Name, text) }).ToArray();
            }
            catch (TimeoutException) { return []; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
        }
        string files = $"<{text}> file:";
        if (extensions != null)
        {
            var valid = extensions.Where(ValidExtension).ToArray();
            if (valid.Length == 0) maxFiles = 0;
            else files += " ext:" + string.Join(';', valid);
        }
        var groups = await Task.WhenAll(Group(FileSearch.Filter(text, FileCategory.Folder), 8), Group(files, maxFiles)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. groups[0], .. groups[1]];
    }

    public Task NavigateAsync(nint dialog, string folder, string? fileName, bool confirm, CancellationToken cancellationToken) =>
        folders != null && Current?.Dialog == dialog ? folders.NavigateAsync(dialog, folder, fileName, confirm, cancellationToken) : Task.CompletedTask;
}
