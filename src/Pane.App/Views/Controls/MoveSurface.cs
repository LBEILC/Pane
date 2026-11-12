// 拖动底面（设计负责人，2026-11-11）：面板上能按住拖动整个窗口的空白处，悬停显示「移动」光标（原型 cursor: move）。
// 放在可拖动区域（例如启动器搜索行）的最底层、铺满；上面的按钮、输入框是它的兄弟而不是子元素，
// 所以光标只在点到空白处时换成移动，按钮上照旧是箭头。拖动本身由宿主处理（PanelPosition.CanDrag），这里只管光标。
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pane.Views.Controls;

public sealed partial class MoveSurface : Grid
{
    public MoveSurface()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // 透明也要命中，空白处才收得到指针
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }
}
