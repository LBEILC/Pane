using Pane.Contracts;

namespace Pane.Services.Words;

// 回调在调用方的上下文执行；读取完成前绝不打开窗口。
internal static class WordsSelection
{
    internal static async Task ReadAsync(ISelectionReader reader, Func<nint, string?> sourceName,
        Action<string, string?, bool> show, Action empty, CancellationToken token)
    {
        SelectionResult result;
        try { result = await reader.ReadAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception) { if (!token.IsCancellationRequested) empty(); return; }
        if (token.IsCancellationRequested) return;
        if (result.Failure != SelectionFailure.None || result.Files != null || string.IsNullOrWhiteSpace(result.Text)) { empty(); return; }
        string? source = null;
        try { source = sourceName(result.SourceWindow); } catch (Exception) { }
        show(result.Text, source, false);
    }
}
