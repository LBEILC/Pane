#include <fstream>
#include <stdexcept>
#include "../../src/Pane.Native/Hook.cpp"
int assertions=0;
void check(bool value,const char* message){assertions++;if(!value)throw std::runtime_error(message);}
void drain(){MSG m{};while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}}
void settle(){auto end=GetTickCount64()+100;while(GetTickCount64()<end){drain();Sleep(1);}}
void publishActual(){auto revision=PanePolicyBegin();POINT point{};GetCursorPos(&point);auto g=policyGeometry(point);PanePolicy(reinterpret_cast<intptr_t>(g.window),g.pid,point.x,point.y,96,0,1,0);PanePolicyCommit(revision);}
void changed(HANDLE signal,const char* name){check(WaitForSingleObject(signal,1000)==WAIT_OBJECT_0,name);check(PanePolicyDirty()!=0,"event marks dirty");settle();publishActual();ResetEvent(signal);check(!PanePolicyDirty(),"refresh acknowledges latest event");}
int main(){HWND a=nullptr,b=nullptr;HANDLE signal=nullptr;try{
 wchar_t desktop[256]{};DWORD length=0;check(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&length)&&wcsncmp(desktop,L"Pane027-",8)==0,"hidden desktop required");
 signal=CreateEventW(nullptr,FALSE,FALSE,nullptr);PaneSetWakeEvent(signal);check(PaneStart(0)!=0,"normal hook starts");
 WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane028EventFixture";check(RegisterClassW(&wc)!=0,"fixture class");
 a=CreateWindowExW(0,wc.lpszClassName,L"fixture-a",WS_POPUP,0,0,400,300,nullptr,nullptr,wc.hInstance,nullptr);check(a!=nullptr,"fixture created");ShowWindow(a,SW_SHOWNOACTIVATE);settle();publishActual();ResetEvent(signal);check(!PanePolicyDirty(),"initial snapshot clean");
 b=CreateWindowExW(0,wc.lpszClassName,L"fixture-b",WS_POPUP,0,0,400,300,nullptr,nullptr,wc.hInstance,nullptr);ShowWindow(b,SW_SHOWNOACTIVATE);changed(signal,"stationary window show notification");
 ShowWindow(b,SW_HIDE);changed(signal,"stationary hide notification");
 SetWindowPos(a,nullptr,0,0,800,600,SWP_NOACTIVATE|SWP_NOZORDER);changed(signal,"geometry notification");
 MONITORINFO info{sizeof(MONITORINFO),{},{},0};GetMonitorInfoW(MonitorFromPoint({0,0},MONITOR_DEFAULTTONEAREST),&info);auto r=info.rcMonitor;SetWindowPos(a,nullptr,r.left,r.top,r.right-r.left,r.bottom-r.top,SWP_NOACTIVATE|SWP_NOZORDER);changed(signal,"fullscreen notification");
 SetForegroundWindow(a);changed(signal,"foreground notification");
 // 真正的窗口查询成本；不调用低级钩子、更不发送输入。
 LARGE_INTEGER f{},start{},end{};QueryPerformanceFrequency(&f);double maximumUs=0,totalUs=0;
 for(int i=0;i<1000;i++){QueryPerformanceCounter(&start);auto g=policyGeometry({0,0});QueryPerformanceCounter(&end);check(g.valid,"geometry available");double us=double(end.QuadPart-start.QuadPart)*1e6/f.QuadPart;totalUs+=us;if(us>maximumUs)maximumUs=us;}
 PaneStop();PaneSetWakeEvent(nullptr);DestroyWindow(b);DestroyWindow(a);CloseHandle(signal);
 std::ofstream out(".codex-runs/028/window-events.txt");out<<"PASS assertions="<<assertions<<"; queryMeanUs="<<totalUs/1000<<"; queryMaxUs="<<maximumUs<<"; realInput=0; hookUninstalled="<<unhook.load()<<"\n";return 0;
 }catch(const std::exception& e){PaneStop();PaneSetWakeEvent(nullptr);if(a)DestroyWindow(a);if(b)DestroyWindow(b);if(signal)CloseHandle(signal);std::ofstream(".codex-runs/028/window-events.txt")<<"FAIL "<<e.what()<<"\n";return 1;}}
