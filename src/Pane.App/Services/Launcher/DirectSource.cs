using Pane.Contracts;
using Pane.Localization;

namespace Pane.Services.Launcher;

internal sealed class DirectSource(IOpenTargets targets) : LauncherSource(LauncherGroups.Direct, -1, "direct:")
{
    public override int Limit(SourceContext c) => 1;

    public override async Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext c, CancellationToken token)
    {
        var target = await targets.DetectAsync(c.Query, token).ConfigureAwait(false);
        if (target == null) return [];
        if (target.Kind == OpenTargetKind.Url)
            return [new("direct:url:" + target.Value, LauncherItemKind.Web, Group, target.Value, Strings.Launcher_DirectOpenUrl, new("globe"))];
        bool folder = target.Kind == OpenTargetKind.Folder;
        return [new("direct:path:" + target.Value, folder ? LauncherItemKind.Folder : LauncherItemKind.File, Group, target.Value,
            folder ? Strings.Launcher_DirectOpenFolder : Strings.Launcher_DirectOpenFile, new(folder ? "folder" : "doc"))];
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext c, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.Id.StartsWith("direct:url:", StringComparison.Ordinal) && OpenTargetService.Url(item.Id[11..]) is {} url)
            return targets.OpenAsync(new(OpenTargetKind.Url, url), false, token);
        if (item.Id.StartsWith("direct:path:", StringComparison.Ordinal) && OpenTargetService.LocalPath(item.Id[12..]) is {} path)
            return targets.OpenAsync(new(item.Kind == LauncherItemKind.Folder ? OpenTargetKind.Folder : OpenTargetKind.File, path), false, token);
        throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
    }
}
