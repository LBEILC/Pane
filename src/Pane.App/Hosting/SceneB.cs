using Pane.Hosting;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI;

// 描边、阴影数值沿用 007。
internal sealed class SceneB : IDisposable
{
    readonly List<CompositionObject> owned=[];
    CompositionBrush? backdropBrush;
    T Own<T>(T value) where T:CompositionObject {owned.Add(value);return value;}
    void Release(CompositionObject? value){if(value==null)return;owned.Remove(value);value.Dispose();}
    readonly Compositor c;
    internal ContainerVisual Root { get; }
    internal ContainerVisual Shadows { get; }
    internal ShapeVisual Edges { get; }
    readonly CompositionRoundedRectangleGeometry maskGeometry, outer, inner;
    readonly ShapeVisual maskVisual;
    readonly CompositionVisualSurface maskSurface;
    readonly SpriteVisual[] shadowVisuals;
    readonly SpriteVisual material, luminosity, tint;
    readonly CompositionRoundedRectangleGeometry clipGeometry;
    internal SceneB(Compositor compositor, bool dark, bool solid)
    {
        c=compositor; Root=Own(c.CreateContainerVisual()); Shadows=Own(c.CreateContainerVisual());
        Root.Children.InsertAtTop(Shadows);
        maskGeometry=Own(c.CreateRoundedRectangleGeometry()); maskGeometry.CornerRadius=new Vector2(20);
        maskVisual=Own(c.CreateShapeVisual());
        var mask=Own(c.CreateSpriteShape(maskGeometry)); mask.FillBrush=Own(c.CreateColorBrush(Microsoft.UI.Colors.White)); maskVisual.Shapes.Add(mask);
        maskSurface=Own(c.CreateVisualSurface()); maskSurface.SourceVisual=maskVisual;
        var maskBrush=Own(c.CreateSurfaceBrush(maskSurface));
        shadowVisuals=new SpriteVisual[2];
        float[] blur={45,8}, y={17,2}, opacity={.22f,.084f};
        for(int i=0;i<2;i++) {
            var shadow=Own(c.CreateDropShadow()); shadow.Mask=maskBrush; shadow.Color=Microsoft.UI.Colors.Black;
            shadow.BlurRadius=blur[i];shadow.Offset=new Vector3(0,y[i],0);shadow.Opacity=opacity[i];
            var visual=Own(c.CreateSpriteVisual());visual.Shadow=shadow;visual.Offset=new Vector3(48,48,0);
            shadowVisuals[i]=visual;Shadows.Children.InsertAtTop(visual);
        }
        // 宿主显示前会 Apply 当前参数，不创建马上就要丢弃的默认效果画刷。
        material=Own(c.CreateSpriteVisual());material.Offset=new Vector3(48,48,0);
        clipGeometry=Own(c.CreateRoundedRectangleGeometry());clipGeometry.CornerRadius=new Vector2(20);
        material.Clip=Own(c.CreateGeometricClip(clipGeometry));Root.Children.InsertAtTop(material);
        luminosity=Own(c.CreateSpriteVisual());luminosity.Offset=material.Offset;
        luminosity.Brush=Own(c.CreateColorBrush(dark?Color.FromArgb(97,10,10,12):Color.FromArgb(97,255,255,255)));
        luminosity.Clip=Own(c.CreateGeometricClip(clipGeometry));Root.Children.InsertAtTop(luminosity);
        luminosity.Opacity=solid?0:1;
        tint=Own(c.CreateSpriteVisual());tint.Opacity=solid?0:1;tint.Offset=material.Offset;
        tint.Brush=Own(c.CreateColorBrush(dark?Color.FromArgb(107,28,28,32):Color.FromArgb(107,246,246,248)));
        tint.Clip=Own(c.CreateGeometricClip(clipGeometry));Root.Children.InsertAtTop(tint);
        Edges=Own(c.CreateShapeVisual());Edges.Offset=new Vector3(48,48,0);
        outer=Own(c.CreateRoundedRectangleGeometry());outer.CornerRadius=new Vector2(20.5f);outer.Offset=new Vector2(-.5f);
        var outerShape=Own(c.CreateSpriteShape(outer));outerShape.StrokeThickness=1;
        outerShape.StrokeBrush=Own(c.CreateColorBrush(Color.FromArgb((byte)Math.Round(255*(dark?.315:.07)),0,0,0)));
        Edges.Shapes.Add(outerShape);
        inner=Own(c.CreateRoundedRectangleGeometry());inner.CornerRadius=new Vector2(19.5f);inner.Offset=new Vector2(.5f);
        var gradient=Own(c.CreateLinearGradientBrush());gradient.StartPoint=Vector2.Zero;gradient.EndPoint=new Vector2(0,1);
        float[] stops={0,.3f,.7f,1}, strengths={1,.3f,.1f,.45f};
        for(int i=0;i<4;i++) gradient.ColorStops.Add(Own(c.CreateColorGradientStop(stops[i],Color.FromArgb((byte)Math.Round(255*(dark?.154:.525)*strengths[i]),255,255,255))));
        var innerShape=Own(c.CreateSpriteShape(inner));innerShape.StrokeThickness=1;innerShape.StrokeBrush=gradient;
        Edges.Shapes.Add(innerShape);Root.Children.InsertAtTop(Edges);
    }
    [System.Runtime.InteropServices.DllImport("Pane.Effect.dll")]
    static extern int CreateSaturation(nint source, float amount, int blur, out nint result);
    CompositionBrush SaturatedBackdrop(Compositor compositor, float saturation=1.6f, float blur=0)
    {
        var parameter=new CompositionEffectSourceParameter("Backdrop");
        using var abi=WinRT.MarshalInterface<Windows.Graphics.Effects.IGraphicsEffectSource>.CreateMarshaler(parameter);
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CreateSaturation(abi.ThisPtr,saturation,0,out var ptr));
        try {
            var effect=WinRT.MarshalInterface<Windows.Graphics.Effects.IGraphicsEffect>.FromAbi(ptr);
            nint blurPtr=0;
            if(blur>0) { using var source=WinRT.MarshalInterface<Windows.Graphics.Effects.IGraphicsEffectSource>.CreateMarshaler(effect);System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CreateSaturation(source.ThisPtr,blur,1,out blurPtr));effect=WinRT.MarshalInterface<Windows.Graphics.Effects.IGraphicsEffect>.FromAbi(blurPtr); }
            using var factory=compositor.CreateEffectFactory(effect);
            if(blurPtr!=0)System.Runtime.InteropServices.Marshal.Release(blurPtr);
            var brush=Own(factory.CreateBrush());backdropBrush=Own(compositor.CreateHostBackdropBrush());brush.SetSourceParameter("Backdrop",backdropBrush);
            return brush;
        } finally { System.Runtime.InteropServices.Marshal.Release(ptr); }
    }
    internal bool Attached => material.Parent==Root && material.Brush!=null && material.Clip!=null && tint.Parent==Root && luminosity.Parent==Root && Edges.Parent==Root && Shadows.Parent==Root && shadowVisuals.All(v=>v.Parent==Shadows && v.Shadow!=null) && Root.Opacity>0 && material.Size.X>0;
    internal void Apply(GlassParameters parameters) {
        var g=parameters.Active; bool dark=parameters.Dark, solid=parameters.Solid;
        var old=material.Brush;var oldBackdrop=backdropBrush;backdropBrush=null;material.Brush=solid?Own(c.CreateColorBrush(dark?Color.FromArgb(255,22,22,25):Color.FromArgb(255,249,249,250))):SaturatedBackdrop(c,(float)g.Saturation,(float)g.ExtraBlur);Release(old);Release(oldBackdrop);
        ((CompositionColorBrush)tint.Brush).Color=dark?Color.FromArgb((byte)Math.Round(255*g.TintOpacity),28,28,32):Color.FromArgb((byte)Math.Round(255*g.TintOpacity),246,246,248);
        ((CompositionColorBrush)luminosity.Brush).Color=dark?Color.FromArgb((byte)Math.Round(255*g.LuminosityOpacity),10,10,12):Color.FromArgb((byte)Math.Round(255*g.LuminosityOpacity),255,255,255);
        tint.Opacity=luminosity.Opacity=solid?0:1;
        float radius=(float)parameters.Current.PanelRadius;maskGeometry.CornerRadius=clipGeometry.CornerRadius=new Vector2(radius);outer.CornerRadius=new Vector2(radius+.5f);inner.CornerRadius=new Vector2(Math.Max(0,radius-.5f));
        // 设计（2026-10-28）：深色不画外圈黑线（压在亮色、花哨背景上像一圈黑框），改成整圈都有的淡白高光，和背景分开交给阴影；浅色不变
        ((CompositionColorBrush)((CompositionSpriteShape)Edges.Shapes[0]).StrokeBrush).Color=Color.FromArgb((byte)Math.Round(255*(dark?0:.1)*g.Highlight),0,0,0);
        var gradient=(CompositionLinearGradientBrush)((CompositionSpriteShape)Edges.Shapes[1]).StrokeBrush;float[] strengths=dark?[1,.6f,.45f,.65f]:[1,.3f,.1f,.45f];
        for(int i=0;i<4;i++) gradient.ColorStops[i].Color=Color.FromArgb((byte)Math.Round(255*(dark?.22:.75)*g.Highlight*strengths[i]),255,255,255);
        for(int i=0;i<2;i++){var shadow=(DropShadow)shadowVisuals[i].Shadow;shadow.BlurRadius=(float)(i==0?64*g.Shadow:8);shadow.Offset=new Vector3(0,(float)(i==0?24*g.Shadow:2),0);shadow.Opacity=(float)((i==0?.32:.12)*g.Shadow);}
    }
    public void Dispose(){
        Root.Children.RemoveAll();Shadows.Children.RemoveAll();Edges.Shapes.Clear();maskVisual.Shapes.Clear();
        foreach(var value in owned.AsEnumerable().Reverse())value.Dispose();owned.Clear();
    }
    internal void Resize(Vector2 size)
    {
        Root.Size=size+new Vector2(96,112);Shadows.Size=Root.Size;Edges.Size=size+new Vector2(2);
        maskGeometry.Size=size;maskVisual.Size=size;maskSurface.SourceSize=size;
        foreach(var visual in shadowVisuals) visual.Size=size;
        outer.Size=size+Vector2.One;inner.Size=size-Vector2.One;
        material.Size=size;luminosity.Size=size;tint.Size=size;clipGeometry.Size=size;
    }
}
