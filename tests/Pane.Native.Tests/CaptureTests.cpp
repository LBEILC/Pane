#define NOMINMAX
#include <windows.h>
#include <iostream>
#include <stdexcept>
#include "../../src/Pane.Native/Capture.h"
extern "C" __declspec(dllimport) int PaneWindow(HWND,int);
using namespace pane;
int assertions=0;
void check(bool value,const char* name){assertions++;if(!value)throw std::runtime_error(name);}
Input event(Kind kind,int button=0,int x=0,int y=0,uint64_t t=0){Input i{};i.kind=kind;i.button=button;i.x=x;i.y=y;i.time=t;return i;}
LRESULT CALLBACK proc(HWND h,UINT m,WPARAM w,LPARAM l){return DefWindowProcW(h,m,w,l);}
int main(){try{
 for(int button:{1,2,4,8}){
  Capture c;c.enabled=15;check(c.feed(event(Down,button)).swallow,"trigger down");auto r=c.feed(event(Up,button));check(r.swallow&&r.signal==Replay&&r.value==button,"short replay");
  c.feed(event(Down,button));c.feed(event(Move,0,10,0));r=c.feed(event(Up,button));check(r.signal==End&&r.value==0,"moved no replay");
 }
 for(int mod:{16,2,4,8}){Capture c;c.feed(event(Down,1));auto r=c.feed(event(Down,mod));check(r.swallow&&r.signal==Modifier,"modifier down swallowed");check(c.feed(event(Up,mod)).swallow,"modifier up swallowed");check(c.feed(event(Up,1)).signal==End,"modifier no replay");}
 for(int wheel:{120,-120}){Capture c;c.feed(event(Down,1));auto i=event(Wheel);i.wheel=wheel;auto r=c.feed(i);check(r.swallow&&r.signal==Modifier&&r.value==(wheel>0?1:2),"wheel swallowed");check(c.feed(event(Up,1)).signal==End,"wheel no replay");}
 {Capture c;check(!c.feed(event(Down,2)).swallow,"disabled middle pass");check(!c.feed(event(Down,8)).swallow,"disabled X2 pass");}
 for(int enabled:{1,5}){Capture c;c.enabled=enabled;c.feed(event(Down,4));check(c.feed(event(Tick,0,0,0,400)).signal==Super,"side hold");check(c.feed(event(Up,4)).value==-1,"panel no replay");}
 {Capture c;c.enabled=5;c.feed(event(Down,4));check(c.feed(event(Move,0,30,0,100)).signal==Point,"side gesture");check(c.feed(event(Tick,0,0,0,500)).signal==None,"moving prevents panel");check(c.feed(event(Up,4)).signal==End,"side gesture ends");}
 {Capture c;c.feed(event(Down,4));check(c.feed(event(Move,0,30,0,100)).signal==None,"disabled side no gesture");check(c.feed(event(Up,4)).signal==Replay,"disabled side short replay despite movement");}
 {Capture c;c.feed(event(Down,4));check(c.feed(event(Up,4,0,0,400)).signal==Super,"long release before timer still opens panel");}
 {Capture c;c.feed(event(Down,1));check(c.feed(event(Tick,0,0,0,9000)).signal==None,"timeout default off");c.timeout=1200;check(c.feed(event(Tick,0,0,0,9001)).signal==Cancel,"timeout on");check(c.feed(event(Up,1)).value==-1,"cancel up");}
 for(int reason=0;reason<3;reason++){Capture c;check(!c.feed(event(Down,1),true).swallow,"fullscreen disabled paused pass");}
 {Capture c;auto i=event(Down,1);i.tag=InputTag;check(!c.feed(i).swallow&&!c.active,"own input passes");i.tag=123;i.injected=true;check(c.feed(i).swallow&&c.active==1,"foreign injected recognized");check(c.feed(event(Up,1)).signal==Replay,"foreign input release");}
 {Capture c;c.feed(event(Down,1));c.feed(event(Down,2));c.feed(event(Up,1));check(c.feed(event(Up,2)).swallow,"modifier up after trigger release");}
 {Capture c;c.dpi=192;c.feed(event(Down,1));c.feed(event(Move,0,10,0));check(c.feed(event(Up,1)).signal==Replay,"DPI movement threshold");}
 for(int button:{2,4,8}){Capture c;c.superButton=button;c.hold=650;c.enabled=0;c.feed(event(Down,button,0,0,10));check(c.feed(event(Tick,0,0,0,659)).signal==None,"configured hold before");check(c.feed(event(Tick,0,0,0,660)).signal==Super,"configured trigger and hold");}
 {Capture c;c.superButton=0;c.enabled=0;check(!c.feed(event(Down,4)).swallow,"disabled super passes");}
 // 高精度滚轮只在满一格时发信号，余数不跨手势。
 {Capture c;c.feed(event(Down,1));auto i=event(Wheel);i.wheel=30;
  for(int n=0;n<3;n++){auto r=c.feed(i);check(r.swallow&&r.signal==None,"partial wheel swallowed without signal");}
  auto r=c.feed(i);check(r.signal==Modifier&&r.value==1&&r.repeats==1,"four small deltas make one step");
  i.wheel=90;c.feed(i);i.wheel=-30;check(c.feed(i).signal==None,"reverse clears positive remainder");
  i.wheel=-90;r=c.feed(i);check(r.signal==Modifier&&r.value==2&&r.repeats==1,"reverse accumulates new direction");
  for(int delta:{240,-240,360,-360}){i.wheel=delta;r=c.feed(i);check(r.signal==Modifier&&r.repeats==std::abs(delta)/120&&r.value==(delta>0?1:2),"merged delta yields multiple steps");}
  i.wheel=30;c.feed(i);c.feed(event(Up,1));c.feed(event(Down,1));i.wheel=90;check(c.feed(i).signal==None,"remainder reset for next gesture");
  i.wheel=0;check(c.feed(i).signal==None,"zero wheel has no modifier");
 }
 // 真实窗口操作仅作用于本进程创建、不激活的测试窗口。
 WNDCLASSW wc{};wc.lpfnWndProc=proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane010NativeTest";RegisterClassW(&wc);
 HWND foreground=GetForegroundWindow();HWND h=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,wc.lpszClassName,L"",WS_OVERLAPPEDWINDOW,-30000,-30000,100,100,nullptr,nullptr,wc.hInstance,nullptr);check(h!=nullptr,"create test window");
 ShowWindow(h,SW_SHOWNOACTIVATE);
 wchar_t className[256]{};check(GetClassNameW(h,className,256)>0&&wcscmp(className,wc.lpszClassName)==0,"window class condition feasible");
 DWORD pid=0;GetWindowThreadProcessId(h,&pid);HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);wchar_t path[32768]{};DWORD capacity=32768;
 check(process&&QueryFullProcessImageNameW(process,0,path,&capacity)&&pid==GetCurrentProcessId()&&wcsstr(path,L"CaptureTests.exe"),"full exe path from target window");if(process)CloseHandle(process);
 auto beforeTop=GetWindowLongPtrW(h,GWL_EXSTYLE)&WS_EX_TOPMOST;
 check(PaneWindow(h,3)!=0,"topmost toggle");
 MSG pending{};for(int retry=0;retry<20&&(GetWindowLongPtrW(h,GWL_EXSTYLE)&WS_EX_TOPMOST)==beforeTop;retry++){while(PeekMessageW(&pending,nullptr,0,0,PM_REMOVE)){TranslateMessage(&pending);DispatchMessageW(&pending);}Sleep(10);}
 auto afterTop=GetWindowLongPtrW(h,GWL_EXSTYLE)&WS_EX_TOPMOST;
 if(afterTop==beforeTop)std::cerr<<"topmost before="<<beforeTop<<" after="<<afterTop<<" error="<<GetLastError()<<"\n";
 check(afterTop!=beforeTop,"topmost state toggled");check(PaneWindow(h,3)!=0,"topmost toggle back");check((GetWindowLongPtrW(h,GWL_EXSTYLE)&WS_EX_TOPMOST)==beforeTop,"topmost state restored");
 check(PaneWindow(h,4)!=0,"snap left");RECT left{};GetWindowRect(h,&left);check(PaneWindow(h,5)!=0,"snap right");RECT right{};GetWindowRect(h,&right);check(right.left>left.left,"snap positions");
 check(PaneWindow(h,0)!=0&&IsZoomed(h),"maximize");check(PaneWindow(h,4)!=0&&!IsZoomed(h),"snap from maximized");check(PaneWindow(h,0)!=0&&IsZoomed(h),"maximize again");check(PaneWindow(h,0)!=0&&!IsZoomed(h),"restore");check(PaneWindow(h,1)!=0,"minimize");
 MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}check(IsIconic(h)!=0,"minimized state");
 check(PaneWindow(h,2)!=0,"close");while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}check(!IsWindow(h),"closed state");
 check(GetForegroundWindow()==foreground,"foreground unchanged");UnregisterClassW(wc.lpszClassName,wc.hInstance);
 std::cout<<"PASS native assertions="<<assertions<<"; input=0; clipboard=0; volume=0\n";return 0;
 }catch(const std::exception& e){std::cerr<<"FAIL "<<e.what()<<"\n";return 1;}}
