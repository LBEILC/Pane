#include <fstream>
#include <iostream>
#include <algorithm>
#include <stdexcept>
#include <vector>
#define NOMINMAX
#include <windows.h>
HWND forcedForeground=nullptr;
unsigned foregroundQueries=0,forwarded=0;
bool directCallback=true;
bool failInstall=false;unsigned installs=0;
unsigned keyStateQueries=0,lastQueriedKey=0;
SHORT keyState=0;
SHORT testKeyState(int vk){keyStateQueries++;lastQueriedKey=static_cast<unsigned>(vk);return keyState;}
bool queryPressed(unsigned vk){return (testKeyState(static_cast<int>(vk))&0x8000)!=0;}
HHOOK testInstall(int id,HOOKPROC proc,HMODULE module,DWORD tid){installs++;if(failInstall){SetLastError(ERROR_ACCESS_DENIED);return nullptr;}return SetWindowsHookExW(id,proc,module,tid);}
HWND testForeground(){foregroundQueries++;return forcedForeground?forcedForeground:GetForegroundWindow();}
LRESULT testNext(HHOOK h,int code,WPARAM w,LPARAM l){forwarded++;return directCallback?12345:CallNextHookEx(h,code,w,l);}
#define GetForegroundWindow testForeground
#define CallNextHookEx testNext
#define SetWindowsHookExW testInstall
#define GetAsyncKeyState testKeyState
#include "../../src/Pane.Native/KeyboardHook.cpp"
#undef GetForegroundWindow
#undef CallNextHookEx
#undef SetWindowsHookExW
#undef GetAsyncKeyState
int checks=0;
void check(bool value,const char* name){++checks;if(!value)throw std::runtime_error(name);}
void unit(){
 using pane::DoubleTap;
 for(unsigned key=0xA0;key<=0xA5;key++){
  DoubleTap s;
  check(!s.feed(key,true,0,false,0),"first down");check(!s.feed(key,false,20,false,0),"first up");
  check(!s.feed(key,true,40,false,0),"second down does not fire");check(s.feed(key,false,60,false,0)==key,"second up fires, six keys");
  check(!s.feed(key,true,80,false,0)&&!s.feed(key,false,100,false,0),"third tap starts new pair");
 }
 for(unsigned other:{0x41u,0xA3u,0x5Bu,0x5Cu})for(unsigned phase:{1u,2u,3u}){
  DoubleTap s;s.feed(0xA2,true,0,false,0);
  if(phase>=2)s.feed(0xA2,false,20,false,0);
  if(phase>=3)s.feed(0xA2,true,40,false,0);
  s.feed(other,true,45,false,0);s.feed(other,false,46,false,0);
  if(phase<2)s.feed(0xA2,false,50,false,0);
  if(phase<3)s.feed(0xA2,true,60,false,0);
  check(!s.feed(0xA2,false,70,false,0),"intervening key invalidates round including Ctrl Ctrl+C");
 }
 for(unsigned key:{0xA0u,0xA2u,0xA4u}){
  DoubleTap s;s.feed(key,true,0,false,0);s.feed(key,false,10,false,0);
  s.feed(key+1,true,20,false,0);check(!s.feed(key+1,false,30,false,0),"left and right never mix");
 }
 for(unsigned part:{1u,2u,3u}){
  DoubleTap s;s.feed(0xA2,true,0,false,0);s.feed(0xA2,false,part==1?301:20,false,0);
  auto second=part==2?421u:320u;s.feed(0xA2,true,second,false,0);
  check(!s.feed(0xA2,false,second+(part==3?301:20),false,0),"long hold or gap");
 }
 {DoubleTap s;s.feed(0xA2,true,0,false,0);s.feed(0xA2,true,10,false,0);s.feed(0xA2,false,20,false,0);s.feed(0xA2,true,40,false,0);s.feed(0xA2,true,50,false,0);check(s.feed(0xA2,false,60,false,0)==0xA2,"auto repeat ignored");}
 {DoubleTap s;s.feed(0xA2,true,0,false,0);s.feed(0xA2,false,300,false,0);s.feed(0xA2,true,700,false,0);check(s.feed(0xA2,false,1000,false,0)==0xA2,"inclusive thresholds");}
 {DoubleTap s;s.feed(0xA2,true,0xFFFFFFF0,false,0);s.feed(0xA2,false,0,false,0);s.feed(0xA2,true,10,false,0);check(s.feed(0xA2,false,20,false,0)==0xA2,"clock wraps");}
 for(unsigned change:{1u,2u,3u}){
  DoubleTap s;s.feed(0xA2,true,0,false,0);s.feed(0xA2,false,20,false,change==1?1:0);
  s.feed(0xA2,true,40,false,change<=2?1:0);check(!s.feed(0xA2,false,60,false,1),"mouse invalidates round");
 }
 {DoubleTap s;for(unsigned t=0;t<4;t++)check(!s.feed(0xA2,t%2==0,t*20,true,0),"own input excluded");}
 {DoubleTap s;s.feed(0xA2,true,0,false,0);s.feed(0xA2,false,20,false,0);s.feed(0x56,true,30,true,0);s.feed(0xA2,true,40,false,0);check(!s.feed(0xA2,false,60,false,0),"own input cancels partial pair");}
 {DoubleTap s;s.feed(0x41,true,0,false,0);s.feed(0xA2,true,10,false,0);s.feed(0xA2,false,20,false,0);s.feed(0xA2,true,30,false,0);check(!s.feed(0xA2,false,40,false,0),"already held chord key blocks");}
 // 模拟切换到安全桌面后收不到 LWin 松开；低位的曾按下标记不代表仍按住。
 for(bool held:{false,true}){
  DoubleTap s;keyStateQueries=0;keyState=held?static_cast<SHORT>(0x8000):1;
  s.feed(VK_LWIN,true,0,false,0,queryPressed);
  check(keyStateQueries==0,"non-modifier down does not query keyboard state");
  check(!s.feed(VK_LCONTROL,true,10,false,0,queryPressed),"reconciled first down never fires");
  check(keyStateQueries==1&&lastQueriedKey==VK_LWIN,"query only marked other key at round start");
  check(s.pressed[VK_LWIN]==held,"stale Win cleared but held Win retained");
  s.feed(VK_LCONTROL,true,11,false,0,queryPressed);
  s.feed(VK_LCONTROL,false,20,false,0,queryPressed);
  check(keyStateQueries==1,"repeat and release do not query keyboard state");
  s.feed(VK_LCONTROL,true,30,false,0,queryPressed);
  auto result=s.feed(VK_LCONTROL,false,40,false,0,queryPressed);
  check(result==(held?0u:static_cast<unsigned>(VK_LCONTROL)),"lost Win release recovers double tap; held Win blocks");
  check(keyStateQueries==(held?2u:1u),"only blocked new rounds query again");
 }
 {DoubleTap s;keyStateQueries=0;
  for(unsigned t=0;t<4;t++)s.feed(VK_LCONTROL,t%2==0,t*20,false,0,queryPressed);
  check(keyStateQueries==0,"normal double tap never queries keyboard state");
 }
 PolicyGeometry g{};g.valid=true;g.rect=g.monitor={0,0,1920,1080};
 check(pressFullscreen(g,L"Game"),"shared fullscreen rule");check(!pressFullscreen(g,L"Progman")&&!pressFullscreen(g,L"WorkerW"),"desktop excluded");
 g.rect.right=1000;check(!pressFullscreen(g,L"Game"),"windowed allowed");
}
unsigned delivered=0,observedKeys=0;bool validCommand=true;
LRESULT CALLBACK windowProc(HWND h,UINT m,WPARAM w,LPARAM l){if(m==WM_APP+54){validCommand=validCommand&&w==7&&l==1;delivered++;return 0;}return DefWindowProcW(h,m,w,l);}
LRESULT CALLBACK observer(int code,WPARAM w,LPARAM l){if(code>=0)observedKeys++;return CallNextHookEx(nullptr,code,w,l);}
void pump(unsigned ms){auto end=GetTickCount64()+ms;MSG m{};do{while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}Sleep(1);}while(GetTickCount64()<end);}
void sendPair(uintptr_t tag=0){INPUT keys[4]{};for(int i=0;i<4;i++){keys[i].type=INPUT_KEYBOARD;keys[i].ki.wVk=VK_LCONTROL;keys[i].ki.dwFlags=i%2?KEYEVENTF_KEYUP:0;keys[i].ki.dwExtraInfo=tag;}SetLastError(0);auto sent=SendInput(4,keys,sizeof(INPUT));if(sent!=4)throw std::runtime_error("SendInput on hidden desktop: sent="+std::to_string(sent)+" error="+std::to_string(GetLastError()));checks++;pump(100);}
int main(int argc,char** argv){
 if(argc<2)return 2;std::ofstream log(argv[1]);HHOOK next=nullptr;HWND window=nullptr;
 try{
  unit();log<<"unit assertions="<<checks<<std::endl;
  wchar_t desktop[128]{};DWORD size=0;check(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&size)&&wcsncmp(desktop,L"Pane027-",8)==0,"hidden desktop required");
  WNDCLASSW wc{};wc.lpfnWndProc=windowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane054Fixture";RegisterClassW(&wc);
  window=CreateWindowExW(0,wc.lpszClassName,L"",WS_POPUP,0,0,500,400,nullptr,nullptr,wc.hInstance,nullptr);check(window!=nullptr,"fixture window");
  check(!PaneKeyboardActive()&&!worker.joinable(),"initial zero hook and zero thread");
  check(PaneKeyboardStart(window,WM_APP+54,7,0,0)==0&&!worker.joinable(),"disabled never starts thread");
  for(int i=0;i<3;i++){
   check(PaneKeyboardStart(window,WM_APP+54,7,VK_LCONTROL,0)==0&&PaneKeyboardActive(),"install real keyboard hook");
   auto handle=OpenThread(SYNCHRONIZE|THREAD_QUERY_INFORMATION,FALSE,threadId);check(handle!=nullptr,"worker handle");
   PaneKeyboardStop();check(!PaneKeyboardActive()&&!worker.joinable()&&WaitForSingleObject(handle,0)==WAIT_OBJECT_0,"uninstall and join real worker");CloseHandle(handle);
  }
  failInstall=true;auto attempts=installs;
  check(PaneKeyboardStart(window,WM_APP+54,7,VK_LCONTROL,0)==ERROR_ACCESS_DENIED,"installation failure returns GetLastError");
  check(installs==attempts+1&&!PaneKeyboardActive()&&!worker.joinable(),"failed installation no retry or thread leak");failInstall=false;
  // 原生回调直接测量；未安装时调用，避免和消息线程共享识别器。
  launcher=VK_LCONTROL;target=window;message=WM_APP+54;generation=7;
  std::vector<double> samples;LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);
  for(unsigned i=0;i<20000;i++){
   KBDLLHOOKSTRUCT e{};e.vkCode=VK_LCONTROL;e.time=i;e.flags=LLKHF_INJECTED;
   LARGE_INTEGER a{},b{};QueryPerformanceCounter(&a);keyboard(0,i%2?WM_KEYUP:WM_KEYDOWN,reinterpret_cast<LPARAM>(&e));QueryPerformanceCounter(&b);
   samples.push_back((b.QuadPart-a.QuadPart)*1000000.0/frequency.QuadPart);
  }
  std::sort(samples.begin(),samples.end());log<<"callback_us median="<<samples[samples.size()/2]<<" p99="<<samples[samples.size()*99/100]<<std::endl;
  pump(10);check(delivered==5000&&validCommand,"foreign injected events recognized by callback with correct command");check(forwarded==20000&&foregroundQueries==5000,"all events forwarded; fullscreen queried only on match");delivered=0;
  for(bool held:{false,true}){
   recognizer={};keyStateQueries=0;keyState=held?static_cast<SHORT>(0x8000):1;
   KBDLLHOOKSTRUCT e{};e.vkCode=VK_LWIN;keyboard(0,WM_KEYDOWN,reinterpret_cast<LPARAM>(&e));
   for(unsigned i=0;i<4;i++){e.vkCode=VK_LCONTROL;e.time=10+i*20;keyboard(0,i%2?WM_KEYUP:WM_KEYDOWN,reinterpret_cast<LPARAM>(&e));}
   pump(1);
   check(delivered==(held?0u:1u),"callback reconciles lost Win release using async high bit");
   check(keyStateQueries==(held?2u:1u)&&lastQueriedKey==VK_LWIN,"callback queries only stale other key at round start");
   delivered=0;
  }
  recognizer={};keyState=0;
  for(auto tag:{pane::InputTag,uintptr_t{0x50414E45303130ULL}}){for(unsigned i=0;i<4;i++){KBDLLHOOKSTRUCT e{};e.vkCode=VK_LCONTROL;e.time=i;e.dwExtraInfo=tag;keyboard(0,i%2?WM_KEYUP:WM_KEYDOWN,reinterpret_cast<LPARAM>(&e));}pump(1);check(delivered==0,"own tags excluded by callback");}
  {pane::SimulationScope simulation;for(unsigned i=0;i<4;i++){KBDLLHOOKSTRUCT e{};e.vkCode=VK_LCONTROL;keyboard(0,i%2?WM_KEYUP:WM_KEYDOWN,reinterpret_cast<LPARAM>(&e));}pump(1);check(delivered==0,"simulation excluded by callback");}
  forcedForeground=window;MONITORINFO monitor{sizeof(MONITORINFO),{},{},0};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor);
  SetWindowPos(window,nullptr,monitor.rcMonitor.left,monitor.rcMonitor.top,monitor.rcMonitor.right-monitor.rcMonitor.left,monitor.rcMonitor.bottom-monitor.rcMonitor.top,SWP_NOACTIVATE);
  for(unsigned i=0;i<4;i++){KBDLLHOOKSTRUCT e{};e.vkCode=VK_LCONTROL;e.time=i;check(keyboard(0,i%2?WM_KEYUP:WM_KEYDOWN,reinterpret_cast<LPARAM>(&e))==12345,"callback always returns downstream value");}pump(1);check(delivered==0,"foreground fullscreen suppresses native dispatch");forcedForeground=nullptr;
  log<<"PASS core assertions="<<checks<<"; keyhook=0; worker=0; clipboard=0"<<std::endl;
  if(argc==2){DestroyWindow(window);return 0;}
  directCallback=false;
  next=SetWindowsHookExW(WH_KEYBOARD_LL,observer,GetModuleHandleW(nullptr),0);check(next!=nullptr,"downstream observation hook");
  check(PaneKeyboardStart(window,WM_APP+54,7,VK_LCONTROL,0)==0,"real input hook");
  sendPair();log<<"sendinput observed="<<observedKeys<<" delivered="<<delivered<<std::endl;
  check(delivered==1&&observedKeys==4,"real injected input recognized and never swallowed");
  sendPair(pane::InputTag);check(delivered==1&&observedKeys==8,"own input forwarded without trigger");
  PaneKeyboardStop();UnhookWindowsHookEx(next);next=nullptr;DestroyWindow(window);window=nullptr;
  log<<"PASS assertions="<<checks<<"; keyhook=0; worker=0; clipboard=0"<<std::endl;return 0;
 }catch(const std::exception& e){PaneKeyboardStop();if(next)UnhookWindowsHookEx(next);if(window)DestroyWindow(window);log<<"FAIL assertions="<<checks<<"; "<<e.what()<<std::endl;return 1;}
}
