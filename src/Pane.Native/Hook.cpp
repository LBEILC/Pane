#define NOMINMAX
#include <windows.h>
#include <shellscalingapi.h>
#include <atomic>
#include <thread>
#include "Capture.h"
#include "PolicyEvents.h"
#include "PressPolicy.h"
#include "ProcessPresence.h"
#include "IdentityCache.h"
#include "HookHealth.h"
#include "HookSafety.h"
#include "ReadSnapshot.h"
#define API extern "C" __declspec(dllexport)
namespace {
struct Event {int kind,x,y,value; intptr_t window; uint32_t pid,dpi;};
std::atomic<uint64_t> version=0;
// 后台缓存用原子字段；首次按下另用完整配置同步确认，不等待服务线程。
std::atomic<intptr_t> pw=0;std::atomic<DWORD> pp=0;
std::atomic<int> px=0,py=0,pdpi=96,pblocked=1,penabled=1,ptimeout=0,psuper=4,phold=400;
std::atomic<uint64_t> policyRevision=1,policyAccepted=0;
std::atomic<bool> policyPending=false;
// 几何字段与原策略共享 version 的发布边界，所有跨线程字段保持原子读写。
std::atomic<intptr_t> pforeground=0;
std::atomic<ULONG_PTR> pclass=0;
std::atomic<int> geometry[9]{};
PolicyGeometry preparingGeometry{};
ReadSnapshot<PressPolicy> pressPolicy;
std::unique_ptr<PressPolicy> preparingPressPolicy;
ReadSnapshot<IdentitySnapshot> identityCache;
std::atomic<DWORD> requestedPid=0;
std::atomic<DWORD> ownIntegrity=0;
// 完整查询只在服务线程执行；回调读取缓存与零等待的进程存活状态。
void refreshIdentities(DWORD pid,POINT point){
 auto next=std::make_unique<IdentitySnapshot>();
 {auto previous=identityCache.read();if(previous)*next=*previous;}
 next->processes.erase(std::remove_if(next->processes.begin(),next->processes.end(),[](const auto& e){return !e->known||!e->process||WaitForSingleObject(e->process,0)!=WAIT_TIMEOUT;}),next->processes.end());
 for(auto id:{pid,requestedPid.exchange(0)})if(id&&!findIdentity(*next,id)){
  if(next->processes.size()>=256)next->processes.erase(next->processes.begin());
  next->processes.push_back(queryProcessIdentity(id));
 }
 // 启动时预热全部显示器，首次跨屏按下也沿用有效 DPI，不依赖目标的 DPI 感知模式。
 if(next->monitors.empty())EnumDisplayMonitors(nullptr,nullptr,[](HMONITOR monitor,HDC,LPRECT,LPARAM data)->BOOL{
  UINT x=96,y=96;if(FAILED(GetDpiForMonitor(monitor,MDT_EFFECTIVE_DPI,&x,&y))||!x)x=96;
  reinterpret_cast<IdentitySnapshot*>(data)->monitors.push_back({monitor,x});return TRUE;
 },reinterpret_cast<LPARAM>(next.get()));
 auto monitor=MonitorFromPoint(point,MONITOR_DEFAULTTONEAREST);bool found=false;
 for(auto& item:next->monitors)if(item.first==monitor){item.second=pressDpi(point);found=true;}
 if(!found)next->monitors.push_back({monitor,pressDpi(point)});
 if(!ownIntegrity)ownIntegrity=processIntegrity(GetCurrentProcess());
 identityCache.publish(std::move(next));
}
#ifndef PANE_TEST_IDENTITY
PressIdentity cachedIdentity(const PolicyGeometry& g,bool){
 PressIdentity value;wchar_t name[256]{};if(GetClassNameW(g.window,name,256))value.windowClass=name;
 auto cache=identityCache.read();auto entry=cache?findIdentity(*cache,g.pid):nullptr;
 if(entry){value.path=entry->path;value.integrityKnown=entry->known&&ownIntegrity!=0;value.higherIntegrity=value.integrityKnown&&entry->integrity>ownIntegrity;}
 if(!value.integrityKnown)requestedPid=g.pid;
 return value;
}
#endif
unsigned cachedDpi(POINT point){auto cache=identityCache.read();auto monitor=MonitorFromPoint(point,MONITOR_DEFAULTTONEAREST);
 if(cache)for(const auto& item:cache->monitors)if(item.first==monitor)return item.second;
 return 96;
}
HWINEVENTHOOK windowEvents=nullptr,foregroundEvents=nullptr;
HDESK testDesktop=nullptr,previousDesktop=nullptr;
std::atomic<bool> running=false,ready=false;bool observe=true;
std::atomic<DWORD> threadId=0;std::thread worker;
std::atomic<int> error=0,unhook=0,overflow=0;
std::atomic<bool> cancelCapture=false;
std::atomic<HANDLE> wakeEvent=nullptr;
constexpr UINT CancelMessage=WM_APP+1;
UINT_PTR captureTimer=0;
HHOOK installedHook=nullptr;HMODULE hookModule=nullptr;
HookSafety safety;
auto& bypass=pane::inputBypass;
std::atomic<uint64_t> safetyEpoch=0;
void tripSafety(int reason);
std::atomic<uint64_t> reinstallCount=0;
constexpr UINT RepairMessage=WM_APP+2;
LRESULT CALLBACK hook(int,WPARAM,LPARAM);
void emit(Event e);
void repairHook(int reason){
 if(safety.tripped)return;
 if(!safety.allow(GetTickCount64(),true)){tripSafety(4);return;}
 // 先安装新钩子再卸旧钩子，保留捕获状态、上下文及定时器。
 auto replacement=SetWindowsHookExW(WH_MOUSE_LL,hook,hookModule,0);
 if(!replacement){error=GetLastError();return;}
 if(installedHook)UnhookWindowsHookEx(installedHook);
 installedHook=replacement;reinstallCount++;
 Event e{};e.kind=8;e.value=reason;emit(e);
}
std::atomic<uint64_t> count=0,total=0,maximum=0;
Event ring[8192];uint64_t ringEpoch[8192]{};std::atomic<unsigned> head=0,tail=0;
pane::Capture capture;Event context{};LARGE_INTEGER frequency{};
void invalidatePolicy(){policyRevision.fetch_add(1);if(!policyPending.exchange(true))if(auto wake=wakeEvent.load())SetEvent(wake);}
void CALLBACK windowEvent(HWINEVENTHOOK,DWORD event,HWND window,LONG object,LONG child,DWORD,DWORD){
 if(observe||!running)return;
 if(window&&(event==EVENT_SYSTEM_FOREGROUND||((event==EVENT_OBJECT_CREATE||event==EVENT_OBJECT_SHOW)&&object==OBJID_WINDOW&&child==CHILDID_SELF))){DWORD pid=0;GetWindowThreadProcessId(window,&pid);if(pid)requestedPid=pid;}
 if(event==EVENT_SYSTEM_FOREGROUND||(window&&object==OBJID_WINDOW&&child==CHILDID_SELF&&
  (event==EVENT_OBJECT_CREATE||event==EVENT_OBJECT_DESTROY||event==EVENT_OBJECT_SHOW||event==EVENT_OBJECT_HIDE||event==EVENT_OBJECT_REORDER||event==EVENT_OBJECT_LOCATIONCHANGE||event==EVENT_OBJECT_STATECHANGE)))invalidatePolicy();
}
PolicyGeometry publishedGeometry(){PolicyGeometry g{};g.window=reinterpret_cast<HWND>(pw.load());g.pid=pp;g.foreground=reinterpret_cast<HWND>(pforeground.load());g.windowClass=pclass;g.rect={geometry[0],geometry[1],geometry[2],geometry[3]};g.monitor={geometry[4],geometry[5],geometry[6],geometry[7]};g.dpi=geometry[8];g.valid=true;return g;}
void emit(Event e){unsigned h=head.load(),t=tail.load(std::memory_order_acquire);unsigned free=(t+8192-h-1)%8192;
 if(e.kind==pane::Point&&free<32)return;
 if(!free){overflow=1;capture.cancelled=true;}else{ring[h]=e;ringEpoch[h]=safetyEpoch.load();head.store((h+1)%8192,std::memory_order_release);}
 if(auto wake=wakeEvent.load())SetEvent(wake);}
void result(pane::Decision d,int x,int y){if(d.signal==pane::Replay&&!safety.allow(GetTickCount64(),false)){tripSafety(3);return;}if(d.signal!=pane::None){auto e=context;e.kind=d.signal;e.x=d.signal==pane::Replay?capture.sx:x;e.y=d.signal==pane::Replay?capture.sy:y;e.value=d.value;for(int n=0;n<d.repeats;n++)emit(e);}}
// 只在捕获期间检查长按与静止超时；空闲线程阻塞在消息队列上。
void updateTimer(){
 if(capture.active&&!captureTimer)captureTimer=SetTimer(nullptr,0,10,nullptr);
 else if(!capture.active&&captureTimer){KillTimer(nullptr,captureTimer);captureTimer=0;}
}
void tripSafety(int reason){
 if(bypass.exchange(true))return;
 safetyEpoch++;safety.tripped=true;capture={};
 if(captureTimer){KillTimer(nullptr,captureTimer);captureTimer=0;}
 Event cancelEvent{};cancelEvent.kind=pane::Cancel;emit(cancelEvent);
 Event diagnostic{};diagnostic.kind=8;diagnostic.value=reason;emit(diagnostic);
}
void cancel(){if(cancelCapture.exchange(false)&&capture.active){capture.cancelled=true;result({false,pane::Cancel,0},capture.sx,capture.sy);}}
void dispatch(const MSG& msg){
 if(msg.message==CancelMessage)cancel();
 if(msg.message==RepairMessage)repairHook(2);
 if(msg.message==WM_TIMER&&captureTimer&&msg.wParam==captureTimer&&!observe){cancel();pane::Input i{};i.kind=pane::Tick;i.time=GetTickCount64();result(capture.feed(i),capture.sx,capture.sy);updateTimer();}
}
LRESULT CALLBACK hook(int code,WPARAM m,LPARAM data){
 // 自身输入在状态机、健康记录和慢回调检测之前直接放行。
 if(code>=0&&(pane::simulatingInput.load()||pane::ownInput(reinterpret_cast<MSLLHOOKSTRUCT*>(data)->dwExtraInfo)))return CallNextHookEx(nullptr,code,m,data);
 LARGE_INTEGER a{},b{};QueryPerformanceCounter(&a);bool swallow=false;
 if(code>=0&&!observe&&running){auto p=reinterpret_cast<MSLLHOOKSTRUCT*>(data);pane::Input i{};i.x=p->pt.x;i.y=p->pt.y;i.time=GetTickCount64();i.tag=p->dwExtraInfo;i.injected=(p->flags&LLMHF_INJECTED)!=0;
 bool valid=true;
 switch(m){case WM_MOUSEMOVE:i.kind=pane::Move;break;case WM_MOUSEWHEEL:i.kind=pane::Wheel;i.wheel=static_cast<short>(HIWORD(p->mouseData));break;
 case WM_RBUTTONDOWN:case WM_RBUTTONUP:i.button=1;i.kind=m==WM_RBUTTONDOWN?pane::Down:pane::Up;break;
 case WM_MBUTTONDOWN:case WM_MBUTTONUP:i.button=2;i.kind=m==WM_MBUTTONDOWN?pane::Down:pane::Up;break;
 case WM_XBUTTONDOWN:case WM_XBUTTONUP:i.button=HIWORD(p->mouseData)==1?4:8;i.kind=m==WM_XBUTTONDOWN?pane::Down:pane::Up;break;
 case WM_LBUTTONDOWN:case WM_LBUTTONUP:i.button=16;i.kind=m==WM_LBUTTONDOWN?pane::Down:pane::Up;break;default:valid=false;}
 if(valid){
 if(!i.injected&&i.kind==pane::Down&&safety.physicalDown()){bypass=false;}
 if(safety.tripped)return CallNextHookEx(nullptr,code,m,data);
 bool deny=false;if(!capture.active){auto v=version.load(std::memory_order_acquire);auto w=pw.load();auto pid=pp.load();auto dpi=pdpi.load();auto x=px.load(),y=py.load();
 bool moved=abs(i.x-x)>8||abs(i.y-y)>8;
 if(moved&&!policyPending.load())invalidatePolicy();
 // WinEvent 为异步通知；首次按下还核对目标、前台、矩形、显示器及 DPI，兜住尚未派发的事件。
 if(i.kind==pane::Down&&!pressPolicy.read()&&!sameGeometry(publishedGeometry(),policyGeometry(p->pt)))invalidatePolicy();
 deny=pblocked.load()!=0||!w||moved||policyRevision.load()!=policyAccepted.load();
 capture.superButton=psuper;capture.hold=phold;capture.enabled=penabled;capture.timeout=ptimeout;capture.dpi=dpi;
 deny=deny||(v&1)||v!=version.load(std::memory_order_acquire);context={0,i.x,i.y,0,w,pid,static_cast<uint32_t>(dpi)};
 // 窗口通知和后台刷新可能落后于首次按下，直接按当前目标与完整配置确认。
 // 未知路径不等于排除；仍遵守暂停、启用按键、全屏与超级面板各自的规则。
 if(i.kind==pane::Down&&!pane::ownInput(i.tag)){
  if(auto settings=pressPolicy.read()){
   auto current=policyGeometry(p->pt);
   auto identity=cachedIdentity(current,!settings->rules.empty());
   auto decision=decidePress(*settings,current,identity);
   capture.enabled=decision.enabled;capture.superButton=decision.superButton;
   capture.timeout=settings->timeout;capture.hold=settings->hold;
   capture.dpi=cachedDpi(p->pt);
   // TODO(design): 未确认权限的冷缓存先放行，避免吞掉无法补发的点击；新进程首个手势可能不启动。
   deny=!current.window||settings->paused||identity.higherIntegrity||!identity.integrityKnown;
   if(requestedPid.load())invalidatePolicy();
   context={0,i.x,i.y,0,reinterpret_cast<intptr_t>(current.window),current.pid,static_cast<uint32_t>(capture.dpi)};
  }
 }
 }
 cancel();auto d=capture.feed(i,deny);swallow=d.swallow;result(d,i.x,i.y);if(safety.tripped)swallow=false;updateTimer();}
 }
 QueryPerformanceCounter(&b);auto elapsed=static_cast<uint64_t>(b.QuadPart-a.QuadPart);count++;total+=elapsed;auto old=maximum.load();while(old<elapsed&&!maximum.compare_exchange_weak(old,elapsed)){}
 if(HookHealth::slow(elapsed,frequency.QuadPart))PostThreadMessageW(threadId,RepairMessage,0,0);
 return swallow?1:CallNextHookEx(nullptr,code,m,data);
}
}
// 仅服务线程写入构造器；发布后快照不可变，旧版本也只由服务线程回收。
API void PanePressPolicyBegin(int enabled,int timeout,int fullscreen,int paused,int superButton,int hold,int superFullscreen){
 preparingPressPolicy=std::make_unique<PressPolicy>();auto& p=*preparingPressPolicy;
 p.enabled=enabled;p.timeout=timeout;p.fullscreen=fullscreen!=0;p.paused=paused!=0;
 p.superButton=superButton;p.hold=hold;p.superFullscreen=superFullscreen!=0;
}
API void PanePressPolicyRule(const wchar_t* path,const wchar_t* windowClass,int disabled,int super){
 if(preparingPressPolicy)preparingPressPolicy->rules.push_back({path?path:L"",windowClass?windowClass:L"",windowClass!=nullptr,disabled!=0,super!=0});
}
API void PanePressPolicyCommit(){
 if(preparingPressPolicy)pressPolicy.publish(std::move(preparingPressPolicy));
}
API void PanePolicy(intptr_t window,uint32_t pid,int x,int y,int dpi,int blocked,int enabled,int timeout){
 version.fetch_add(1);pw=window;pp=pid;px=x;py=y;pdpi=dpi;pblocked=blocked;penabled=enabled;ptimeout=timeout;
 pforeground=reinterpret_cast<intptr_t>(preparingGeometry.foreground);pclass=preparingGeometry.windowClass;auto& g=preparingGeometry;
 geometry[0]=g.rect.left;geometry[1]=g.rect.top;geometry[2]=g.rect.right;geometry[3]=g.rect.bottom;geometry[4]=g.monitor.left;geometry[5]=g.monitor.top;geometry[6]=g.monitor.right;geometry[7]=g.monitor.bottom;geometry[8]=g.dpi;
 version.fetch_add(1,std::memory_order_release);
}
API uint64_t PanePolicyBegin(){policyAccepted=0;policyPending=false;auto revision=policyRevision.load();POINT p{};GetCursorPos(&p);preparingGeometry=policyGeometry(p);refreshIdentities(preparingGeometry.pid,p);return revision;}
API void PanePolicyCommit(uint64_t revision){
 POINT p{px.load(),py.load()};auto current=policyGeometry(p);
 // 没有窗口本身就拒绝捕获；无需为这个稳定状态反复刷新。
 if((sameGeometry(preparingGeometry,current)||(!preparingGeometry.window&&!current.window))&&current.window==reinterpret_cast<HWND>(pw.load())&&current.pid==pp.load())policyAccepted=revision;
 else invalidatePolicy();
}
API int PanePolicyDirty(){return policyRevision.load()!=policyAccepted.load();}
API int PaneWGesturesPresent(){return wgesturesPresent();}
API void PaneSuperPolicy(int button,int hold){psuper=button;phold=hold;}
API int PaneEnterTestDesktop(){
 wchar_t name[80]{};swprintf_s(name,L"Pane028-%lu-%llu",GetCurrentProcessId(),GetTickCount64());
 previousDesktop=GetThreadDesktop(GetCurrentThreadId());testDesktop=CreateDesktopW(name,nullptr,nullptr,0,GENERIC_ALL,nullptr);
 if(!testDesktop)return 0;
 if(SetThreadDesktop(testDesktop))return 1;CloseDesktop(testDesktop);testDesktop=nullptr;return 0;
}
API void PaneLeaveTestDesktop(){if(testDesktop){SetThreadDesktop(previousDesktop);CloseDesktop(testDesktop);testDesktop=nullptr;previousDesktop=nullptr;}}
// 句柄由托管服务持有，钩子停止后才解除关联并释放。
API void PaneSetWakeEvent(HANDLE handle){wakeEvent=handle;}
API int PaneStart(int observeOnly){if(running.exchange(true))return 0;observe=observeOnly!=0;ready=false;error=0;unhook=0;head=tail=0;capture={};count=total=maximum=0;QueryPerformanceFrequency(&frequency);
 POINT point{};if(GetCursorPos(&point))refreshIdentities(policyGeometry(point).pid,point);
 worker=std::thread([]{threadId=GetCurrentThreadId();if(testDesktop&&!SetThreadDesktop(testDesktop)){error=GetLastError();ready=true;return;}MSG msg{};PeekMessageW(&msg,nullptr,0,0,PM_NOREMOVE);GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&hook),&hookModule);
 SetThreadDescription(GetCurrentThread(),L"Pane 鼠标钩子");
 if(!SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_HIGHEST))error=GetLastError();
 installedHook=SetWindowsHookExW(WH_MOUSE_LL,hook,hookModule,0);if(!installedHook)error=GetLastError();
 safety={};bypass=false;reinstallCount=0;captureTimer=0;cancelCapture=false;
 if(!observe){windowEvents=SetWinEventHook(EVENT_OBJECT_CREATE,EVENT_OBJECT_LOCATIONCHANGE,nullptr,windowEvent,0,0,WINEVENT_OUTOFCONTEXT);foregroundEvents=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,nullptr,windowEvent,0,0,WINEVENT_OUTOFCONTEXT);if(!windowEvents||!foregroundEvents)error=GetLastError()?GetLastError():ERROR_INVALID_HOOK_HANDLE;invalidatePolicy();}
 ready=true;
 while(running&&GetMessageW(&msg,nullptr,0,0)>0){if(!running)break;dispatch(msg);TranslateMessage(&msg);DispatchMessageW(&msg);}
 if(windowEvents){UnhookWinEvent(windowEvents);windowEvents=nullptr;}if(foregroundEvents){UnhookWinEvent(foregroundEvents);foregroundEvents=nullptr;}
 if(captureTimer){KillTimer(nullptr,captureTimer);captureTimer=0;} unhook=installedHook?UnhookWindowsHookEx(installedHook):1;installedHook=nullptr;});
 while(!ready)Sleep(1);return error==0;
}
API void PaneStop(){if(!running.exchange(false))return;PostThreadMessageW(threadId,WM_QUIT,0,0);if(worker.joinable())worker.join();}
API int PanePoll(Event* output){for(;;){auto t=tail.load();if(t==head.load(std::memory_order_acquire))return 0;*output=ring[t];auto epoch=ringEpoch[t];tail.store((t+1)%8192,std::memory_order_release);if(output->kind==8||epoch==safetyEpoch.load())return 1;}}
API int PaneOverflow(){return overflow.exchange(0);}
API void PaneCancelCapture(){cancelCapture=true;if(running)PostThreadMessageW(threadId,CancelMessage,0,0);}
API void PaneStats(uint64_t* events,double* mean,double* max,int* hookError,int* unhookOk){*events=count;*mean=count?double(total)/double(count)/frequency.QuadPart*1e6:0;*max=double(maximum)/frequency.QuadPart*1e6;*hookError=error;*unhookOk=unhook;}

API uint64_t PaneReinstallCount(){return reinstallCount;}
