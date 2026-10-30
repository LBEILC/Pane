// 文件窗口的假数据服务，照原型 prototype/launcher.html 的 files。不查真实文件、不打开任何东西。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class FileSearchSample : IFileSearch
{
    sealed record Entry(string Name, string Folder, FileCategory Category, long? Size, DateTimeOffset Modified, string? Text = null, int? Items = null);

    static readonly DateTimeOffset Now = DateTimeOffset.Now;
    static DateTimeOffset Day(int m, int d, int y = 2026) => new(y, m, d, 12, 0, 0, Now.Offset);
    const long K = 1024, M = 1024 * 1024;

    static readonly Entry[] Files =
    [
        new("报价单-2026Q4.xlsx", @"D:\工作\客户\星河科技", FileCategory.Spreadsheet, 86 * K, Now.AddHours(-2)),
        new("客户报价汇总.docx", @"D:\工作\客户", FileCategory.Document, 1200 * K, Now.AddDays(-1)),
        new("报价模板", @"D:\工作\模板", FileCategory.Folder, null, Day(10, 21), Items: 12),
        new("报价截图.png", @"C:\Users\你\Pictures\Screenshots", FileCategory.Image, 412 * K, Day(10, 27)),
        new("报价说明.txt", @"D:\工作\客户\星河科技", FileCategory.Text, 3 * K, Day(10, 26),
            "报价说明（2026 Q4）\n\n1. 含税价，有效期 30 天\n2. 首期付款 40%，验收后付清\n3. 加急另计，详见附表\n\n联系人：张工 138****0000"),
        new("报价会议录音.m4a", @"D:\工作\录音", FileCategory.Audio, 18 * M, Day(10, 25)),
        new("旧报价 2025.zip", @"D:\归档", FileCategory.Archive, 24 * M, Day(12, 30, 2025)),
        new("报价流程讲解.mp4", @"D:\工作\培训", FileCategory.Video, 212 * M, Day(9, 3)),
        new("报价审批单.pdf", @"D:\工作\客户\星河科技", FileCategory.Pdf, 640 * K, Day(10, 20)),
        new("2026 Q3 季度汇报.pptx", @"D:\工作\汇报", FileCategory.Presentation, 8 * M, Day(10, 9)),
        new("家庭预算.xlsx", @"D:\个人\账本", FileCategory.Spreadsheet, 44 * K, Day(10, 1)),
        new("旅行照片", @"E:\照片\2026", FileCategory.Folder, null, Day(8, 15), Items: 318),
        new("需求与范围.md", @"D:\项目\Pane\docs", FileCategory.Text, 18 * K, Now.AddHours(-12),
            "# 需求与范围\n\n## 要替代的工具\n- uTools：快速启动 / 搜索\n- WGestures：鼠标手势\n- 剪贴板工具\n\n## 不做\n- 插件市场"),
        new("简历-2026.pdf", @"D:\个人", FileCategory.Pdf, 220 * K, Day(6, 2)),
        new("Pane 安装 0.1.123.exe", @"D:\下载", FileCategory.Program, 32 * M, Now.AddHours(-1)),
        new("壁纸 dusk.svg", @"D:\图片\壁纸", FileCategory.Image, 6 * K, Day(10, 28)),
    ];

    public async Task<FilePage> SearchAsync(FileQuery query, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(30, cancellationToken); // 模拟 Everything 往返
        if (Environment.GetEnvironmentVariable("PANE_DESIGN_FILES") == "unavailable")
            throw new FileSearchUnavailableException("Everything 没有运行，打开它之后就能搜文件");
        var q = query.Text.Trim();
        if (q.Length == 0) return new([], 0, null);
        var hits = Files.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var counts = Enum.GetValues<FileCategory>().ToDictionary(c => c, c => c == FileCategory.All ? hits.Count : hits.Count(h => h.Category == c));
        var shown = hits.Where(h => query.Category == FileCategory.All || h.Category == query.Category);
        if (query.Sort == FileSort.Modified) shown = shown.OrderByDescending(h => h.Modified);
        var list = shown.ToList();
        var page = list.Skip(offset).Take(count).Select(f =>
        {
            int i = f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            return new FileHit(System.IO.Path.Combine(f.Folder, f.Name), f.Name, f.Category, f.Size, f.Modified, i >= 0 ? [new TextSpan(i, q.Length)] : null);
        }).ToList();
        return new(page, list.Count, counts);
    }

    public Task<FilePreview> PreviewAsync(FileHit hit, CancellationToken cancellationToken)
    {
        var f = Files.FirstOrDefault(x => x.Name == hit.Name);
        return Task.FromResult(f is null ? new FilePreview() : new FilePreview(f.Text, f.Category == FileCategory.Image ? 1920 : null, f.Category == FileCategory.Image ? 1080 : null, f.Items));
    }

    public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RevealAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
}
