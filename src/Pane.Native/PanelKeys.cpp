#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <thread>
#include <vector>
#include "Capture.h"
#include "PanelKeys.h"
#define API extern "C" __declspec(dllexport)
namespace panelkeys {
constexpr UINT Begin=WM_APP+1,End=WM_APP+2,Replay=WM_APP+3,Stop=WM_APP+4,Drain=WM_APP+5,Arm=WM_APP+6;
std::thread worker;
HWND commandWindow=nullptr,recipient=nullptr;
UINT notification=0;WPARAM generation=0;
HHOOK hook=nullptr;
std::atomic<bool> installed=false,taking=false;
pane::PanelKeys state;
DWORD startError=0;
bool drainPosted=false;
bool replayEnds=false;
bool maskPending=false;
std::array<bool,256> releasedMask{};
WPARAM drainGeneration=0;
// 大缓冲只在消息循环里扩容，钩子回调不建立 INPUT 数组。
std::vector<INPUT> inputs;
bool held(unsigned key){return !releasedMask[key]&&(GetAsyncKeyState(static_cast<int>(key))&0x8000)!=0;}
INPUT input(pane::PanelEvent e){
 INPUT i{};i.type=INPUT_KEYBOARD;i.ki.wVk=static_cast<WORD>(e.vk);i.ki.wScan=static_cast<WORD>(e.scan);
 i.ki.dwFlags=(e.down?0:KEYEVENTF_KEYUP)|(e.extended?KEYEVENTF_EXTENDEDKEY:0);i.ki.dwExtraInfo=pane::InputTag;return i;
}
void retire(){
 taking=state.active;
 if(!state.active&&!state.pending()&&hook){UnhookWindowsHookEx(hook);hook=nullptr;installed=false;}
}
void drain(bool end){
 KillTimer(commandWindow,1);drainPosted=false;maskPending=false;
 inputs.clear();inputs.reserve(state.count+8);
 for(const auto& q:state.queued){
  if(q.mask){inputs.push_back(input({0xE8,0,true}));inputs.push_back(input({0xE8,0,false}));}
  inputs.push_back(input(q.event));
 }
 const auto n=inputs.size();
 // 在发送前清空排队状态；带 InputTag 的重入回调直接放行。
 const bool active=state.active;
 state.replayed();if(!end)state.active=active;
 if(n){
  auto accepted=SendInput(static_cast<UINT>(n),inputs.data(),sizeof(INPUT));
  // 部分发送时只松开已接受且还没被配对松开的键，避免留下逻辑卡键。
  if(accepted<n){
   std::array<bool,256> down{};
   for(UINT j=0;j<accepted;j++)down[inputs[j].ki.wVk]=(inputs[j].ki.dwFlags&KEYEVENTF_KEYUP)==0;
   for(unsigned k=0;k<256;k++)if(down[k]){auto up=input({k,0,false});SendInput(1,&up,sizeof(INPUT));}
  }
 }
 retire();
}
void schedule(){if(!drainPosted){drainPosted=true;PostMessageW(commandWindow,Drain,++drainGeneration,0);}}
LRESULT CALLBACK keyboard(int code,WPARAM kind,LPARAM data){
 if(code<0)return CallNextHookEx(nullptr,code,kind,data);
 const auto& k=*reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
 bool down=kind==WM_KEYDOWN||kind==WM_SYSKEYDOWN;
 if(!down&&kind!=WM_KEYUP&&kind!=WM_SYSKEYUP)return CallNextHookEx(nullptr,code,kind,data);
 bool own=pane::ownInput(k.dwExtraInfo)||pane::simulatingInput.load()!=0;
 if(own)return CallNextHookEx(nullptr,code,kind,data);
 if(down&&k.vkCode<256)releasedMask[k.vkCode]=false;
 pane::PanelModifiers mods{held(VK_LCONTROL)||held(VK_RCONTROL),held(VK_LMENU)||held(VK_RMENU),held(VK_LWIN)||held(VK_RWIN),held(VK_LSHIFT)||held(VK_RSHIFT)};
 mods.altSides=(held(VK_LMENU)?1u:0u)|(held(VK_RMENU)?2u:0u);
 auto e=pane::PanelEvent{k.vkCode,k.scanCode,down,(k.flags&LLKHF_EXTENDED)!=0,false};
 const bool wasTyping=state.typing;
 auto r=state.feed(e,mods,GetTickCount64());
 if(r.mask)releasedMask[k.vkCode]=true;
 if(r.action!=pane::PanelNone)PostMessageW(recipient,notification,generation,static_cast<LPARAM>(r.action|(r.number<<8)));
 if(!wasTyping&&state.typing){
  // 屏蔽松开仍在队列里时若紧接着打字，也必须通知搜索；不能提前把字符重放回原窗口。
  maskPending=false;drainPosted=false;drainGeneration++;
  replayEnds=true;PostMessageW(commandWindow,Arm,generation,0);
 }
 if(r.mask&&!r.swallow){
  // 屏蔽队列与打字队列分开判定；随后导航仍消费，字符仍切搜索，其余事件顺序转发。
  state.enqueue({e,true});maskPending=true;replayEnds=false;schedule();r.swallow=true;
 }else if(maskPending&&!r.swallow){
  state.enqueue({e,false});r.swallow=true;
 }
 if(!state.active&&!state.pending())schedule();
 if(r.swallow&&down)pane::panelKeySerial++;
 return r.swallow?1:CallNextHookEx(nullptr,code,kind,data);
}
struct Request {HWND recipient;UINT message;WPARAM generation;pane::PanelMode mode;};
LRESULT CALLBACK windowProc(HWND h,UINT m,WPARAM w,LPARAM l){
 if(m==Begin){
  if(state.count)drain(true);state.end();
  drainPosted=false;drainGeneration++;
  releasedMask.fill(false);
  auto& r=*reinterpret_cast<Request*>(l);recipient=r.recipient;notification=r.message;generation=r.generation;
  if(!hook){
   HMODULE module=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&keyboard),&module);
   hook=SetWindowsHookExW(WH_KEYBOARD_LL,keyboard,module,0);
   if(!hook){retire();return 0;}
   installed=true;
  }
  std::array<bool,256> pressed{};for(unsigned k:{VK_LMENU,VK_RMENU,VK_LWIN,VK_RWIN})pressed[k]=held(k);
  state.begin(r.mode,pressed);taking=true;return 1;
 }
 if(m==End){state.end();if(state.count)drain(true);retire();return 0;}
 if(m==Replay){if(state.active&&state.typing)drain(true);return 0;}
 if(m==Drain){if(drainPosted&&w==drainGeneration)drain(replayEnds);return 0;}
 if(m==Arm){
  if(state.typing&&replayEnds&&w==generation){
   auto elapsed=GetTickCount64()-state.typingAt;
   if(elapsed>=pane::PanelKeys::ReplayTimeoutMs)drain(true);
   else if(!SetTimer(h,1,static_cast<UINT>(pane::PanelKeys::ReplayTimeoutMs-elapsed),nullptr))drain(true);
  }return 0;
 }
 if(m==WM_TIMER&&w==1){if(state.expired(GetTickCount64()))drain(true);return 0;}
 if(m==Stop){
  state.end();if(state.count)drain(true);
  // 退出不能再等物理松开；仍持有修饰键时先完成屏蔽，不伪造用户的松键。
  bool mask=false;for(unsigned k:{VK_LMENU,VK_RMENU,VK_LWIN,VK_RWIN})mask=mask||(state.maskRelease[k]&&held(k));
  if(mask){INPUT pair[]={input({0xE8,0,true}),input({0xE8,0,false})};SendInput(2,pair,sizeof(INPUT));}
  if(hook)UnhookWindowsHookEx(hook);hook=nullptr;installed=false;taking=false;KillTimer(h,1);PostQuitMessage(0);return 0;
 }
 return DefWindowProcW(h,m,w,l);
}
bool ensure(){
 if(worker.joinable())return commandWindow!=nullptr;
 HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!ready)return false;
 startError=0;
 try{worker=std::thread([ready]{
  SetThreadDescription(GetCurrentThread(),L"Pane 面板按键");
  WNDCLASSW wc{};wc.lpfnWndProc=windowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PanePanelKeyWorker";
  RegisterClassW(&wc);commandWindow=CreateWindowExW(0,wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
  if(!commandWindow)startError=GetLastError();SetEvent(ready);
  if(commandWindow){MSG msg{};while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}DestroyWindow(commandWindow);commandWindow=nullptr;}
  UnregisterClassW(wc.lpszClassName,wc.hInstance);
 });}catch(...){CloseHandle(ready);return false;}
 WaitForSingleObject(ready,INFINITE);CloseHandle(ready);
 if(startError){worker.join();return false;}return true;
}
}
// 宿主 UI 线程串行调用；同步等待仅在控制路径，回调不获取锁。
API int PanePanelKeysBegin(HWND recipient,UINT message,WPARAM generation,int mode){
 using namespace panelkeys;if(!ensure())return 0;Request r{recipient,message,generation,static_cast<pane::PanelMode>(mode)};return static_cast<int>(SendMessageW(commandWindow,Begin,0,reinterpret_cast<LPARAM>(&r)));
}
API void PanePanelKeysEnd(){using namespace panelkeys;if(commandWindow)SendMessageW(commandWindow,End,0,0);}
API void PanePanelKeysReplay(){using namespace panelkeys;if(commandWindow)SendMessageW(commandWindow,Replay,0,0);}
API int PanePanelKeysActive(){return panelkeys::taking?1:0;}
API int PanePanelKeysInstalled(){return panelkeys::installed?1:0;}
API void PanePanelKeysDispose(){
 using namespace panelkeys;if(worker.joinable()){if(commandWindow)SendMessageW(commandWindow,Stop,0,0);worker.join();}state={};
}
