#pragma once
// 产品钩子线程不拥有业务窗口；查询不跨线程发送消息、不打开进程、不等待服务线程。
struct PolicyGeometry {
 HWND window{},foreground{};DWORD pid{};RECT rect{},monitor{};int dpi{};bool valid{};ULONG_PTR windowClass{};bool maximized{};
};
inline void readWindowBounds(PolicyGeometry& g,HMONITOR monitor){
 MONITORINFO info{sizeof(MONITORINFO),{},{},0};
 g.valid=g.window&&GetWindowRect(g.window,&g.rect)&&GetMonitorInfoW(monitor,&info);
 g.monitor=info.rcMonitor;
 g.maximized=g.window&&IsZoomed(g.window);
}
inline PolicyGeometry policyGeometry(POINT point){
 PolicyGeometry g{};g.window=GetAncestor(WindowFromPoint(point),GA_ROOT);
 // 与托管目标解析一致，跳过分层且鼠标穿透的覆盖窗口；限制遍历长度。
 int remaining=128;
 while(g.window&&(GetWindowLongPtrW(g.window,GWL_EXSTYLE)&0x80020)==0x80020&&--remaining>0){
  g.window=GetWindow(g.window,GW_HWNDNEXT);RECT r{};
  while(g.window&&--remaining>0&&(!IsWindowVisible(g.window)||!GetWindowRect(g.window,&r)||!PtInRect(&r,point)))g.window=GetWindow(g.window,GW_HWNDNEXT);
 }
 g.foreground=GetForegroundWindow();GetWindowThreadProcessId(g.window,&g.pid);
 if(remaining>0)readWindowBounds(g,MonitorFromPoint(point,MONITOR_DEFAULTTONEAREST));
 g.dpi=static_cast<int>(GetDpiForWindow(g.window));g.windowClass=GetClassLongPtrW(g.window,GCW_ATOM);return g;
}
inline bool sameGeometry(const PolicyGeometry& a,const PolicyGeometry& b){
 return a.valid&&b.valid&&a.window==b.window&&a.foreground==b.foreground&&a.pid==b.pid&&a.dpi==b.dpi&&a.windowClass==b.windowClass
  &&a.maximized==b.maximized&&EqualRect(&a.rect,&b.rect)&&EqualRect(&a.monitor,&b.monitor);
}
