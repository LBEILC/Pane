using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pane.Settings;

namespace Pane.Hosting;

public sealed partial class GlassPanel
{
    internal SettingsStore? PositionStore {get;set;}
    /// <summary>打开时放到这个位置（物理像素，玻璃左上角）。二级窗口用来「记住位置」。</summary>
    internal (int X,int Y)? RememberedOrigin {get;set;}
    /// <summary>当前玻璃左上角的物理像素位置，关窗口时由 App 记下。</summary>
    internal (int X,int Y) Origin=>(positionX+(int)(48*dpi),positionY+(int)(48*dpi));
    MonitorArea? activeMonitor;
    double anchorX,anchorY;
    bool dragging,dragMoved,movingWindows;
    uint dragPointer;
    int pressX,pressY;
    double grabX,grabY;
    long lastClick;
    int clickX,clickY;

    sealed record MonitorArea(string Id,Native.Rect Work,float Dpi,Native.Rect Bounds);
    static MonitorArea ReadMonitor(nint monitor)
    {
        var info=new MonitorEx{Size=Marshal.SizeOf<MonitorEx>()};
        if(!ReadMonitorInfo(monitor,ref info))throw new System.ComponentModel.Win32Exception();
        GetDpiForMonitor(monitor,0,out var dx,out _);
        var device=new DisplayDevice{Size=Marshal.SizeOf<DisplayDevice>()};
        // EDD_GET_DEVICE_INTERFACE_NAME 返回显示器设备接口路径，避免仅用 DISPLAY1 编号。
        string id=EnumDisplayDevicesW(info.Device,0,ref device,1)&&!string.IsNullOrEmpty(device.Id)?device.Id:info.Device;
        return new(id,info.Work,(dx==0?96:dx)/96f,info.Monitor);
    }
    static MonitorArea At(int x,int y)=>ReadMonitor(MonitorFromPoint(new Point{X=x,Y=y},2));
    static MonitorArea? FindMonitor(string id)
    {
        MonitorArea? found=null;
        MonitorCallback callback=(nint h,nint dc,ref Native.Rect rect,nint data)=>{var m=ReadMonitor(h);if(m.Id==id)found=m;return true;};
        EnumDisplayMonitors(0,0,callback,0);GC.KeepAlive(callback);return found;
    }
    Point PlaceForShow(int px,int py,bool near)
    {
        var m=At(px,py);
        double x=0,y=0;
        bool restored=false;
        // 二级窗口：这次运行里上一次关掉时的位置（宿主每次打开新建，位置由 App 记着交回来）
        if(!near&&RememberedOrigin is {} origin){m=At(origin.X,origin.Y);x=origin.X;y=origin.Y;restored=true;}
        else if(!near&&PositionStore?.Current.Launcher.Position is {} saved&&FindMonitor(saved.MonitorId) is {} remembered)
        {
            // 启动器从 PreferredSize 的搜索行展开，结果高度不参与保存位置的有效性判断。
            double width=(remembered.Work.Right-remembered.Work.Left)/remembered.Dpi;
            double height=(remembered.Work.Bottom-remembered.Work.Top)/remembered.Dpi;
            if(saved.X>=8&&saved.Y>=8&&saved.X+Width<=width-8&&saved.Y+view.PreferredSize.Height<=height-8)
            {m=remembered;x=m.Work.Left+saved.X*m.Dpi;y=m.Work.Top+saved.Y*m.Dpi;restored=true;}
        }
        dpi=m.Dpi;activeMonitor=m;
        if(!restored)
        {
            x=near?px+12*dpi:m.Work.Left+(m.Work.Right-m.Work.Left-Width*dpi)/2;
            double anchorHeight=PositionStore==null?Height:view.PreferredSize.Height;
            y=near?py+12*dpi:m.Work.Top+(m.Work.Bottom-m.Work.Top-anchorHeight*dpi)/4;
            if(Placement==PanelPlacement.BottomCenter)y=m.Work.Bottom-Height*dpi-8*dpi;
            if(Placement==PanelPlacement.Center){var c=Centered(m);x=c.X;y=c.Y;}
            if(Placement==PanelPlacement.Tray){var t=TrayPlace(m,px,py);x=t.X;y=t.Y;}
        }
        var p=ClampPosition(m,x,y);
        if(PositionStore!=null){anchorX=(x-m.Work.Left)/dpi;anchorY=(y-m.Work.Top)/dpi;}else SetAnchor(m,p);
        return p;
    }
    // 托盘菜单：以鼠标点为一角。鼠标落在工作区外的哪一边，任务栏就在哪一边，菜单往反方向展开；
    // 右边放不下翻到左边；最后由 ClampPosition 夹进工作区（四周留 8）
    (double X,double Y) TrayPlace(MonitorArea m,int px,int py)
    {
        double w=Width*m.Dpi,h=Height*m.Dpi;var work=m.Work;var screen=m.Bounds;
        double x=px,y=py;
        if(x+w>work.Right)x=px-w;
        if(py>=work.Bottom&&work.Bottom<screen.Bottom)y=py-h;
        else if(py<work.Top&&work.Top>screen.Top)y=py;
        else if(px<work.Left&&work.Left>screen.Left)x=px;
        else if(px>=work.Right&&work.Right<screen.Right)x=px-w;
        else if(y+h>work.Bottom)y=py-h;
        return (x,y);
    }
    Point ClampPosition(MonitorArea m,double x,double y)
    {
        int edge=(int)Math.Ceiling(8*m.Dpi);
        return new Point{
            X=(int)Math.Round(Math.Clamp(x,m.Work.Left+edge,Math.Max(m.Work.Left+edge,m.Work.Right-edge-Math.Ceiling(Width*m.Dpi)))),
            Y=(int)Math.Round(Math.Clamp(y,m.Work.Top+edge,Math.Max(m.Work.Top+edge,m.Work.Bottom-edge-Math.Ceiling(Height*m.Dpi))))};
    }
    (double X,double Y) Centered(MonitorArea m)=>(m.Work.Left+(m.Work.Right-m.Work.Left-Width*m.Dpi)/2,m.Work.Top+(m.Work.Bottom-m.Work.Top-Height*m.Dpi)/2);
    void SetAnchor(MonitorArea m,Point p){activeMonitor=m;anchorX=(p.X-m.Work.Left)/m.Dpi;anchorY=(p.Y-m.Work.Top)/m.Dpi;}
    void LayoutAtAnchor()
    {
        if(activeMonitor is not {} m){Layout(positionX,positionY);return;}
        if(Placement==PanelPlacement.Center){var c=Centered(m);SetAnchor(m,ClampPosition(m,c.X,c.Y));}
        var p=ClampPosition(m,m.Work.Left+anchorX*dpi,m.Work.Top+anchorY*dpi);
        Layout(p.X-(int)(48*dpi),p.Y-(int)(48*dpi));
    }
    void MovePanel(int x,int y,MonitorArea m)
    {
        dpi=m.Dpi;var p=ClampPosition(m,x,y);SetAnchor(m,p);LayoutAtAnchor();
    }
    void SavePosition()
    {
        if(PositionStore is not {} store||activeMonitor is not {} m)return;
        store.Save(store.Current with {Launcher=store.Current.Launcher with {Position=new(m.Id,anchorX,anchorY)}});
    }
    internal void SimulateDrag(int dx,int dy)
    {
        if(!Shown)throw new InvalidOperationException("拖动需要显示中的面板"); // 不翻译：异常
        int x=positionX+(int)(48*dpi)+dx,y=positionY+(int)(48*dpi)+dy;
        MovePanel(x,y,At(x,y));SavePosition();
    }
    internal void ResetPosition()
    {
        if(PositionStore is {} store)store.Save(store.Current with {Launcher=store.Current.Launcher with {Position=null}});
        Native.GetCursorPos(out var p);var point=PlaceForShow(p.X,p.Y,Placement==PanelPlacement.Pointer);
        // 按任务许可直接跳回，不重放出现动画和视图 OnShowing。
        Layout(point.X-(int)(48*dpi),point.Y-(int)(48*dpi));
    }
    bool CanDrag(DependencyObject? source)
    {
        if(view.DragRegion is not {} region)return false;
        for(var node=source;node!=null;node=VisualTreeHelper.GetParent(node))
        {
            // 标准控件一律保留交互；纯展示 UserControl（例如图标）仅在不可聚焦时放行。
            if(node is Control c&&(c is not UserControl||c.IsTabStop))return false;
            if(node is TextBlock {IsTextSelectionEnabled:true}||node is RichTextBlock {IsTextSelectionEnabled:true})return false;
            if(node is UIElement e&&(e.CanDrag||e.ManipulationMode!=ManipulationModes.System&&e.ManipulationMode!=ManipulationModes.None))return false;
            if(node==region)return true;
        }
        return false;
    }
    void DragPressed(object sender,PointerRoutedEventArgs e)
    {
        if(dragging||!Settled||!CanDrag(e.OriginalSource as DependencyObject)||!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed)return;
        Native.GetCursorPos(out var p);long now=Environment.TickCount64;
        if(lastClick!=0&&now-lastClick<=GetDoubleClickTime()&&Math.Abs(p.X-clickX)<=GetSystemMetrics(36)&&Math.Abs(p.Y-clickY)<=GetSystemMetrics(37))
        {lastClick=0;ResetPosition();e.Handled=true;return;}
        if(!root.CapturePointer(e.Pointer))return;
        dragging=true;dragMoved=false;dragPointer=e.Pointer.PointerId;pressX=p.X;pressY=p.Y;
        grabX=(p.X-positionX)/dpi-48;grabY=(p.Y-positionY)/dpi-48;e.Handled=true;
    }
    void DragMoved(object sender,PointerRoutedEventArgs e)
    {
        if(!dragging||e.Pointer.PointerId!=dragPointer)return;
        Native.GetCursorPos(out var p);
        if(!dragMoved&&Math.Abs(p.X-pressX)<GetSystemMetrics(68)&&Math.Abs(p.Y-pressY)<GetSystemMetrics(69))return;
        dragMoved=true;lastClick=0;var m=At(p.X,p.Y);
        MovePanel((int)Math.Round(p.X-grabX*m.Dpi),(int)Math.Round(p.Y-grabY*m.Dpi),m);e.Handled=true;
    }
    void EndDrag()
    {
        if(!dragging)return;dragging=false;
        if(dragMoved)SavePosition();else{lastClick=Environment.TickCount64;clickX=pressX;clickY=pressY;}
    }
    void DragReleased(object sender,PointerRoutedEventArgs e)
    {
        if(!dragging||e.Pointer.PointerId!=dragPointer)return;
        DragMoved(sender,e);EndDrag();root.ReleasePointerCapture(e.Pointer);e.Handled=true;
    }
    void DragCaptureLost(object sender,PointerRoutedEventArgs e){EndDrag();}
    int inputX,inputY,inputW,inputH;   // 最近一次放好的内容窗位置，FollowInputWindow 用来判断是不是别人挪的
    void MoveWindowsTogether(int x,int y,int ix,int iy,int w,int h)
    {
        movingWindows=true;inputX=ix;inputY=iy;inputW=w;inputH=h;
        try {
            nint batch=BeginDeferWindowPos(2);
            if(batch!=0)batch=DeferWindowPos(batch,Visual,0,x,y,(int)((Width+96)*dpi),(int)((Height+112)*dpi),0x214);
            if(batch!=0)batch=DeferWindowPos(batch,Input,0,ix,iy,w,h,0x214);
            if(batch==0||!EndDeferWindowPos(batch))throw new System.ComponentModel.Win32Exception();
        }finally{movingWindows=false;}
    }
    internal string PositionDiagnostics()
    {
        var m=activeMonitor!;
        return $"{WindowDiagnostics()};work={m.Work.Left},{m.Work.Top},{m.Work.Right},{m.Work.Bottom};anchor={anchorX},{anchorY};monitor={m.Id}";
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct MonitorEx {public int Size;public Native.Rect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct DisplayDevice {public int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Key;}
    delegate bool MonitorCallback(nint monitor,nint dc,ref Native.Rect rect,nint data);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW",CharSet=CharSet.Unicode)] static extern bool ReadMonitorInfo(nint monitor,ref MonitorEx info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string device,uint number,ref DisplayDevice info,uint flags);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorCallback callback,nint data);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll",SetLastError=true)] static extern nint DeferWindowPos(nint batch,nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool EndDeferWindowPos(nint batch);
}
