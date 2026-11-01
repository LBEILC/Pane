// 文件对话框跳转的演示数据（设计负责人，038）：两个开着的资源管理器、四个最近用过的文件夹；跳转什么都不做。
using Pane.Contracts;

namespace Pane.DesignData;

public sealed class FolderJumpSample : IFolderJump
{
    public event EventHandler<FolderJumpRequest>? MenuRequested { add { } remove { } }

    public IReadOnlyList<FolderJumpEntry> Entries() =>
    [
        new(@"D:\照片\2026-10 青岛", "2026-10 青岛", FolderJumpSource.Explorer),
        new(@"C:\Users\你\Downloads", "下载", FolderJumpSource.Explorer),
        new(@"D:\工作\季度汇报", "季度汇报", FolderJumpSource.Recent),
        new(@"D:\工作\合同\2026", "2026", FolderJumpSource.Recent),
        new(@"C:\Users\你\Desktop", "桌面", FolderJumpSource.Recent),
        new(@"E:\", "本地磁盘 (E:)", FolderJumpSource.Recent),
    ];

    public Task JumpAsync(nint dialog, string path, CancellationToken cancellationToken) => Task.CompletedTask;
}
