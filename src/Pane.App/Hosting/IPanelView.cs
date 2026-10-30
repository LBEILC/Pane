using Microsoft.UI.Xaml;
using Windows.Foundation;
namespace Pane.Hosting;
public interface IPanelView
{
    Size PreferredSize { get; }
    event EventHandler<Size>? SizeRequested;
    event EventHandler? CloseRequested;
    void OnShowing(PanelShowContext context);
    void OnShown();
    void OnHidden();
    UIElement? DragRegion { get; }
}
public sealed record PanelShowContext(nint PreviousForegroundWindow, Point? AnchorPhysical, bool Activate);
// Center：工作区正中，改尺寸时保持居中（手势提示框，设计加）
// Tray：托盘菜单，贴着右键那一刻的鼠标点、往任务栏的反方向展开（设计加，2026-10-30，做法照 NovelAIPromptStudio）
public enum PanelPlacement { UpperCenter, Pointer, BottomCenter, Center, Tray }
/// <summary>
/// 二级窗口（文件、翻译、AI 对话）：和设置窗口同一套宿主，失去焦点不关、有任务栏按钮、可改大小；
/// Esc 不关窗口（视图自己处理），关闭用关闭按钮、Ctrl+W（视图发 CloseRequested）、Alt+F4。
/// </summary>
public interface ISecondaryView : IPanelView
{
    /// <summary>右上角图钉：「总在最前」开 / 关。</summary>
    event EventHandler<bool>? TopmostChanged;
    /// <summary>可拖边框改到的最小尺寸。</summary>
    Size MinimumSize { get; }
}
