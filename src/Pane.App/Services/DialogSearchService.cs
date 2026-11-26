using System.Runtime.InteropServices;
using Pane.Contracts;
using Pane.Services.Launcher;

namespace Pane.Services;

internal sealed class DialogSearchService : IDialogSearch
{
    readonly FolderJumpService? folders;
    readonly IEverythingIpc ipc;
    readonly Func<SearchMemory?>? memory;
    FileDialogInfo? current;
    Task delivery = Task.CompletedTask;
    long generation;

    internal DialogSearchService(IEverythingIpc ipc, FolderJumpService? folders = null, Func<SearchMemory?>? memory = null)
    {
        this.ipc = ipc;
        this.memory = memory;
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
            if (!token.StartsWith("*.") || !FileSearch.ValidExtension(token[2..])) return (name.Length > 0 ? name : null, null);
            extensions.Add(token[2..].ToLowerInvariant());
        }
        return (name.Length > 0 ? name : null, extensions.Distinct().ToArray());
    }

    [DllImport("Pane.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void PaneReadDialog(nint window, int filter, FolderJumpService.DialogCallback callback);

    internal static FileDialogInfo? ReadDialog(nint window, bool filter)
    {
        FileDialogInfo? result = null;
        PaneReadDialog(window, filter ? 1 : 0, (dialog, bounds, flags, type, app) =>
            result = Describe(dialog, bounds, flags, type, app));
        return result;
    }

    static FileDialogInfo? Describe(nint dialog, FolderJumpService.Bounds bounds, int flags, string type, string appName)
    {
        var (name, extensions) = ParseType(type);
        return dialog == 0 ? null : new FileDialogInfo(dialog, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, extensions, name, (flags & 8) != 0, string.IsNullOrEmpty(appName) ? null : appName);
    }

    // 原生回调在同一消息线程串行。订阅者可能重新应用设置，不可在原生回调栈内执行。
    void Update(nint dialog, FolderJumpService.Bounds bounds, int flags, string type, string appName)
    {
        var next = Describe(dialog, bounds, flags, type, appName);
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

    public async Task<DialogSearchResult> SearchAsync(string text, FileDialogInfo? dialog, int maxFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        if (string.IsNullOrWhiteSpace(text)) return new([],0);
        text = text.Trim();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(1000);
        var ids = memory?.Invoke()?.FileIds(text) ?? [];
        var extensions = dialog?.Extensions?.Where(FileSearch.ValidExtension).ToArray();
        bool includeFiles = dialog?.FoldersOnly != true && extensions is not { Length: 0 };
        var bothPrefixes = new Lazy<Task<EverythingPage>>(() => ipc.QueryAsync(
            FileSearch.Restrict($"startwith:\"{text}\"",extensions,false),0,FileRanking.PrefixLimit,1,budget.Token,1000));
        async Task<EverythingPage> Prefix(Func<string,string> filter, bool foldersOnly)
        {
            var both = await bothPrefixes.Value.ConfigureAwait(false);
            if (both.Total > both.Items.Count)
                return await ipc.QueryAsync(filter($"startwith:\"{text}\""),0,FileRanking.PrefixLimit,1,budget.Token,1000).ConfigureAwait(false);
            var items = both.Items.Where(hit => (hit.Category == FileCategory.Folder) == foldersOnly).ToArray();
            return new(items,items.Length);
        }
        async Task<EverythingPage> Group(Func<string,string> filter, int limit, bool foldersOnly)
        {
            try
            {
                if (limit > 0 && FileRanking.Plain(text))
                {
                    var head = await FileRanking.HeadAsync(ipc,text,filter,ids,budget.Token,1000,true,
                        sharedPrefix: includeFiles && maxFiles > 0 ? () => Prefix(filter,foldersOnly) : null).ConfigureAwait(false);
                    return new(head.Items.Take(limit).ToArray(),head.Total);
                }
                var page = await ipc.QueryAsync(filter(text),0,limit,1,budget.Token,1000).WaitAsync(budget.Token).ConfigureAwait(false);
                return new(page.Items.Take(limit).Select(h => h with { Highlights = FileSearch.Highlights(h.Name,text) }).ToArray(),page.Total);
            }
            catch (TimeoutException) { return new([],-1); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new([],-1); }
        }
        var foldersTask = Group(q => FileSearch.Filter(q,FileCategory.Folder),8,true);
        var filesTask = !includeFiles
            ? Task.FromResult(new EverythingPage([],0))
            : Group(q => $"<{q}> file:" + (extensions == null ? "" : " ext:" + string.Join(';',extensions)),maxFiles,false);
        var groups = await Task.WhenAll(foldersTask,filesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        FileHit[] items = [.. groups[0].Items, .. groups[1].Items];
        return new(items,groups.Any(g => g.Total < 0) ? items.Length : groups.Sum(g => g.Total));
    }

    public Task NavigateAsync(nint dialog, string folder, string? fileName, bool confirm, CancellationToken cancellationToken) =>
        folders != null && Current?.Dialog == dialog ? folders.NavigateAsync(dialog, folder, fileName, confirm, cancellationToken) : Task.CompletedTask;
}
