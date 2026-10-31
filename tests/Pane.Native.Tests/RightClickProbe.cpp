#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <fstream>
#include <string>
#include <thread>
#include <mutex>
#include <atomic>
#include "../../src/Pane.Native/Capture.h"

// 设计验收专用。必须显式带 --user-desktop；工程自动测试不运行此入口。
namespace {
std::ofstream trace;
HWND fixture{};ULONGLONG started{};bool inject=false,configured=false;int phase=0,roundNumber=0,down=0,up=0,menus=0;
struct Event {int kind,x,y,value;intptr_t window;unsigned pid,dpi;};
using Start=int(*)(int);using Stop=void(*)();using Poll=int(*)(Event*);using Begin=uint64_t(*)();
using Configure=void(*)(int,int,int,int,int,int,int);using Commit=void(*)();
using Replay=int(*)(int,int,int,unsigned);using Stats=void(*)(uint64_t*,double*,double*,int*,int*);
std::mutex stopMutex;bool stopped=false;HANDLE finished{};unsigned replayCount=0;
Start start{};Stop stop{};Poll poll{};Begin policyBegin{};Configure configure{};Commit commit{};Replay replay{};Stats stats{};
void stopOnce(){std::lock_guard<std::mutex> lock(stopMutex);if(!stopped){stop();stopped=true;}}
template<class T>T load(HMODULE module,const char* name){return reinterpret_cast<T>(GetProcAddress(module,name));}
void write(const char* text){trace<<GetTickCount64()-started<<"ms "<<text<<'\n';trace.flush();}
void settings(bool paused){configure(1,0,0,paused?1:0,0,400,0);commit();}
void sendClick(){
 INPUT inputs[2]{};for(auto& input:inputs)input.type=INPUT_MOUSE;
 inputs[0].mi.dwFlags=MOUSEEVENTF_RIGHTDOWN;inputs[1].mi.dwFlags=MOUSEEVENTF_RIGHTUP;
 UINT sent=SendInput(2,inputs,sizeof(INPUT));trace<<"roundNumber="<<++roundNumber<<" sent="<<sent<<" error="<<GetLastError()<<'\n';
}
LRESULT CALLBACK procedure(HWND window,UINT message,WPARAM w,LPARAM l){
 if(message==WM_KEYDOWN&&w==VK_ESCAPE){DestroyWindow(window);return 0;}
 if(message==WM_CLOSE){DestroyWindow(window);return 0;}
 if(message==WM_DESTROY){PostQuitMessage(0);return 0;}
 if(message==WM_MOUSEACTIVATE){auto result=DefWindowProcW(window,message,w,l);trace<<"mouseActivate="<<result<<'\n';return result;}
 if(message==WM_RBUTTONDOWN||message==WM_RBUTTONUP||message==WM_CONTEXTMENU){
  if(message==WM_RBUTTONDOWN)down++;if(message==WM_RBUTTONUP)up++;if(message==WM_CONTEXTMENU)menus++;
  trace<<"message="<<message<<" replayTag="<<pane::ownInput(static_cast<uintptr_t>(GetMessageExtraInfo()))<<" roundNumber="<<roundNumber<<'\n';trace.flush();
 }
 if(message==WM_TIMER){
  bool focused=GetForegroundWindow()==fixture;
  if(configured!=focused){settings(!focused);configured=focused;write(focused?"fixture-foreground":"fixture-background");}
  policyBegin();Event event{};
  for(int drained=0;drained<256&&poll(&event);drained++){
   if(event.kind==pane::Replay){
    if(++replayCount>60){write("probe-replay-limit");stopOnce();DestroyWindow(window);return 0;}
    trace<<"replayResult="<<replay(event.value,event.x,event.y,event.dpi)<<'\n';
   }
   else if(event.kind==8)trace<<"hookDiagnostic reason="<<event.value<<'\n';
  }
  if(inject&&focused&&roundNumber<30){
   POINT point{200,130};ClientToScreen(fixture,&point);
   if(phase==0){
    int left=GetSystemMetrics(SM_XVIRTUALSCREEN),top=GetSystemMetrics(SM_YVIRTUALSCREEN);
    INPUT move{};move.type=INPUT_MOUSE;move.mi.dwFlags=MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK;
    move.mi.dx=static_cast<LONG>((static_cast<int64_t>(point.x-left)*65536+32768)/GetSystemMetrics(SM_CXVIRTUALSCREEN));
    move.mi.dy=static_cast<LONG>((static_cast<int64_t>(point.y-top)*65536+32768)/GetSystemMetrics(SM_CYVIRTUALSCREEN));
    trace<<"moveSent="<<SendInput(1,&move,sizeof(move))<<'\n';phase=1;
   }else{POINT actual{};GetCursorPos(&actual);
    if(GetAncestor(WindowFromPoint(actual),GA_ROOT)==fixture)sendClick();else write("click-skipped-target-changed");
   }
  }
  if(GetTickCount64()-started>45000||(inject&&roundNumber==30&&GetTickCount64()-started>18000))DestroyWindow(window);
 }
 return DefWindowProcW(window,message,w,l);
}
}
int wmain(int argc,wchar_t** argv){
 if(argc<4)return 2;
 bool limitTest=wcscmp(argv[1],L"--hidden-replay-limit-test")==0;
 bool hiddenTest=limitTest||wcscmp(argv[1],L"--hidden-watchdog-test")==0;
 if(hiddenTest){
  wchar_t desktop[128]{};DWORD bytes=0;
  if(!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&bytes)||wcsncmp(desktop,L"Pane027-",8)!=0)return 2;
 }else if(wcscmp(argv[1],L"--user-desktop")!=0)return 2;
 inject=!hiddenTest&&argc>4&&wcscmp(argv[4],L"--inject")==0;trace.open(argv[3]);if(!trace)return 2;
 HMODULE module=LoadLibraryExW(argv[2],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);if(!module)return 3;
 start=load<Start>(module,"PaneStart");stop=load<Stop>(module,"PaneStop");poll=load<Poll>(module,"PanePoll");policyBegin=load<Begin>(module,"PanePolicyBegin");
 configure=load<Configure>(module,"PanePressPolicyBegin");commit=load<Commit>(module,"PanePressPolicyCommit");replay=load<Replay>(module,"PaneReplayAt");stats=load<Stats>(module,"PaneStats");
 if(!start||!stop||!poll||!policyBegin||!configure||!commit||!replay||!stats){FreeLibrary(module);return 4;}
 started=GetTickCount64();settings(true);
 if(!start(0)){stop();FreeLibrary(module);return 5;}
 WNDCLASSW wc{};wc.lpfnWndProc=procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane034RightClickProbe";wc.hCursor=LoadCursorW(nullptr,MAKEINTRESOURCEW(32512));wc.hbrBackground=GetSysColorBrush(COLOR_WINDOW);
 RegisterClassW(&wc);fixture=CreateWindowExW(0,wc.lpszClassName,L"Pane 034 验收：右键测试区域，Esc 退出，45 秒自动结束",WS_OVERLAPPEDWINDOW,200,200,600,400,nullptr,nullptr,wc.hInstance,nullptr);
 if(!fixture){stop();FreeLibrary(module);return 6;}
 if(!hiddenTest){ShowWindow(fixture,SW_SHOW);SetForegroundWindow(fixture);}SetTimer(fixture,1,500,nullptr);
 // 看门狗不读取 UI 状态，不依赖 WM_TIMER 或排队的关闭消息。
 finished=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 if(!finished){stopOnce();return 8;}
 std::wstring watchdogLog=std::wstring(argv[3])+L".watchdog";
 std::thread watchdog([hiddenTest,limitTest,watchdogLog]{
  if(WaitForSingleObject(finished,hiddenTest&&!limitTest?250:45000)==WAIT_TIMEOUT){
   stopOnce();
   // UI 队列即使饥饿也必须退出；卸钩已同步完成。
   uint64_t events{};double mean{},maximum{};int error{},unhook{};stats(&events,&mean,&maximum,&error,&unhook);
   std::ofstream report(watchdogLog);report<<"watchdog-timeout unhook="<<unhook<<" error="<<error<<"\n";report.close();
   ExitProcess(hiddenTest&&unhook?0:9);
  }
 });
 write(inject?"mode=inject":"mode=manual");
 if(limitTest){
  // 持续补发请求使用假边界；此模式绝不调用 SendInput。
  poll=[](Event* e)->int{*e={};e->kind=pane::Replay;e->value=1;return 1;};
  replay=[](int,int,int,unsigned)->int{return 1;};
  procedure(fixture,WM_TIMER,1,0);
 }else if(hiddenTest)Sleep(INFINITE);
 MSG message{};
 while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
 stopOnce();SetEvent(finished);watchdog.join();CloseHandle(finished);uint64_t events{};double mean{},maximum{};int error{},unhook{};stats(&events,&mean,&maximum,&error,&unhook);
 trace<<"rounds="<<roundNumber<<" down="<<down<<" up="<<up<<" contextMenu="<<menus<<" callbacks="<<events<<" meanUs="<<mean<<" maxUs="<<maximum<<" error="<<error<<" unhook="<<unhook<<'\n';
 UnregisterClassW(wc.lpszClassName,wc.hInstance);FreeLibrary(module);return unhook&&(!limitTest||replayCount==61)?0:7;
}
