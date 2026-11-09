namespace Pane;

public sealed partial class App
{
    CancellationTokenSource? wordsSelectionCancellation;
    async Task ReadWordsSelection()
    {
        if (Words == null || selectionReader == null) return;
        wordsSelectionCancellation?.Cancel();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        wordsSelectionCancellation = pending;
        try
        {
            await Services.Words.WordsSelection.ReadAsync(selectionReader, Services.SourceProgram.Name,
                (text, source, fromClipboard) => { if (!closing && Words != null) ShowWords(text, source, fromClipboard); },
                () => { if (!closing && Words != null) ReportWordsNoSelection(); }, pending.Token);
        }
        finally { if (wordsSelectionCancellation == pending) wordsSelectionCancellation = null; }
    }
}
