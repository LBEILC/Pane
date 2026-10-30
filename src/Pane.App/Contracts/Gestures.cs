// 鼠标手势的轨迹和提示框（设计负责人定义界面侧；工程把 Pane.Gestures 的事件翻译成这里的类型交给 GestureOverlay）。
// 规则见 docs/gestures.md；外观照原型 #gesture-canvas / #gesture-hint。
namespace Pane.Contracts;

/// <summary>八个方向，数值和 Pane.Gestures.GestureDirection 一致（可以直接强转）。</summary>
public enum GestureArrow { Up = 0, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft }

/// <summary>按住触发键期间的修饰动作（提示框里画成一个小胶囊）。</summary>
public enum GestureHintModifier { None, WheelUp, WheelDown, LeftClick, MiddleClick, X1Click, X2Click }

public enum GestureHintKind
{
    /// <summary>正在画：有匹配就显示命令名，没有就显示「未识别的手势」。一直显示到下一个状态。</summary>
    Drawing,
    /// <summary>已执行（松开时，或「即时」修饰动作每触发一次）：轻微弹一下，稍后收起。</summary>
    Executed,
    /// <summary>松开时没识别：什么都不做，提示稍后收起。</summary>
    NotRecognized,
    /// <summary>识别了但执行失败：Detail 写原因。</summary>
    Failed,
    /// <summary>静止超时取消（可选功能，默认关）。</summary>
    Cancelled,
    /// <summary>没有方向的一句话提示，例如「手势已暂停」「手势已恢复」。文字放 CommandName。</summary>
    Notice,
}

/// <param name="Arrows">已识别的方向序列（可以为空：只按住右键滚滚轮）。</param>
/// <param name="CommandName">匹配到的命令名（用户配置里的名字）；没有匹配为 null。</param>
/// <param name="Detail">Failed 的原因。</param>
public sealed record GestureHint(
    GestureHintKind Kind,
    IReadOnlyList<GestureArrow> Arrows,
    GestureHintModifier Modifier = GestureHintModifier.None,
    string? CommandName = null,
    string? Detail = null);
