using Pane.Hosting;
using Windows.Foundation;
using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Pane.Hosting;

// 从 008 提取的双 HWND 玻璃宿主；视图内容与材质共享动画时间样本。
public sealed partial class GlassPanel : IDisposable
{
    readonly IPanelView view;
    internal IPanelView View=>view;
    readonly KeyEventHandler keyHandler;
    readonly GlassParameters parameters;
    readonly bool settingsWindow;
    public event EventHandler? Hidden;
    public bool MaterialAttached=>target.Root==scene.Root && scene.Attached && Native.IsWindowVisible(Visual);
    public PanelPlacement Placement {get;set;}
    public Func<bool>? HideOnBlur {get;set;}
    readonly Native.SubclassProc childProc;
    nint childWindow;
    readonly string className;
    readonly double[] spring=((string)GlassParameters.Resource("PaneEaseSpring")).Split(',').Select(x=>double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    readonly double[] smooth=((string)GlassParameters.Resource("PaneEaseSmooth")).Split(',').Select(x=>double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    readonly Native.Proc proc;
    readonly Windows.UI.Composition.Compositor compositor=new();
    readonly Windows.UI.Composition.Desktop.DesktopWindowTarget target;
    readonly DesktopWindowXamlSource island=new();
    readonly SceneB scene;
    readonly Grid root;
    readonly bool interactive;
    readonly bool demo=Environment.GetEnvironmentVariable("PANE_DEMO")=="1";
    internal bool Settled=>!animating&&!resizing&&!rectAnimating;
    internal bool Active=>!disposing&&(Shown||!Settled||pendingFocus);
    internal event Action? TickRequested;
    internal readonly nint Visual,Input;
    internal bool Shown {get;private set;}
    internal nint Previous {get;private set;}
    internal double Width=592,Height=352;
    readonly double minWidth,minHeight;
    readonly bool resizable;
    float dpi=1,scale=1,opacity=1,startScale=1,startOpacity=1;
    int positionX,positionY;
    readonly Stopwatch animation=new();
    bool disposing,focusing,pendingFocus,animating;
    /// <summary>Esc 是否收起窗口（视图没处理时）。二级窗口（文件、翻译、AI 对话）设成 false：Esc 永远不关窗口。</summary>
    public bool EscapeHides {get;set;}=true;
    public GlassPanel(string name, UIElement content, IPanelView view, GlassParameters parameters, bool settingsWindow=false, bool interactive=true, Size? minSize=null)
    {
        this.view=view;this.parameters=parameters;this.settingsWindow=settingsWindow;
        keyHandler=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Escape&&EscapeHides){e.Handled=true;Hide();}};
        Width=view.PreferredSize.Width;Height=view.PreferredSize.Height;
        // 设计改（2026-10-29）：有任务栏按钮的窗口里，设置窗口最小 760 × 520 可拖边框改大小；安装 / 卸载窗口（520 × 480）比这小，就固定大小。
        // 原来一律按 760 × 520，系统把安装窗撑到 760 宽，内容仍按 520 居中，关闭按钮缩在里面。二级窗口给出自己的最小尺寸（minSize），可改大小
        resizable=settingsWindow&&(minSize!=null||Width>=760&&Height>=520);
        minWidth=settingsWindow?minSize?.Width??Math.Min(760,Width):1;minHeight=settingsWindow?minSize?.Height??Math.Min(520,Height):1;
        this.interactive=interactive;proc=WndProc;childProc=ChildWndProc;className="Pane011"+Guid.NewGuid().ToString("N");
        var wc=new Native.WndClass{size=(uint)Marshal.SizeOf<Native.WndClass>(),proc=proc,name=className,instance=Native.GetModuleHandleW(null)};
        Native.RegisterClassExW(ref wc);
        Visual=Native.CreateWindowExW(0x82800A0,wc.name!,name,0x80000000,0,0,688,464,0,0,wc.instance,0);
        Input=Native.CreateWindowExW(settingsWindow?(demo?0x8240000u:0x240000u):interactive?(demo?0x8200080u:0x200080u):0x82800A0u,wc.name!,name,settingsWindow?0x800E0000u:0x80000000u,48,48,592,352,settingsWindow?0:Visual,0,wc.instance,0);
        if(Visual==0||Input==0)throw new System.ComponentModel.Win32Exception();
        if(settingsWindow)AppIcon.Apply(Input);   // 设置窗口在任务栏上有按钮：给它 Pane 的图标，不然是系统默认的程序图标
        Native.SetLayeredWindowAttributes(Visual,0,255,2);if(!interactive)Native.SetLayeredWindowAttributes(Input,0,255,2);
        foreach(var h in new[]{Visual,Input}){int noRound=1,noBorder=-2;Native.DwmSetWindowAttribute(h,33,ref noRound,4);Native.DwmSetWindowAttribute(h,34,ref noBorder,4);}
        int enable=1;Marshal.ThrowExceptionForHR(Native.DwmSetWindowAttribute(Visual,17,ref enable,4));
        dpi=Native.GetDpiForWindow(Input)/96f;target=Native.Target(compositor,Visual);
        bool dark=parameters.Dark;
        if(demo){var theme=Environment.GetEnvironmentVariable("PANE_THEME");if(theme is "Light" or "Dark")dark=theme=="Dark";}
        scene=new SceneB(compositor,dark,parameters.Solid);target.Root=scene.Root;
        island.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Input));
        // 设计改：贴左上角。宿主窗按 Ceiling 取整总比 root 大一点，默认居中会让整片内容随高度动画在 0 / 1 像素之间跳
        root=new Grid{Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),RequestedTheme=dark?ElementTheme.Dark:ElementTheme.Light,TabFocusNavigation=KeyboardNavigationMode.Cycle,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        root.Children.Add(content);root.AddHandler(UIElement.KeyDownEvent,keyHandler,false);/* 设计改：视图处理过的 Esc（如启动器的「先清空输入」）不再收起 */
        root.PointerPressed+=DragPressed;root.PointerMoved+=DragMoved;root.PointerReleased+=DragReleased;root.PointerCaptureLost+=DragCaptureLost;
        island.Content=root;island.SystemBackdrop=new TransparentBackdrop();island.SiteBridge.Show();Layout(0,0);
        childWindow=Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.SiteBridge.WindowId);
        if(settingsWindow)Native.SetWindowSubclass(childWindow,childProc,1,0);
        root.Measure(new Windows.Foundation.Size(Width,Height));root.Arrange(new Windows.Foundation.Rect(0,0,Width,Height));
        parameters.Changed+=ParametersChanged;view.SizeRequested+=ViewSize;view.CloseRequested+=ViewClose;ParametersChanged(null,EventArgs.Empty);
    }
    void ParametersChanged(object? sender, EventArgs args){root.RequestedTheme=parameters.Dark?ElementTheme.Dark:ElementTheme.Light;scene.Apply(parameters);Layout(positionX,positionY);TickRequested?.Invoke();}
    void ViewSize(object? sender, Size size)=>RequestSize(size);
    void ViewClose(object? sender, EventArgs args)=>Hide();
    double targetWidth,targetHeight,velocityWidth,velocityHeight;
    bool resizing;
    readonly Stopwatch resizeClock=new();
    // 设计改（2026-10-29）：改尺寸用临界阻尼弹簧追目标。原来每次请求都从静止重新起一段缓动，
    // 手势提示框连续加箭头时一直在「起步」，框看着停住不动；弹簧换目标时保留当前速度
    public void RequestSize(Size size){targetWidth=Math.Max(minWidth,size.Width);targetHeight=Math.Max(minHeight,size.Height);if(!resizing){velocityWidth=velocityHeight=0;resizeClock.Restart();}resizing=true;TickRequested?.Invoke();}
    void StepResize()
    {
        double dt=Math.Min(0.05,resizeClock.Elapsed.TotalSeconds);resizeClock.Restart();
        double duration=GlassParameters.Token("PaneDurSlow")*parameters.Current.MotionSpeed/1000;
        if(duration<=0.001){Width=targetWidth;Height=targetHeight;resizing=false;LayoutAtAnchor();return;}
        double omega=6.6/duration;   // 临界阻尼约 6.6/ω 走完 99%，和原来的时长相当
        (Width,velocityWidth)=Spring(Width,velocityWidth,targetWidth,omega,dt);(Height,velocityHeight)=Spring(Height,velocityHeight,targetHeight,omega,dt);
        if(Math.Abs(Width-targetWidth)<0.02&&Math.Abs(Height-targetHeight)<0.02&&Math.Abs(velocityWidth)<0.5&&Math.Abs(velocityHeight)<0.5){Width=targetWidth;Height=targetHeight;resizing=false;}
        LayoutAtAnchor();
    }
    static (double,double) Spring(double x,double v,double target,double omega,double dt){double d=x-target,e=Math.Exp(-omega*dt),k=(v+omega*d)*dt;return (target+(d+k)*e,(v-omega*k)*e);}
    internal void Prepare(){Layout(0,0);root.Measure(new Windows.Foundation.Size(Width,Height));root.Arrange(new Windows.Foundation.Rect(0,0,Width,Height));root.UpdateLayout();}
    internal bool Prepared=>root.ActualWidth>0&&root.Children.Count>0;
    public void Show(bool near=false,int px=0,int py=0,bool activate=true)
    {
        if(disposing)return;Previous=Native.GetForegroundWindow();
        if(!near){Native.GetCursorPos(out var cursor);px=cursor.X;py=cursor.Y;}
        near=near||Placement is PanelPlacement.Pointer or PanelPlacement.Tray;
        view.OnShowing(new PanelShowContext(Previous,new Windows.Foundation.Point(px,py),activate&&!demo));
        // 托盘菜单要按最终尺寸算位置，弹出前直接定尺寸，不走改尺寸的弹簧
        if(Placement==PanelPlacement.Tray&&!Shown){var s=view.PreferredSize;Width=targetWidth=Math.Max(minWidth,s.Width);Height=targetHeight=Math.Max(minHeight,s.Height);resizing=false;}
        var point=PlaceForShow(px,py,near);int x=point.X,y=point.Y;
        if(!Shown&&!animating){scale=(float)GlassParameters.Token("PaneEnterScale");opacity=0;}
        Layout(x-(int)(48*dpi),y-(int)(48*dpi));Shown=true;Animate();
        Native.ShowWindow(Visual,4);Native.ShowWindow(Input,4);Native.SetWindowPos(Visual,settingsWindow&&!demo?0:-1,0,0,0,0,0x13);Native.SetWindowPos(Input,settingsWindow&&!demo?0:-1,0,0,0,0,0x13);
        // 两次独立置顶不能保证 owner 与 owned 窗口连续；背景可能插在中间。
        // 显式以输入窗定位视觉窗，并禁止系统联动调整所有者的 Z 序。
        if(!Native.SetWindowPos(Visual,Input,0,0,0,0,0x213))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if(!demo&&activate&&interactive){Activate();pendingFocus=true;}
        outsideButtons=MouseButtonsDown();
    }
    // 设计改（2026-10-30）：从鼠标钩子触发的面板（超级面板长按侧键）不是前台进程发起的，系统会拒绝 SetForegroundWindow，
    // 面板拿不到焦点：点外面不收起、Esc 和数字键也不响应。被拒绝时临时挂到前台窗口线程的输入上再激活一次（常用做法）
    void Activate()
    {
        if(Native.SetForegroundWindow(Input)&&Native.GetForegroundWindow()==Input)return;
        uint mine=GetCurrentThreadId(),theirs=GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
        bool attached=theirs!=0&&theirs!=mine&&AttachThreadInput(mine,theirs,true);
        try{BringWindowToTop(Input);Native.SetForegroundWindow(Input);}
        finally{if(attached)AttachThreadInput(mine,theirs,false);}
    }
    // 保险：面板开着却不在前台时（激活仍被拒绝），在面板外面按下鼠标左 / 右 / 中键也照常收起；只认新按下的那一下
    int outsideButtons;
    static int MouseButtonsDown()=>((GetAsyncKeyState(1)&0x8000)!=0?1:0)|((GetAsyncKeyState(2)&0x8000)!=0?2:0)|((GetAsyncKeyState(4)&0x8000)!=0?4:0);
    void CheckOutsideClick()
    {
        int now=MouseButtonsDown(),pressed=now&~outsideButtons;outsideButtons=now;
        if(pressed==0||settingsWindow||!interactive||demo||dragging||Native.GetForegroundWindow()==Input||!(HideOnBlur?.Invoke()??true))return;
        Native.GetCursorPos(out var p);Native.GetWindowRect(Input,out var r);
        if(p.X<r.Left||p.X>=r.Right||p.Y<r.Top||p.Y>=r.Bottom)Hide();
    }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach,uint to,bool on);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(nint window);
    void Focus(){if(demo||focusing||disposing)return;focusing=true;try{var h=Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.SiteBridge.WindowId);Native.SetFocus(h);island.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.First));}finally{focusing=false;}}
    internal void ShowGrid(int cell)
    {
        // 与背景夹具使用相同显示器，不跟随正在远程操作的鼠标。
        var monitor=MonitorFromPoint(new Point(),2);var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfoW(monitor,ref info);
        Show(true,info.Work.Left,info.Work.Top,false);
        int margin=(int)Math.Ceiling(8*dpi);
        int left=info.Work.Left+margin,top=info.Work.Top+margin;
        int right=Math.Max(left,info.Work.Right-margin-(int)Math.Ceiling(Width*dpi));
        int bottom=Math.Max(top,info.Work.Bottom-margin-(int)Math.Ceiling(Height*dpi));
        Layout(left+(right-left)*(cell%3)/2-(int)(48*dpi),top+(bottom-top)*(cell/3)/2-(int)(48*dpi));
    }
    internal string WindowDiagnostics()
    {
        Native.GetWindowRect(Visual,out var v);Native.GetWindowRect(Input,out var i);
        return $"material={MaterialAttached};opacity={opacity};scale={scale};dpi={dpi};visual=0x{Visual:X};input=0x{Input:X};visualRect={v.Left},{v.Top},{v.Right},{v.Bottom};inputRect={i.Left},{i.Top},{i.Right},{i.Bottom}";
    }
    public void Hide(){if(!Shown)return;Shown=false;pendingFocus=false;minimizing=false;Animate();}
    /// <summary>二级窗口再次展开：没开就打开；已经开着就还原（最小化时）、提到前台并聚焦，位置大小不动。</summary>
    public void BringToFront(bool activate=true)
    {
        if(!Shown||!animating&&!Native.IsWindowVisible(Input)){Show(activate:activate);return;}
        if(minimizing){minimizing=false;Animate();}   // 收起动画还没播完又被叫出来：原地反向
        if(IsIconic(Input))Native.ShowWindow(Input,9);
        view.OnShown();
        if(!demo&&activate&&interactive){Activate();pendingFocus=true;}
    }
    /// <summary>「总在最前」（二级窗口右上角的图钉）。只对有任务栏按钮的窗口有意义，面板本来就在最前。</summary>
    public void SetTopmost(bool on){if(!settingsWindow||demo)return;Native.SetWindowPos(Input,on?-1:-2,0,0,0,0,0x13);Native.SetWindowPos(Visual,Input,0,0,0,0,0x213);}
    public void Toggle(){if(Shown)Hide();else Show();}
    // 设计改（2026-10-29）：出现 / 消失只有玻璃缩放，内容不缩放、只淡入淡出，位置从头到尾不动。
    // 玻璃在系统合成器、内容在 WinUI 合成器，两边永远做不到同一帧：原来每帧分别设缩放值，内容总比玻璃晚一帧左右（3～10 像素）；
    // 改成两边各跑关键帧动画后，内容那边要等 XAML 出第一帧才开始，开头差得更多（13 像素）。所以内容干脆不缩放。
    // 动画用合成器的关键帧（按显示器帧时钟算），不再每帧从这里设值
    void Animate(){startScale=scale;startOpacity=opacity;animation.Restart();animating=true;StartAnimations();TickRequested?.Invoke();}
    void StartAnimations()
    {
        double speed=parameters.Current.MotionSpeed;double[] curve=Showing?spring:smooth;
        float toScale=(float)(Showing?1:GlassParameters.Token("PaneEnterScale")),toOpacity=Showing?1:0;
        var scaleTime=TimeSpan.FromMilliseconds(Math.Max(1,GlassParameters.Token(Showing?"PaneDurSlow":"PaneDurFast")*speed));
        var fadeTime=TimeSpan.FromMilliseconds(Math.Max(1,GlassParameters.Token(Showing?"PaneDurNormal":"PaneDurFast")*speed));
        Vector2 P(double[] c,int i)=>new((float)c[i],(float)c[i+1]);
        {
            var c=compositor;
            var grow=c.CreateVector3KeyFrameAnimation();grow.InsertKeyFrame(0,new(startScale,startScale,1));grow.InsertKeyFrame(1,new(toScale,toScale,1),c.CreateCubicBezierEasingFunction(P(curve,0),P(curve,2)));grow.Duration=scaleTime;
            var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(0,startOpacity);fade.InsertKeyFrame(1,toOpacity,c.CreateCubicBezierEasingFunction(P(smooth,0),P(smooth,2)));fade.Duration=fadeTime;
            scene.Root.StartAnimation("Scale",grow);scene.Root.StartAnimation("Opacity",fade);
        }
        {
            var content=ElementCompositionPreview.GetElementVisual(root);var c=content.Compositor;
            var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(0,startOpacity);fade.InsertKeyFrame(1,toOpacity,c.CreateCubicBezierEasingFunction(P(smooth,0),P(smooth,2)));fade.Duration=fadeTime;
            content.StartAnimation("Opacity",fade);
        }
    }
    void StopAnimations()
    {
        scene.Root.StopAnimation("Scale");scene.Root.StopAnimation("Opacity");
        var content=ElementCompositionPreview.GetElementVisual(root);content.StopAnimation("Opacity");
    }
    internal void Tick()
    {
        if(disposing)return;
        if(pendingFocus&&root.IsLoaded){pendingFocus=false;Focus();}
        if(resizing)StepResize();
        if(deferFrames>0&&--deferFrames==0){if(Shown&&!IsIconic(Input))FollowTo(deferredRect);else ElementCompositionPreview.GetElementVisual(root).Opacity=opacity;}
        if(rectAnimating)StepRect();
        if(Shown&&!animating)CheckOutsideClick();
        if(!animating)return;
        double t=animation.Elapsed.TotalMilliseconds,duration=GlassParameters.Token(Showing?"PaneDurSlow":"PaneDurFast")*parameters.Current.MotionSpeed;
        double a=Ease(Math.Min(1,t/duration),Showing?spring:smooth);
        double b=Ease(Math.Min(1,t/(GlassParameters.Token(Showing?"PaneDurNormal":"PaneDurFast")*parameters.Current.MotionSpeed)),smooth);
        // 这里只跟踪当前值（中途反向时作起点、诊断用），画面由合成器的动画负责
        scale=(float)(startScale+((Showing?1:GlassParameters.Token("PaneEnterScale"))-startScale)*a);opacity=(float)(startOpacity+((Showing?1:0)-startOpacity)*b);
        if(t>=duration&&minimizing){animating=false;minimizing=false;StopAnimations();Layout(positionX,positionY);Native.ShowWindow(Input,6);return;}
        if(t>=duration){animating=false;StopAnimations();Layout(positionX,positionY);if(!Shown){Native.ShowWindow(Input,0);Native.ShowWindow(Visual,0);view.OnHidden();Hidden?.Invoke(this,EventArgs.Empty);}else view.OnShown();}
    }
    static double Ease(double t,double[] curve)=>t<=0?0:t>=1?1:Bezier(t,curve[0],curve[1],curve[2],curve[3]);
    static double Bezier(double t,double x1,double y1,double x2,double y2)
    {
        static double C(double v,double a,double b)=>3*(1-v)*(1-v)*v*a+3*(1-v)*v*v*b+v*v*v;
        double lo=0,hi=1;for(int i=0;i<20;i++){double mid=(lo+hi)/2;if(C(mid,x1,x2)<t)lo=mid;else hi=mid;}return C((lo+hi)/2,y1,y2);
    }
    void Layout(int x,int y)
    {
        positionX=x;positionY=y;
        scene.Resize(new Vector2((float)Width,(float)Height));scene.Root.TransformMatrix=Matrix4x4.CreateScale(dpi,dpi,1);
        scene.Root.CenterPoint=new Vector3(48+(float)Width/2,48+(float)Height/2,0);if(!animating){scene.Root.Scale=new Vector3(scale,scale,1);scene.Root.Opacity=opacity;}
        // 设计改（2026-10-29）：内容窗不跟着动画走。
        // - 出现 / 消失时内容窗不缩放、不挪（原来每帧把内容窗挪到整像素，玻璃却平滑缩放，内容相对玻璃左右跳 1～2 像素；缩放见 StartAnimations）；
        // - 改尺寸时内容一开始就按目标尺寸排好、位置不动，只有玻璃在变，玻璃还没长到的部分先裁掉
        //   （原来每帧改 root 尺寸、居中面板每帧重新取整居中，手势提示框里的箭头跟着左右晃）
        double cw=resizing?targetWidth:Width,ch=resizing?targetHeight:Height;
        double align=Placement==PanelPlacement.Center?.5:0,offX=align*(Width-cw),offY=align*(Height-ch);   // 内容左上角相对玻璃左上角
        root.Width=cw;root.Height=ch;
        var content=ElementCompositionPreview.GetElementVisual(root);content.CenterPoint=new Vector3((float)(Width/2-offX),(float)(Height/2-offY),0);content.Scale=Vector3.One;if(!animating)content.Opacity=opacity;
        if(content.Clip is not Microsoft.UI.Composition.InsetClip clip){clip=content.Compositor.CreateInsetClip();content.Clip=clip;}
        clip.LeftInset=(float)Math.Max(0,-offX);clip.TopInset=(float)Math.Max(0,-offY);clip.RightInset=(float)Math.Max(0,offX+cw-Width);clip.BottomInset=(float)Math.Max(0,offY+ch-Height);
        int w=(int)Math.Ceiling(cw*dpi),h=(int)Math.Ceiling(ch*dpi);
        double ex=x+(48+offX)*dpi,ey=y+(48+offY)*dpi;
        int ix=(int)Math.Round(ex),iy=(int)Math.Round(ey);
        if(align>0&&activeMonitor is {} am)
        {
            // 居中的面板（手势提示框，不可交互、鼠标穿透）：内容窗铺满工作区、永远不挪，内容用边距放在工作区的精确中心。
            // 原来内容窗随目标尺寸挪动：窗口挪动立刻生效、XAML 下一帧才按新宽度重排，加箭头那一帧整片内容会跳 13～23 像素
            ix=am.Work.Left;iy=am.Work.Top;w=am.Work.Right-am.Work.Left;h=am.Work.Bottom-am.Work.Top;
            root.Margin=new Thickness(((am.Work.Right-am.Work.Left)/dpi-cw)/2,((am.Work.Bottom-am.Work.Top)/dpi-ch)/2,0,0);
            MoveWindowsTogether(x,y,ix,iy,w,h);Native.SetWindowRgn(Input,0,false);
        }
        else
        {
            content.Offset=new Vector3((float)((ex-ix)/dpi),(float)((ey-iy)/dpi),0);
            MoveWindowsTogether(x,y,ix,iy,w,h);
            var region=Native.CreateRoundRectRgn(0,0,w+1,h+1,(int)(2*parameters.Current.PanelRadius*dpi),(int)(2*parameters.Current.PanelRadius*dpi));if(Native.SetWindowRgn(Input,region,false)==0)Native.DeleteObject(region);
        }
        island.SiteBridge.MoveAndResize(new RectInt32(0,0,w,h));
    }
    nint ChildWndProc(nint h,uint m,nuint w,nint l,nuint id,nuint data) {
        if(m==0x84 && settingsWindow){var hit=HitTest(l);if(hit!=1)return -1;}
        return Native.DefSubclassProc(h,m,w,l);
    }
    nint HitTest(nint l){
        int sx=(short)((long)l&65535),sy=(short)(((long)l>>16)&65535);Native.GetWindowRect(Input,out var rect);int edge=(int)(8*dpi);
        if(!resizable)return 1;
        bool left=sx<rect.Left+edge,right=sx>=rect.Right-edge,top=sy<rect.Top+edge,bottom=sy>=rect.Bottom-edge;
        if(top)return left?13:right?14:12;if(bottom)return left?16:right?17:15;if(left)return 10;if(right)return 11;
        // 拖动交给未被控件处理的 XAML 指针事件，不把输入框覆盖成系统标题栏。
        return 1;
    }
    nint WndProc(nint h,uint m,nuint w,nint l)
    {
        if(settingsWindow && h==Input && m==0x84)return HitTest(l);
        if(settingsWindow && m==0x83)return 0;
        if(h==Input && m==0x02E0){if(movingWindows)return 0;dpi=(w&0xffff)/96f;var rect=Marshal.PtrToStructure<Native.Rect>(l);Layout(rect.Left-(int)(48*dpi),rect.Top-(int)(48*dpi));return 0;}
        // 设计改（2026-10-30，用户提：设置窗口最小化没有动效）：系统要最小化（任务栏点击、Win+↓）时先播收起动画（同关闭：玻璃缩小、整体淡出），播完再真的最小化；
        // 从最小化还原时播出现动画。手势的「最小化」直接改窗口状态、不经过这条消息，那一下没有收起动画，还原照样有
        if(settingsWindow && h==Input && m==0x112 && ((int)w&0xFFF0)==0xF020 && Shown && parameters.Current.MotionSpeed>0.01){if(!minimizing){minimizing=true;Animate();}return 0;}
        if(settingsWindow && h==Input && m==5){
            if(w==1){iconic=true;Native.ShowWindow(Visual,0);}
            else if(Shown){Native.ShowWindow(Visual,4);Native.SetWindowPos(Visual,Input,0,0,0,0,0x13);
                if(iconic){iconic=false;minimizing=false;if(parameters.Current.MotionSpeed>0.01){scale=(float)GlassParameters.Token("PaneEnterScale");opacity=0;Layout(positionX,positionY);Animate();}}}}
        if(settingsWindow && h==Input && m==6 && (w&0xffff)!=0 && Shown)Native.SetWindowPos(Visual,Input,0,0,0,0,0x13);
        if(settingsWindow && h==Input && m==0x24){var info=Marshal.PtrToStructure<Native.MinMaxInfo>(l);info.MinTrack.X=(int)Math.Ceiling(minWidth*dpi);info.MinTrack.Y=(int)Math.Ceiling(minHeight*dpi);Marshal.StructureToPtr(info,l,false);return 0;}
        if(settingsWindow && h==Input && (m==0x216||m==0x214)){var rect=Marshal.PtrToStructure<Native.Rect>(l);Width=Math.Max(minWidth,(rect.Right-rect.Left)/dpi);Height=Math.Max(minHeight,(rect.Bottom-rect.Top)/dpi);Layout(rect.Left-(int)(48*dpi),rect.Top-(int)(48*dpi));return 1;}
        // 设计改（2026-10-29）：设置窗口被系统或手势改了位置 / 大小（最大化、还原、贴左右半边）时，玻璃窗跟着走；
        // 原来只处理了用户拖边框，手势「最大化」后只有内容窗变大，玻璃留在原地
        if(settingsWindow && h==Input && m==0x47 && !movingWindows && Shown && !IsIconic(Input))FollowInputWindow();
        // 系统要挪 / 改内容窗（最大化、还原、贴边）：先拦下来，窗口暂时不动、内容透明度设 0；等这一帧画出来（两次 Tick 后）再自己放到新位置、
        // 玻璃开始长、内容淡入。直接让系统挪的话，界面线程忙着按新尺寸重排设置页（全屏时约 100 ms），这段时间新位置上露着旧尺寸的内容、玻璃还在原处
        if(settingsWindow && resizable && h==Input && m==0x46 && !movingWindows && Shown && !IsIconic(Input) && parameters.Current.MotionSpeed>0.01){
            var pos=Marshal.PtrToStructure<WindowPos>(l);
            bool move=(pos.Flags&2)==0&&(pos.X!=inputX||pos.Y!=inputY),size=(pos.Flags&1)==0&&(pos.Cx!=inputW||pos.Cy!=inputH);
            if(move||size){   // 最大化 / 还原的这条带 0x8000（系统的「状态改变」标记），照样拦
                int x0=move?pos.X:inputX,y0=move?pos.Y:inputY,w0=size?pos.Cx:inputW,h0=size?pos.Cy:inputH;
                deferredRect=new Native.Rect{Left=x0,Top=y0,Right=x0+w0,Bottom=y0+h0};deferFrames=2;
                pos.Flags|=3;Marshal.StructureToPtr(pos,l,false);
                ElementCompositionPreview.GetElementVisual(root).Opacity=0;TickRequested?.Invoke();
            }
        }
        if(m==0x10){Hide();return 0;}
        if(m==0x14)return 1;
        if(h==Input&&m==6&&(w&0xffff)==0&&Shown&&interactive&&!dragging&&!settingsWindow&&!demo&&l!=Input&&l!=Visual&&!Native.IsChild(Input,l)&&(HideOnBlur?.Invoke()??true))Hide();
        if(h==Input&&m==7&&island!=null)Focus();
        return Native.DefWindowProcW(h,m,w,l);
    }
    void FollowInputWindow()
    {
        // 固定大小的窗口（安装 / 卸载）：不接受最大化，别人改了大小也按原尺寸放回，只跟随位置
        if(!resizable){if(IsZoomed(Input)){Native.ShowWindow(Input,9);return;}Native.GetWindowRect(Input,out var p);if(p.Left!=inputX||p.Top!=inputY||p.Right-p.Left!=inputW||p.Bottom-p.Top!=inputH)Layout(p.Left-(int)(48*dpi),p.Top-(int)(48*dpi));return;}
        Native.GetWindowRect(Input,out var r);
        FollowTo(r);
    }
    Native.Rect deferredRect;
    int deferFrames;
    void FollowTo(Native.Rect r)
    {
        // 最大化：铺满所在显示器的工作区（不让系统按边框外扩到屏幕外）
        if(IsZoomed(Input)){var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(GetMonitorInfoW(MonitorFromWindow(Input,2),ref info))r=info.Work;}
        if(r.Left==inputX&&r.Top==inputY&&r.Right-r.Left==inputW&&r.Bottom-r.Top==inputH){if(!animating&&!rectAnimating&&deferFrames==0)ElementCompositionPreview.GetElementVisual(root).Opacity=opacity;return;}
        // 设计改（2026-10-30，用户提：设置窗口最大化没有动效）：最大化、还原、贴左右半边（系统、手势、Win+方向键都走这里）。
        // 照「出现 / 消失」的分工：内容窗一步放到新位置、只排一次版，内容淡入；玻璃在合成层上从旧位置和大小平滑长到新的
        // （逐帧改真实窗口试过：和系统抢位置会来回跳，每帧重排设置页内容也跟不上，慢玻璃 300 多毫秒）。动效速度为 0 时一步到位
        var from=rectAnimating?rectNow:new Native.Rect{Left=inputX,Top=inputY,Right=inputX+inputW,Bottom=inputY+inputH};
        Width=Math.Max(minWidth,(r.Right-r.Left)/dpi);Height=Math.Max(minHeight,(r.Bottom-r.Top)/dpi);
        rectAnimating=false;Layout(r.Left-(int)(48*dpi),r.Top-(int)(48*dpi));
        if(parameters.Current.MotionSpeed<=0.01||inputW<=0||from.Right<=from.Left)return;
        rectFrom=from;rectTo=r;rectAnimating=true;rectClock.Restart();
        var content=ElementCompositionPreview.GetElementVisual(root);var c=content.Compositor;
        // 前 30% 保持透明：内容按新尺寸重排要几十到一百毫秒，别让旧尺寸的内容在新位置上半透明地露出来
        var fade=c.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(0,0);fade.InsertKeyFrame(0.3f,0);fade.InsertKeyFrame(1,1,c.CreateCubicBezierEasingFunction(new Vector2((float)smooth[0],(float)smooth[1]),new Vector2((float)smooth[2],(float)smooth[3])));
        fade.Duration=TimeSpan.FromMilliseconds(Math.Max(1,GlassParameters.Token("PaneDurNormal")*1.4*parameters.Current.MotionSpeed));content.StartAnimation("Opacity",fade);
        StepRect();TickRequested?.Invoke();
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowPos{public nint Hwnd,After;public int X,Y,Cx,Cy;public uint Flags;}
    bool rectAnimating,minimizing,iconic;
    bool Showing=>Shown&&!minimizing;   // 正在播最小化前的收起动画时按「离开」处理
    Native.Rect rectFrom,rectTo,rectNow;
    readonly Stopwatch rectClock=new();
    // 只动玻璃：玻璃窗先铺到新旧两块的并集（还原时旧的比新的大），合成层里的玻璃按插值的位置和大小画；播完收回最终位置
    void StepRect()
    {
        double t=Math.Min(1,rectClock.Elapsed.TotalMilliseconds/Math.Max(1,GlassParameters.Token("PaneDurNormal")*parameters.Current.MotionSpeed)),a=Ease(t,smooth);
        int L(int f,int to)=>(int)Math.Round(f+(to-f)*a);
        rectNow=new Native.Rect{Left=L(rectFrom.Left,rectTo.Left),Top=L(rectFrom.Top,rectTo.Top),Right=L(rectFrom.Right,rectTo.Right),Bottom=L(rectFrom.Bottom,rectTo.Bottom)};
        if(t>=1){rectAnimating=false;scene.Root.Offset=Vector3.Zero;Layout(positionX,positionY);return;}
        int ul=Math.Min(rectFrom.Left,rectTo.Left),ut=Math.Min(rectFrom.Top,rectTo.Top),ur=Math.Max(rectFrom.Right,rectTo.Right),ub=Math.Max(rectFrom.Bottom,rectTo.Bottom);
        int m=(int)(48*dpi);
        float w=(float)((rectNow.Right-rectNow.Left)/dpi),h=(float)((rectNow.Bottom-rectNow.Top)/dpi);
        scene.Resize(new Vector2(w,h));scene.Root.CenterPoint=new Vector3(48+w/2,48+h/2,0);
        scene.Root.Offset=new Vector3(rectNow.Left-ul,rectNow.Top-ut,0);
        Native.SetWindowPos(Visual,0,ul-m,ut-m,ur-ul+2*m,ub-ut+(int)(112*dpi),0x214);   // 同 MoveWindowsTogether：左右上各留 48，下面多留给阴影
    }
    [DllImport("user32.dll")] static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window,uint flags);
    public void Dispose(){if(disposing)return;disposing=true;if(settingsWindow)Native.RemoveWindowSubclass(childWindow,childProc,1);parameters.Changed-=ParametersChanged;view.SizeRequested-=ViewSize;view.CloseRequested-=ViewClose;Native.ShowWindow(Input,0);Native.ShowWindow(Visual,0);root.RemoveHandler(UIElement.KeyDownEvent,keyHandler);root.PointerPressed-=DragPressed;root.PointerMoved-=DragMoved;root.PointerReleased-=DragReleased;root.PointerCaptureLost-=DragCaptureLost;root.Children.Clear();island.Content=null;island.SystemBackdrop=null;island.Dispose();target.Root=null;target.Dispose();scene.Dispose();compositor.Dispose();Native.DestroyWindow(Input);Native.DestroyWindow(Visual);Native.UnregisterClassW(className,Native.GetModuleHandleW(null));}
    [StructLayout(LayoutKind.Sequential)] internal struct Point{internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo{internal int Size;internal Native.Rect Monitor,Work;internal uint Flags;}
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
}
