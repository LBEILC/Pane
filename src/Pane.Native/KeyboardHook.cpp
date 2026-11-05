#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <thread>
#include "Capture.h"
#include "DoubleTap.h"
#include "PolicyEvents.h"
#include "PressPolicy.h"
#define API extern "C" __declspec(dllexport)
namespace {
std::thread worker;
std::atomic<DWORD> threadId=0;
std::atomic<bool> active=false;
DWORD startError=0;
HWND target=nullptr;UINT message=0;WPARAM generation=0;
unsigned launcher=0,clipboard=0;
pane::DoubleTap recognizer;
bool keyPressed(unsigned vk){return (GetAsyncKeyState(static_cast<int>(vk))&0x8000)!=0;}
bool foregroundFull(){
 PolicyGeometry g{};g.window=GetForegroundWindow();
 MONITORINFO monitor{sizeof(MONITORINFO),{},{},0};
 g.valid=g.window&&GetWindowRect(g.window,&g.rect)&&GetMonitorInfoW(MonitorFromWindow(g.window,MONITOR_DEFAULTTONEAREST),&monitor);
 g.monitor=monitor.rcMonitor;
 wchar_t name[256]{};GetClassNameW(g.window,name,256);
 return pressFullscreen(g,name);
}
LRESULT CALLBACK keyboard(int code,WPARAM kind,LPARAM data){
 if(code>=0){
  auto& event=*reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
  bool down=kind==WM_KEYDOWN||kind==WM_SYSKEYDOWN;
  bool up=kind==WM_KEYUP||kind==WM_SYSKEYUP;
  if(down||up){
   auto key=recognizer.feed(event.vkCode,down,event.time,pane::ownInput(event.dwExtraInfo)||pane::simulatingInput.load()!=0,pane::mouseDownSerial.load()+pane::panelKeySerial.load(),keyPressed);
   unsigned id=key&&key==launcher?1:key&&key==clipboard?2:0;
   if(id&&!foregroundFull())PostMessageW(target,message,generation,static_cast<LPARAM>(id));
  }
 }
 return CallNextHookEx(nullptr,code,kind,data);
}
}
// 仅由宿主线程串行调用；等待发生在安装/卸载路径，回调没有等待。
API void PaneKeyboardStop(){
 if(worker.joinable()){PostThreadMessageW(threadId.load(),WM_QUIT,0,0);worker.join();}
 threadId=0;
}
API int PaneKeyboardStart(HWND window,UINT msg,WPARAM epoch,unsigned first,unsigned second){
 PaneKeyboardStop();
 if(!first&&!second)return 0;
 HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 if(!ready)return static_cast<int>(GetLastError());
 target=window;message=msg;generation=epoch;launcher=first;clipboard=second;recognizer={};startError=0;
 try {worker=std::thread([ready]{
  SetThreadDescription(GetCurrentThread(),L"Pane 双击修饰键");
  MSG event{};PeekMessageW(&event,nullptr,0,0,PM_NOREMOVE);threadId=GetCurrentThreadId();
  HMODULE module=nullptr;
  GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&keyboard),&module);
  auto hook=SetWindowsHookExW(WH_KEYBOARD_LL,keyboard,module,0);
  if(!hook)startError=GetLastError();
  active=hook!=nullptr;SetEvent(ready);
  if(hook){while(GetMessageW(&event,nullptr,0,0)>0){TranslateMessage(&event);DispatchMessageW(&event);}UnhookWindowsHookEx(hook);}
  active=false;
 });}catch(...){CloseHandle(ready);return ERROR_NOT_ENOUGH_MEMORY;}
 WaitForSingleObject(ready,INFINITE);CloseHandle(ready);
 if(startError)PaneKeyboardStop();
 return static_cast<int>(startError);
}
API int PaneKeyboardActive(){return active?1:0;}
