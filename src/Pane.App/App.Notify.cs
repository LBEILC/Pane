// 失败提示（设计负责人）：面板收起之后才失败的动作（粘贴、打开、复制、替换选中文字……），
// 用手势提示框在屏幕中间说一句「X 失败：原因」，不弹窗、不抢焦点、几秒后自己消失。
using System.ComponentModel;
using Pane.Contracts;
namespace Pane;
public sealed partial class App
{
    /// <summary>报告一个动作失败。what 是动作名（「粘贴」「打开」「复制」），界面线程或后台线程都可以调。用户自己取消的不提示。</summary>
    public void ReportFailure(string what, Exception e)
    {
        if (closing || FailureReason(e) is not { } reason) return;
        queue.TryEnqueue(() => { if (!closing) gestures?.Hint(new(GestureHintKind.Failed, [], CommandName: what, Detail: reason)); });
    }

    /// <summary>给用户看的失败原因：一句短话；null = 用户自己取消的，不用提示。面板还开着时由面板自己显示。</summary>
    public static string? FailureReason(Exception e) => e switch
    {
        OperationCanceledException => null,
        Win32Exception { NativeErrorCode: 1223 } => null,                     // 用户在 UAC 提示里点了「否」
        Pane.Services.Clipboard.ClipboardTargetClosedException => "原来的窗口已经关了",
        Pane.Services.Clipboard.ClipboardPermissionException or UnauthorizedAccessException => "没有权限往这个窗口输入（它可能以管理员身份运行）",
        Pane.Services.Clipboard.ClipboardFocusException => "没能切回原来的窗口，内容已经复制好了，按 Ctrl+V 就能贴",
        TimeoutException => "等太久了，稍后再试",
        FileNotFoundException or DirectoryNotFoundException or Win32Exception { NativeErrorCode: 2 or 3 } => "找不到这个文件或程序",
        _ when HasChinese(e.Message) => e.Message.TrimEnd('。', '.'),        // 服务自己写的中文原因直接用
        _ => "出了点问题，稍后再试",
    };

    static bool HasChinese(string s) => s.Any(c => c is >= '一' and <= '鿿');
}
