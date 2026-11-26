// 对话框搜索的假数据（设计负责人，2026-11-23）：演示模式没有真实对话框，给一个假的对话框位置和几条结果，看搜索框的样子。
// PANE_DESIGN_OPEN=dialog 打开；PANE_DESIGN_DIALOG=save 当成另存为、=folder 当成「选择文件夹」；PANE_DESIGN_QUERY 预先填入搜索词。不读真实磁盘。
// 2026-11-25（任务 125）：照服务给 Total（文件夹 8 个、文件 maxFiles 个以外还有就出「查看全部」）、程序名；PANE_DESIGN_OPEN=dialog-files 直接看文件窗口「为对话框选」。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class DialogSearchSample(string mode) : IDialogSearch
{
    public event EventHandler<FileDialogInfo?>? DialogChanged { add { } remove { } }

    // 假对话框：主屏左上 (360, 160)，800 × 520（物理像素）
    public FileDialogInfo? Current { get; } = mode switch
    {
        "save" => new(0, 360, 160, 1160, 680, true, false, true, ["png"], "PNG", AppName: "画图"), // 不翻译：演示数据
        "folder" => new(0, 360, 160, 1160, 680, true, false, false, null, null, FoldersOnly: true, AppName: "Visual Studio Code"),
        _ => new(0, 360, 160, 1160, 680, true, false, false, ["png", "jpg", "jpeg", "psd", "heic", "svg"], "图像文件", AppName: "画图"), // 不翻译：演示数据
    };

    static readonly (string Path, bool Folder, int Days)[] Index =
    [
        (@"D:\设计\海报", true, 3), (@"D:\设计\海报\参考图", true, 9), (@"D:\设计\图标", true, 20), (@"D:\照片\2026-10 旅行", true, 45),
        (@"D:\设计\海报\春季海报.psd", false, 0), (@"D:\设计\海报\春季海报-导出.png", false, 1), (@"D:\设计\海报\春季海报-小样.jpg", false, 3),
        (@"D:\设计\图标\app-icon@2x.png", false, 9), (@"C:\Users\你\Desktop\海报终稿.png", false, 0), (@"C:\Users\你\Downloads\截图 2026-11-20.png", false, 5),
        (@"C:\Users\你\Documents\海报文案.docx", false, 2),
        // 截图那一类（2026-11-25）：名字中间夹着 Projects 的临时会话目录，服务按相关度降到后面
        (@"D:\Projects", true, 1), (@"D:\Projects\ProjectX", true, 3), (@"E:\备份\MyProjects", true, 20), (@"D:\归档\旧 Projects 2024", true, 120),
        (@"C:\Users\你\AppData\Local\Temp\projects-cache", true, 0), (@"D:\Projects\pane-site\node_modules\project-name", true, 9),
        (@"C:\Users\你\.dsh\sessions\--D--Projects-notes--", true, 0), (@"C:\Users\你\.finch\sessions\--D--Projects-pane-site--", true, 0),
        (@"C:\Users\你\.dsh\sessions\--C-Users-你-AppData-Local-Temp-claude-D--Projects-pane-01", true, 0),
        (@"C:\Users\你\.dsh\sessions\--C-Users-你-AppData-Local-Temp-claude-D--Projects-pane-02", true, 0),
        (@"D:\Projects\ProjectX\project-cover.png", false, 3), (@"C:\Users\你\Downloads\project-logo.png", false, 20),
        (@"D:\Projects\Projects 计划.xlsx", false, 1),
    ];

    // 演示用的排法：顺序就是上面写的顺序（已经按相关度排好）
    internal static IEnumerable<FileHit> Hits(string text, FileDialogInfo? dialog) =>
        Index.Where(x => System.IO.Path.GetFileName(x.Path).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Folder || (dialog?.FoldersOnly != true && (dialog?.Extensions is not { } ext || ext.Contains(System.IO.Path.GetExtension(x.Path).TrimStart('.').ToLowerInvariant()))))
            .Select(x =>
            {
                var name = System.IO.Path.GetFileName(x.Path);
                var at = name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                var category = x.Folder ? FileCategory.Folder : System.IO.Path.GetExtension(name) == ".docx" ? FileCategory.Document : System.IO.Path.GetExtension(name) == ".xlsx" ? FileCategory.Spreadsheet : FileCategory.Image;
                return new FileHit(x.Path, name, category, x.Folder ? null : 412_000, DateTimeOffset.Now.AddDays(-x.Days), at >= 0 ? [new TextSpan(at, text.Length)] : null);
            });

    public Task<DialogSearchResult> SearchAsync(string text, FileDialogInfo? dialog, int maxFiles, CancellationToken cancellationToken)
    {
        var hits = Hits(text, dialog).ToList();
        var folders = hits.Where(h => h.Category == FileCategory.Folder).ToList();
        var files = hits.Where(h => h.Category != FileCategory.Folder).ToList();
        return Task.FromResult(new DialogSearchResult([.. folders.Take(8), .. files.Take(maxFiles)], hits.Count));
    }

    public Task NavigateAsync(nint dialog, string folder, string? fileName, bool confirm, CancellationToken cancellationToken) => Task.CompletedTask;
}
