using Pane.Contracts;

namespace Pane.DesignData;

internal sealed class OpenTargetsSample : IOpenTargets
{
    static readonly OpenTarget[] Examples =
    [
        new(OpenTargetKind.Url, "https://github.com/example/pane/issues"),
        new(OpenTargetKind.Folder, @"D:\设计"),
        new(OpenTargetKind.File, @"D:\设计\设计稿-v3.fig")
    ];

    public Task<OpenTarget?> DetectAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Array.Find(Examples, item => item.Value == text));
    }

    public Task<OpenTarget?> DetectAsync(ClipEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind == ClipKind.Files && entry.Files is [var path])
            return Task.FromResult(Array.Find(Examples, item => item.Kind != OpenTargetKind.Url && item.Value == path));
        return DetectAsync(entry.Kind is ClipKind.Files or ClipKind.Image ? "" : entry.Text ?? "", cancellationToken);
    }

    public Task OpenAsync(OpenTarget target, bool reveal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
