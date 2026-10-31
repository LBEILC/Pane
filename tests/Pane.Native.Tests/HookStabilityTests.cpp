#include <iostream>
#include <fstream>
#include <vector>
#include <algorithm>
#include <stdexcept>
#include "../../src/Pane.Native/Hook.cpp"

int checks=0;
void check(bool value,const char* name){checks++;if(!value)throw std::runtime_error(name);}
LRESULT input(UINT message,int x=20,DWORD flags=0,uintptr_t tag=0){MSLLHOOKSTRUCT p{};p.pt={x,20};p.time=GetTickCount();p.flags=flags;p.dwExtraInfo=tag;return hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&p));}
bool registeredRaw(){UINT n=0;GetRegisteredRawInputDevices(nullptr,&n,sizeof(RAWINPUTDEVICE));return n!=0;}
int main(int argc,char** argv){
 if(argc!=2)return 2;std::ofstream log(argv[1]);
 try{
  wchar_t desktop[128]{};DWORD size=0;
  check(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&size)&&wcsncmp(desktop,L"Pane027-",8)==0,"hidden desktop required");
  PanePressPolicyBegin(1,0,0,0,4,400,0);PanePressPolicyCommit();
  for(int cycle=0;cycle<3;cycle++){
   check(PaneStart(0)!=0,"real hook installation");
   check(installedHook!=nullptr,"native handles present");
   HANDLE thread=OpenThread(THREAD_QUERY_INFORMATION,FALSE,threadId);
   check(thread&&GetThreadPriority(thread)==THREAD_PRIORITY_HIGHEST,"highest hook thread priority");if(thread)CloseHandle(thread);
   UINT countDevices=0;GetRegisteredRawInputDevices(nullptr,&countDevices,sizeof(RAWINPUTDEVICE));
   check(countDevices==0,"no raw input registered");
   check(UnhookWindowsHookEx(installedHook)!=0,"simulate silent loss by removing real hook");
   PostThreadMessageW(threadId,RepairMessage,0,0);
   auto deadline=GetTickCount64()+2000;while(!reinstallCount&&GetTickCount64()<deadline)Sleep(1);
   check(reinstallCount==1,"real hook replacement");
   PaneStop();check(unhook==1&&!installedHook,"clean lifecycle shutdown");
   countDevices=0;GetRegisteredRawInputDevices(nullptr,&countDevices,sizeof(RAWINPUTDEVICE));
   check(countDevices==0,"raw registration removed on shutdown");
  }
  // 以下在同一隐藏桌面直接调用回调；不是系统注入。
  WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane034Fixture";
  RegisterClassW(&wc);auto window=CreateWindowExW(0,wc.lpszClassName,L"",WS_POPUP,0,0,500,400,nullptr,nullptr,wc.hInstance,nullptr);
  check(window!=nullptr,"fixture window");ShowWindow(window,SW_SHOWNOACTIVATE);
  MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}
  refreshIdentities(GetCurrentProcessId(),{20,20});
  running=true;observe=false;capture={};head=tail=0;
  check(input(WM_RBUTTONDOWN)==1,"first direct click captured");
  auto active=capture.active;repairHook(1);
  check(capture.active==active&&active==1,"repair retains in-flight capture");
  check(input(WM_RBUTTONUP)==1&&capture.active==0,"release after repair consumed");
  Event event{};int replays=0;while(PanePoll(&event))replays+=event.kind==pane::Replay;
  check(replays==1,"exactly one replay after repair");
  safety={};head=tail=0;
  // 假边界复现旧过滤器：截断标记未识别，补发被当成新点击。
  const uintptr_t oldTag=0x50414E45303130ULL;
  pane::Capture oldCapture;int oldReplays=0;
  for(int n=0;n<1000;n++)if constexpr(static_cast<ULONG>(oldTag)!=oldTag){
   pane::Input i{};i.kind=pane::Down;i.button=1;oldCapture.feed(i);
   i.kind=pane::Up;oldReplays+=oldCapture.feed(i).signal==pane::Replay;
  }
  check(oldReplays==1000,"old full-width-only filter reproduces feedback");
  // 旧实现按方向记录物理回调，却无条件接收注入 Raw Input。
  DWORD seen=0,pending=1000;bool valid=false;
  check(!valid||static_cast<LONG>(seen-pending)<-100,"old injected raw falsely reports missing callback");
  for(auto tag:{pane::InputTag,oldTag}){
   auto callbacks=count.load();auto repairs=reinstallCount.load();
   check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED,tag)==0,"own down passes");
   check(input(WM_RBUTTONUP,20,LLMHF_INJECTED,tag)==0,"own up passes");
   check(count==callbacks&&reinstallCount==repairs&&!capture.active,"own input has no side effects");
  }
  auto callbacks=count.load();
  {pane::SimulationScope simulation;
   check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED)==0,"sending guard bypasses untagged down");
   check(input(WM_RBUTTONUP,20,LLMHF_INJECTED)==0,"sending guard bypasses untagged up");
  }
  check(count==callbacks&&!capture.active,"sending guard skips recording");
  check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED)==1,"remote short down captured");
  check(input(WM_RBUTTONUP,20,LLMHF_INJECTED)==1,"remote short up captured");
  replays=0;while(PanePoll(&event))replays+=event.kind==pane::Replay;
  check(replays==1,"remote short click requests one replay");
  auto repairs=reinstallCount.load();
  for(int n=0;n<1000;n++){
   input(WM_RBUTTONDOWN,20,LLMHF_INJECTED,static_cast<ULONG>(oldTag));
   input(WM_RBUTTONUP,20,LLMHF_INJECTED,static_cast<ULONG>(oldTag));
   // Raw Input 不再注册，也没有处理路径；即使收到消息也不能触发恢复。
   RAWINPUT raw{};raw.header.dwType=RIM_TYPEMOUSE;
   raw.header.hDevice=n%2?reinterpret_cast<HANDLE>(1):nullptr;
   raw.data.mouse.ulExtraInformation=n%3?static_cast<ULONG>(oldTag):0;
   raw.data.mouse.usButtonFlags=RI_MOUSE_RIGHT_BUTTON_DOWN|RI_MOUSE_RIGHT_BUTTON_UP;
   MSG message{};message.message=WM_INPUT;message.lParam=reinterpret_cast<LPARAM>(&raw);dispatch(message);
  }
  check(!registeredRaw()&&!PanePoll(&event)&&reinstallCount==repairs&&!capture.active,"injected raw and replay cannot feed back");
  check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED)==1,"remote drag starts");
  input(WM_MOUSEMOVE,100,LLMHF_INJECTED);input(WM_RBUTTONUP,100,LLMHF_INJECTED);
  int ends=0;replays=0;while(PanePoll(&event)){ends+=event.kind==pane::End&&event.value==0;replays+=event.kind==pane::Replay;}
  check(ends==1&&replays==0,"remote drag produces gesture without replay");
  HookSafety limit;for(int n=0;n<20;n++)check(limit.allow(1000+n,false),"twenty replay requests allowed");
  check(!limit.allow(1999,false)&&limit.tripped,"twenty-first replay trips");
  check(!limit.allow(5000,false),"time alone cannot recover");
  check(limit.physicalDown()&&limit.allow(5000,false),"physical press recovers");
  limit={};check(limit.allow(0,true)&&limit.allow(1,true)&&limit.allow(2,true)&&!limit.allow(9999,true),"fourth repair trips");
  limit={};check(limit.allow(0,true)&&limit.allow(1,true)&&limit.allow(2,true)&&limit.allow(10000,true),"repair window expires");
  safety={};for(int n=0;n<21;n++)result({false,pane::Replay,1},20,20);
  check(safety.tripped&&bypass&&!capture.active&&!captureTimer,"fuse cancels capture and timer");
  int diagnostics=0;replays=0;while(PanePoll(&event)){diagnostics+=event.kind==8&&event.value==3;replays+=event.kind==pane::Replay;}
  check(diagnostics==1&&replays==0,"one diagnostic and stale replay discarded");
  result({false,pane::Replay,1},20,20);check(!PanePoll(&event),"no repeated fuse diagnostic");
  check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED)==0&&input(WM_RBUTTONUP,20,LLMHF_INJECTED)==0&&safety.tripped,"remote input passes while fused");
  check(input(WM_RBUTTONDOWN)==1&&!safety.tripped&&!bypass,"physical down restores capture");input(WM_RBUTTONUP);while(PanePoll(&event)){}
  safety={};repairs=reinstallCount.load();for(int n=0;n<4;n++)repairHook(2);
  check(reinstallCount==repairs+3&&safety.tripped,"repair storm bounded");
  diagnostics=0;while(PanePoll(&event))diagnostics+=event.kind==8&&event.value==4;
  check(diagnostics==1,"one repair fuse diagnostic");
  input(WM_RBUTTONDOWN);input(WM_RBUTTONUP);while(PanePoll(&event)){}
  safety={};result({false,pane::Replay,1},20,20);tripSafety(3);
  input(WM_RBUTTONDOWN);input(WM_RBUTTONUP);
  replays=0;while(PanePoll(&event))replays+=event.kind==pane::Replay;
  check(replays==1,"recovery cannot revive stale replay");
  safety={};for(int n=0;n<20;n++)safety.allow(GetTickCount64(),false);
  check(input(WM_RBUTTONDOWN,20,LLMHF_INJECTED)==1,"capture before replay limit");
  check(input(WM_RBUTTONUP,20,LLMHF_INJECTED)==0&&safety.tripped,"event that trips fuse is passed through immediately");
  input(WM_RBUTTONDOWN);input(WM_RBUTTONUP);while(PanePoll(&event)){}
  std::vector<double> times;for(int n=0;n<10000;n++){
   safety={};LARGE_INTEGER a{},b{};QueryPerformanceCounter(&a);auto down=input(WM_RBUTTONDOWN);QueryPerformanceCounter(&b);
   if(down!=1)throw std::runtime_error("timed press rejected");
   times.push_back(double(b.QuadPart-a.QuadPart)*1e6/frequency.QuadPart);input(WM_RBUTTONUP);while(PanePoll(&event)){}
  }
  std::sort(times.begin(),times.end());log<<"direct-real-metadata n=10000 medianUs="<<(times[4999]+times[5000])/2<<" p99Us="<<times[9899]<<" maxUs="<<times.back()<<"\n";
  check(!captureTimer,"no idle timer");
  running=false;check(UnhookWindowsHookEx(installedHook)!=0,"final direct hook removed");installedHook=nullptr;
  DestroyWindow(window);UnregisterClassW(wc.lpszClassName,wc.hInstance);
  log<<"assertions="<<checks<<" failures=0 realInput=0 userDesktopHook=0\n";return 0;
 }catch(const std::exception& e){PaneStop();if(installedHook)UnhookWindowsHookEx(installedHook);log<<"FAIL "<<e.what()<<"\n";return 1;}
}
