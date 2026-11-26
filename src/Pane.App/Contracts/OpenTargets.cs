namespace Pane.Contracts;

// 网址和路径（issue 71 / 76，任务 141）：启动器直接输入、启动器附件、超级面板、剪贴板条目共用一套认法。
public enum OpenTargetKind { Url, Folder, File }

/// <summary>认出来的打开目标。<see cref="Value"/> 是规范化后的网址或完整路径。</summary>
public sealed record OpenTarget(OpenTargetKind Kind, string Value);

public interface IOpenTargets
{
    /// <summary>认一段文字：单行、去掉首尾空白和外面成对的引号 / 尖括号后，整段是网址或存在的路径才返回。
    /// 路径存在性在后台检查，300 ms 内没结果返回 null；不抛异常（取消除外）。</summary>
    Task<OpenTarget?> DetectAsync(string text, CancellationToken cancellationToken);

    /// <summary>认一条剪贴板记录：文字条目同 <see cref="DetectAsync(string, CancellationToken)"/>；只有一个文件 / 文件夹的条目返回那个路径；其他返回 null。</summary>
    Task<OpenTarget?> DetectAsync(ClipEntry entry, CancellationToken cancellationToken);

    /// <summary>打开：网址用默认浏览器，文件夹在资源管理器里打开，文件用默认程序打开。
    /// <paramref name="reveal"/> 为 true 时在资源管理器里显示并选中（只对文件、文件夹有效）。失败抛异常，由界面提示。</summary>
    Task OpenAsync(OpenTarget target, bool reveal, CancellationToken cancellationToken);
}
