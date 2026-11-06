// 输入光标跟文字色（设计，2026-10-31 起；2026-11-06 改做法，issue 30，实验见 docs/research/068-输入框光标颜色实验.md）。
// WinUI 的光标是 TextBox 里的一个白色图形，合成方式是内部的「反色」（ElementCompositeMode 值 3，公开枚举里没有）：
// 它只能反到本窗口的 XAML 内容，Pane 的玻璃在另一扇窗口，输入框底下是透明的，反出来永远是白的，浅色模式看不见。
// TextBox 没有改光标颜色的 API，原来在光标上面盖一条文字色竖条，但位置和高度靠字符坐标估算，缩放不是 100% 时对不齐（issue 30）。
// 现在直接把这个原生图形改成普通绘制、填文字色：位置、尺寸、闪烁全由系统管。
// 依赖内部视觉树：找不到（以后 WinUI 改了结构）就什么都不做，退回系统的白光标，不会错位。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Pane.Views.Controls;

internal sealed class PaneCaret
{
    const ElementCompositeMode NativeInvert = (ElementCompositeMode)3;
    readonly TextBox box;
    Shape? caret;
    bool pending;

    PaneCaret(TextBox box)
    {
        this.box = box;
        // 光标图形在第一次需要时才建，控件重建模板时也会换新的，所以这些时机都看一眼
        box.Loaded += (_, _) => Check();
        box.GotFocus += (_, _) => Check();
        box.SelectionChanged += (_, _) => Check();
        box.TextChanged += (_, _) => Check();
        box.ActualThemeChanged += (_, _) => Paint();
        box.RegisterPropertyChangedCallback(Control.ForegroundProperty, (_, _) => Paint());
    }

    public static void Attach(TextBox box) => _ = new PaneCaret(box);

    // 图形可能在事件之后才建出来，下一帧再找
    void Check()
    {
        if (pending || Valid()) return;
        pending = true;
        CompositionTarget.Rendering += Next;
    }

    void Next(object? sender, object e)
    {
        CompositionTarget.Rendering -= Next;
        pending = false;
        if (Valid()) return;
        caret = Find(box);
        if (caret is null) return;
        caret.CompositeMode = ElementCompositeMode.SourceOver;
        Paint();
    }

    // 还在这个输入框里、没被系统改回反色
    bool Valid() => caret is { CompositeMode: ElementCompositeMode.SourceOver } c && VisualTreeHelper.GetParent(c) is not null;

    void Paint()
    {
        if (caret is not null) caret.Fill = box.Foreground;
    }

    static Shape? Find(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Shape { CompositeMode: NativeInvert } s) return s;
            if (Find(child) is { } found) return found;
        }
        return null;
    }
}
