using Pane.Localization;
// 手势轨迹（设计负责人）。照原型 drawTrail：主题色 6px 线 + 外发光（模糊 14），中间一条 1.6px 白色高光芯（75%），
// 笔头是带发光的白点（半径 4.5）；松开后整条在 260ms 内淡出。
// 每块显示器一张全屏透明窗（分层 + 鼠标穿透 + 不激活 + 无重定向位图），Composition 画线，不挡任何点击。
// 坐标一律是物理像素（和 Pane.Gestures 的 TrailSample 一致），按各显示器的 DPI 换算。
using System.Numerics;
using System.Runtime.InteropServices;
using Pane.Hosting;
using Windows.UI;
using Windows.UI.Composition;

namespace Pane.Views;

public sealed class GestureTrail : IDisposable
{
    sealed record Surface(nint Window, Windows.UI.Composition.Desktop.DesktopWindowTarget Target, ContainerVisual Root,
        ShapeVisual Glow, ShapeVisual Mask, ShapeVisual Core, CompositionEllipseGeometry Head, DropShadow Shadow, Native.Rect Bounds, float Dpi);

    readonly Compositor compositor = new();
    readonly Native.Proc proc = Native.DefWindowProcW;
    readonly string className = "PaneTrail" + Guid.NewGuid().ToString("N");
    readonly List<Surface> surfaces = [];
    readonly CompositionColorBrush accent, white;
    Vector2 previous;
    bool drawing, visible;
    long fadeEnd;

    public GestureTrail()
    {
        var wc = new Native.WndClass { size = (uint)Marshal.SizeOf<Native.WndClass>(), proc = proc, name = className, instance = Native.GetModuleHandleW(null) };
        Native.RegisterClassExW(ref wc);
        accent = compositor.CreateColorBrush(Color.FromArgb(255, 0x58, 0x74, 0xc9));
        white = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>淡出时长乘的动效速度（设置里的值）。</summary>
    public double MotionSpeed { get; set; } = 1;
    public bool Settled => fadeEnd == 0;
    internal bool Active => drawing || visible || fadeEnd != 0;

    public void Begin(int x, int y, Color color)
    {
        EnsureSurfaces();
        accent.Color = color;
        fadeEnd = 0; drawing = true; previous = new(x, y);
        foreach (var s in surfaces)
        {
            s.Shadow.Color = color;
            s.Root.StopAnimation("Opacity"); s.Root.Opacity = 1;
            s.Glow.Shapes.Clear(); s.Mask.Shapes.Clear(); s.Core.Shapes.Clear();
            s.Head.Center = Local(s, previous);
            AddHead(s);
        }
    }

    public void Move(int x, int y)
    {
        if (!drawing) return;
        var current = new Vector2(x, y);
        // 高回报率鼠标一次只挪一两个像素，太近的点不加线段
        if (Vector2.Distance(current, previous) < 2) return;
        if (!visible) Show();
        foreach (var s in surfaces)
        {
            Vector2 a = Local(s, previous), b = Local(s, current);
            var line = compositor.CreateLineGeometry();
            line.Start = a; line.End = b;
            // 形状顺序：线段在笔头下面
            s.Glow.Shapes.Insert(s.Glow.Shapes.Count - 1, Stroke(line, accent, 6));
            s.Mask.Shapes.Insert(s.Mask.Shapes.Count - 1, Stroke(line, accent, 6));
            s.Core.Shapes.Add(Stroke(line, white, 1.6f));
            s.Head.Center = b;
        }
        previous = current;
    }

    /// <param name="fade">false = 立即消失（设置里关了「轨迹淡出」）。</param>
    public void End(bool fade = true)
    {
        if (!drawing) return;
        drawing = false;
        if (!visible || !fade) { Clear(); return; }
        var duration = TimeSpan.FromMilliseconds(260 * MotionSpeed);
        foreach (var s in surfaces)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
            animation.Duration = duration;
            s.Root.StartAnimation("Opacity", animation);
        }
        fadeEnd = Environment.TickCount64 + (long)duration.TotalMilliseconds + 16;
    }

    /// <summary>宿主的定时器里调用：淡出结束后隐藏窗口、清掉线段。</summary>
    public void Tick()
    {
        if (fadeEnd != 0 && Environment.TickCount64 >= fadeEnd) Clear();
    }

    void Clear()
    {
        fadeEnd = 0;
        foreach (var s in surfaces) { Native.ShowWindow(s.Window, 0); s.Glow.Shapes.Clear(); s.Mask.Shapes.Clear(); s.Core.Shapes.Clear(); }
        visible = false;
    }

    void Show()
    {
        visible = true;
        foreach (var s in surfaces) { Native.ShowWindow(s.Window, 4); Native.SetWindowPos(s.Window, -1, 0, 0, 0, 0, 0x13); }
    }

    static Vector2 Local(Surface s, Vector2 physical) => (physical - new Vector2(s.Bounds.Left, s.Bounds.Top)) / s.Dpi;

    CompositionSpriteShape Stroke(CompositionGeometry line, CompositionBrush brush, float thickness)
    {
        var shape = compositor.CreateSpriteShape(line);
        shape.StrokeBrush = brush; shape.StrokeThickness = thickness;
        shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
        return shape;
    }

    void AddHead(Surface s)
    {
        var dot = compositor.CreateSpriteShape(s.Head);
        dot.FillBrush = white;
        s.Glow.Shapes.Add(dot);
        var mask = compositor.CreateSpriteShape(s.Head);
        mask.FillBrush = white;
        s.Mask.Shapes.Add(mask);
    }

    // 显示器布局变了（插拔、改分辨率、改缩放）才重建
    void EnsureSurfaces()
    {
        var monitors = new List<(Native.Rect Bounds, float Dpi)>();
        MonitorProc callback = (nint monitor, nint dc, ref Native.Rect bounds, nint data) =>
        {
            GetDpiForMonitor(monitor, 0, out var dpi, out _);
            monitors.Add((bounds, (dpi == 0 ? 96 : dpi) / 96f));
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        if (monitors.Count == surfaces.Count && monitors.Zip(surfaces).All(p => p.First.Bounds.Equals(p.Second.Bounds) && p.First.Dpi == p.Second.Dpi)) return;
        DisposeSurfaces();
        foreach (var (b, dpi) in monitors)
        {
            int w = b.Right - b.Left, h = b.Bottom - b.Top;
            var hwnd = Native.CreateWindowExW(0x82800A0, className, Strings.Gestures_TrailTitle, 0x80000000, b.Left, b.Top, w, h, 0, 0, Native.GetModuleHandleW(null), 0);
            if (hwnd == 0) throw new System.ComponentModel.Win32Exception();
            Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);
            var target = Native.Target(compositor, hwnd);
            var size = new Vector2(w / dpi, h / dpi);
            var root = compositor.CreateContainerVisual();
            root.Size = size; root.Scale = new Vector3(dpi, dpi, 1);
            // 外发光：主题色线和笔头另画一份不进树的遮罩，阴影按遮罩的形状画（同 SceneB 的面板阴影）。
            // 直接给 LayerVisual 挂阴影会铺满整块屏幕。
            var mask = compositor.CreateShapeVisual(); mask.Size = size;
            var maskSurface = compositor.CreateVisualSurface(); maskSurface.SourceVisual = mask; maskSurface.SourceSize = size;
            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = 14; shadow.Offset = Vector3.Zero; shadow.Color = accent.Color;
            shadow.Mask = compositor.CreateSurfaceBrush(maskSurface);
            var glowLayer = compositor.CreateSpriteVisual();
            glowLayer.Size = size; glowLayer.Shadow = shadow;
            var glow = compositor.CreateShapeVisual(); glow.Size = size;
            // 高光芯：整层 75% 透明，线段接头处不会叠出亮点
            var coreLayer = compositor.CreateLayerVisual();
            coreLayer.Size = size; coreLayer.Opacity = 0.75f;
            var core = compositor.CreateShapeVisual(); core.Size = size;
            coreLayer.Children.InsertAtTop(core);
            root.Children.InsertAtTop(glowLayer);
            root.Children.InsertAtTop(glow);
            root.Children.InsertAtTop(coreLayer);
            target.Root = root;
            var head = compositor.CreateEllipseGeometry(); head.Radius = new Vector2(4.5f);
            surfaces.Add(new(hwnd, target, root, glow, mask, core, head, shadow, b, dpi));
        }
    }

    void DisposeSurfaces()
    {
        foreach (var s in surfaces)
        {
            s.Target.Root = null; s.Target.Dispose(); s.Root.Dispose(); s.Shadow.Dispose(); s.Mask.Dispose();
            Native.DestroyWindow(s.Window);
        }
        surfaces.Clear(); visible = false;
    }

    public void Dispose()
    {
        DisposeSurfaces();
        accent.Dispose(); white.Dispose(); compositor.Dispose();
        Native.UnregisterClassW(className, Native.GetModuleHandleW(null));
    }

    delegate bool MonitorProc(nint monitor, nint dc, ref Native.Rect rect, nint data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);
}
