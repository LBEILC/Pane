using System.Text.Json;
using Pane.Contracts;

namespace Pane.Services.Clipboard;

internal sealed partial class ClipboardHistory
{
    readonly Dictionary<string,string> installedIcons=new(StringComparer.OrdinalIgnoreCase);
    DateTime iconIndexAt;
    LauncherIcon SourceIcon(string source)
    {
        var cached=entries.Find(c=>c.Replica?.Origin==null && c.Entry.Source==source && c.Entry.SourceIcon!=null)?.Entry.SourceIcon;
        if(cached!=null)return cached;
        var path=Path.Combine(settings.DirectoryPath,"launcher-index.json");
        try
        {
            var stamp=File.GetLastWriteTimeUtc(path);
            if(stamp!=iconIndexAt)
            {
                installedIcons.Clear();
                if(File.Exists(path))
                {
                    using var file=File.OpenRead(path);
                    using var index=JsonDocument.Parse(file);
                    if(index.RootElement.TryGetProperty("Snapshot",out var snapshot) && snapshot.TryGetProperty("Apps",out var apps))
                        foreach(var app in apps.EnumerateArray())
                        {
                            if(!app.TryGetProperty("Icon",out var icon) || icon.GetString() is not {} image)continue;
                            if(app.TryGetProperty("Name",out var name) && name.TryGetProperty("Name",out var display) && display.GetString() is {} title)
                                installedIcons.TryAdd(title,image);
                            if(app.TryGetProperty("Target",out var target) && target.GetString() is {} exe)
                                installedIcons.TryAdd(Path.GetFileName(exe),image);
                        }
                }
                iconIndexAt=stamp;
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){ }
        return new("app",installedIcons.GetValueOrDefault(source));
    }
}
