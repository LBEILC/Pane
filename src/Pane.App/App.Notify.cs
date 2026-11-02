using Pane.Localization;
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
        Pane.Services.Clipboard.ClipboardTargetClosedException => Strings.Common_TargetClosed,
        Pane.Services.Clipboard.ClipboardPermissionException or UnauthorizedAccessException => Strings.Common_InputPermissionDenied,
        Pane.Services.Clipboard.ClipboardFocusException => Strings.Common_FocusFailed,
        TimeoutException => Strings.Common_Timeout,
        FileNotFoundException or DirectoryNotFoundException or Win32Exception { NativeErrorCode: 2 or 3 } => Strings.Common_FileNotFound,
        _ when HasChinese(e.Message) => e.Message.TrimEnd('。', '.'),        // 服务自己写的中文原因直接用
        _ => Strings.Common_TryAgain,
    };

    static bool HasChinese(string s) => s.Any(c => c is >= '一' and <= '鿿');
}
