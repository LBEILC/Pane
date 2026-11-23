// 对话框搜索的假数据（设计负责人，2026-11-23）：演示模式没有真实对话框，给一个假的对话框位置和几条结果，看搜索框的样子。
// PANE_DESIGN_OPEN=dialog 打开；PANE_DESIGN_DIALOG=save 当成另存为；PANE_DESIGN_QUERY 预先填入搜索词。不读真实磁盘。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class DialogSearchSample(bool save) : IDialogSearch
{
    public event EventHandler<FileDialogInfo?>? DialogChanged { add { } remove { } }

    // 假对话框：主屏左上 (360, 160)，800 × 520（物理像素）
    public FileDialogInfo? Current { get; } = new(0, 360, 160, 1160, 680, true, false, save,
        save ? ["png"] : ["png", "jpg", "jpeg", "psd", "heic", "svg"], save ? "PNG" : "图像文件"); // 不翻译：演示数据

    static readonly (string Path, bool Folder)[] Index =
    [
        (@"D:\设计\海报", true), (@"D:\设计\海报\参考图", true), (@"D:\设计\图标", true), (@"D:\照片\2026-10 旅行", true),
        (@"D:\设计\海报\春季海报.psd", false), (@"D:\设计\海报\春季海报-导出.png", false), (@"D:\设计\海报\春季海报-小样.jpg", false),
        (@"D:\设计\图标\app-icon@2x.png", false), (@"C:\Users\你\Desktop\海报终稿.png", false), (@"C:\Users\你\Downloads\截图 2026-11-20.png", false),
        (@"C:\Users\你\Documents\海报文案.docx", false),
    ];

    public Task<IReadOnlyList<FileHit>> SearchAsync(string text, IReadOnlyList<string>? extensions, int maxFiles, CancellationToken cancellationToken)
    {
        var hits = Index.Where(x => System.IO.Path.GetFileName(x.Path).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Folder || extensions is null || extensions.Contains(System.IO.Path.GetExtension(x.Path).TrimStart('.').ToLowerInvariant()))
            .OrderBy(x => x.Folder ? 0 : 1)
            .Select(x =>
            {
                var name = System.IO.Path.GetFileName(x.Path);
                var at = name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                return new FileHit(x.Path, name, x.Folder ? FileCategory.Folder : FileCategory.Image, null, null, at >= 0 ? [new TextSpan(at, text.Length)] : null);
            }).ToList();
        return Task.FromResult<IReadOnlyList<FileHit>>(hits);
    }

    public Task NavigateAsync(nint dialog, string folder, string? fileName, bool confirm, CancellationToken cancellationToken) => Task.CompletedTask;
}
