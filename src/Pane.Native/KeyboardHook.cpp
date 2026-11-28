#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <thread>
#include "Capture.h"
#include "DoubleTap.h"
#include "PolicyEvents.h"
#include "PressPolicy.h"
#include "StartButton.h"
#include "StartKey.h"
#define API extern "C" __declspec(dllexport)
API int PaneWindowFullscreen(HWND window,HMONITOR monitor,const wchar_t* name){
 PolicyGeometry g{};g.window=window;
 readWindowBounds(g,monitor);
 return pressFullscreen(g,name);
}
namespace {
std::thread worker;
std::atomic<DWORD> threadId=0;
std::atomic<bool> active=false;
DWORD startError=0;
HWND target=nullptr;UINT message=0;WPARAM generation=0;
unsigned launcher=0,clipboard=0,words=0,memo=0;
bool pauseInFullscreen=true;
pane::DoubleTap recognizer;
// 代替开始菜单（2026-11-28）：1 = 单按 Win，2 = 点任务栏开始按钮，都按启动器热键处理（开着时收起）。
int startFlags=0;
pane::WinTap winTap;
bool swallowUp=false;
bool keyPressed(unsigned vk){return (GetAsyncKeyState(static_cast<int>(vk))&0x8000)!=0;}
INPUT keyInput(WORD vk,bool up){
 INPUT i{};i.type=INPUT_KEYBOARD;i.ki.wVk=vk;i.ki.dwExtraInfo=pane::InputTag;
 i.ki.dwFlags=(up?KEYEVENTF_KEYUP:0)|(vk==VK_LWIN||vk==VK_RWIN?KEYEVENTF_EXTENDEDKEY:0);
 return i;
}
// 换成「空键按下、松开，再松开 Win」：系统把它当组合键，不弹开始菜单；空键 0xE8 未分配，没有副作用。
void releaseQuietly(unsigned win){
 INPUT inputs[]{keyInput(0xE8,false),keyInput(0xE8,true),keyInput(static_cast<WORD>(win),true)};
 SendInput(3,inputs,sizeof(INPUT));
}
bool foregroundFull(){
 HWND window=GetForegroundWindow();
 wchar_t name[256]{};GetClassNameW(window,name,256);
 return PaneWindowFullscreen(window,MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),name)!=0;
}
LRESULT CALLBACK keyboard(int code,WPARAM kind,LPARAM data){
 if(code>=0){
  auto& event=*reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
  bool down=kind==WM_KEYDOWN||kind==WM_SYSKEYDOWN;
  bool up=kind==WM_KEYUP||kind==WM_SYSKEYUP;
  if(down||up){
   bool own=pane::ownInput(event.dwExtraInfo)||pane::simulatingInput.load()!=0;
   uint64_t serial=pane::mouseDownSerial.load()+pane::panelKeySerial.load();
   auto key=recognizer.feed(event.vkCode,down,event.time,own,serial,keyPressed);
   unsigned id=key&&key==launcher?1:key&&key==clipboard?2:key&&key==words?4:key&&key==memo?0xbfff:0;
   if(id&&(id>=4||!pauseInFullscreen||!foregroundFull()))PostMessageW(target,message,generation,static_cast<LPARAM>(id));
   if((startFlags&1)&&winTap.feed(event.vkCode,down,own,serial,keyPressed(VK_CONTROL)||keyPressed(VK_MENU)||keyPressed(VK_SHIFT))){
    PostMessageW(target,message,generation,1);
    releaseQuietly(event.vkCode);
    return 1;
   }
  }
 }
 return CallNextHookEx(nullptr,code,kind,data);
}
LRESULT CALLBACK mouse(int code,WPARAM kind,LPARAM data){
 if(code>=0){
  auto& event=*reinterpret_cast<MSLLHOOKSTRUCT*>(data);
  if(!pane::ownInput(event.dwExtraInfo)){
   if(kind==WM_LBUTTONDOWN&&pane::StartButtonHit(event.pt)){swallowUp=true;PostMessageW(target,message,generation,1);return 1;}
   if(kind==WM_LBUTTONUP&&swallowUp){swallowUp=false;return 1;}
   // 手势钩子没开时也让按着 Win 点鼠标不算单按。
   if(kind==WM_LBUTTONDOWN||kind==WM_RBUTTONDOWN||kind==WM_MBUTTONDOWN||kind==WM_XBUTTONDOWN)pane::mouseDownSerial.fetch_add(1);
  }
 }
 return CallNextHookEx(nullptr,code,kind,data);
}
}
// 仅由宿主线程串行调用；等待发生在安装/卸载路径，回调没有等待。
API void PaneKeyboardStop(){
 if(worker.joinable()){PostThreadMessageW(threadId.load(),WM_QUIT,0,0);worker.join();}
 threadId=0;
 pane::StartButtonsWatch(false);
}
API int PaneKeyboardStart(HWND window,UINT msg,WPARAM epoch,unsigned first,unsigned second,unsigned third=0,unsigned fourth=0,int pauseWhenFull=1,int start=0){
 PaneKeyboardStop();
 if(!first&&!second&&!third&&!fourth&&!start)return 0;
 HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 if(!ready)return static_cast<int>(GetLastError());
 target=window;message=msg;generation=epoch;launcher=first;clipboard=second;words=third;memo=fourth;recognizer={};startError=0;
 pauseInFullscreen=pauseWhenFull!=0;startFlags=start;winTap={};swallowUp=false;
 pane::StartButtonsWatch((start&2)!=0);
 try {worker=std::thread([ready]{
  SetThreadDescription(GetCurrentThread(),L"Pane 双击修饰键");
  MSG event{};PeekMessageW(&event,nullptr,0,0,PM_NOREMOVE);threadId=GetCurrentThreadId();
  HMODULE module=nullptr;
  GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&keyboard),&module);
  auto hook=SetWindowsHookExW(WH_KEYBOARD_LL,keyboard,module,0);
  if(!hook)startError=GetLastError();
  // 开始按钮的鼠标钩子挂不上时只少这一项，单按 Win 和连按两下照常。
  HHOOK click=hook&&(startFlags&2)?SetWindowsHookExW(WH_MOUSE_LL,mouse,module,0):nullptr;
  active=hook!=nullptr;SetEvent(ready);
  if(hook){while(GetMessageW(&event,nullptr,0,0)>0){TranslateMessage(&event);DispatchMessageW(&event);}UnhookWindowsHookEx(hook);}
  if(click)UnhookWindowsHookEx(click);
  active=false;
 });}catch(...){CloseHandle(ready);return ERROR_NOT_ENOUGH_MEMORY;}
 WaitForSingleObject(ready,INFINITE);CloseHandle(ready);
 if(startError)PaneKeyboardStop();
 return static_cast<int>(startError);
}
API int PaneKeyboardActive(){return active?1:0;}
