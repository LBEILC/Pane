// 剪贴板「不抢焦点」打开时的按键接管（issue 20，059）。归设计负责人定义；由工程实现（059）。
// 面板以不激活的方式显示，原来的窗口（重命名框、地址栏）一直保有焦点；面板开着时，下面这些键由服务在全局键盘钩子里拦下、转给面板，
// 不传给原窗口；其余按键照常落到原窗口。规则见 docs/design.md「剪贴板不抢焦点」。
namespace Pane.Contracts;

public enum PanelKey
{
    /// <summary>↑（不带 Ctrl / Alt / Win；自动重复照常一次次报）</summary>
    Up,
    /// <summary>↓</summary>
    Down,
    /// <summary>Enter</summary>
    Enter,
    /// <summary>Shift+Enter</summary>
    ShiftEnter,
    /// <summary>Alt+1～9（主键盘数字）；Number 为 1～9</summary>
    Quick,
    /// <summary>Ctrl+P</summary>
    Pin,
    /// <summary>Tab（只在 Begin 时 sendEnabled 为 true 才拦）</summary>
    Send,
    /// <summary>Esc</summary>
    Escape,
    /// <summary>打字：会产生字符的键（不带 Ctrl / Alt / Win，可带 Shift）。收到后界面切到激活方式、聚焦搜索框，再调 ReplayAndEnd</summary>
    Type,
}

/// <summary>Number 只在 Quick 时有值（1～9），其余为 0。</summary>
public readonly record struct PanelKeyPress(PanelKey Key, int Number = 0);

public interface IPanelKeyRouter
{
    /// <summary>
    /// 开始接管。target：面板打开前的前台窗口。返回 false 表示这次接管不了（目标窗口权限比 Pane 高、钩子挂不上等），
    /// 宿主改用原来的激活方式打开面板。已在接管中再调用：先 End 再重新开始。
    /// </summary>
    bool Begin(nint target, bool sendEnabled);

    /// <summary>停止接管，之后不再拦新按下的键。面板收起、切到激活方式、粘贴前都会调。重复调用无害。</summary>
    void End();

    /// <summary>
    /// 收到 Type 之后：界面已经激活面板、搜索框拿到了焦点。停止接管，并把从那个键起排队的按键按原顺序重新发出（落进搜索框，输入法照常组字）。
    /// 不在接管中时什么也不做。
    /// </summary>
    void ReplayAndEnd();

    /// <summary>正在接管。</summary>
    bool Active { get; }

    /// <summary>拦下的按键。在界面线程上触发。</summary>
    event EventHandler<PanelKeyPress>? Pressed;
}
