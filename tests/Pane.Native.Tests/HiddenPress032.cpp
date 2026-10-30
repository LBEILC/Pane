#include <algorithm>
#include <fstream>
#include <stdexcept>
#include <vector>
#ifdef PANE_BEFORE
#include "../../.codex-runs/032/before/Hook.cpp"
#else
#include "../../src/Pane.Native/Hook.cpp"
#endif

// 隐藏桌面只能验证安装及直接调用；绝不把假事件记为系统投递。
std::ofstream output;
int checks=0,failures=0;
void require(bool ok,const char* text){if(!ok)throw std::runtime_error(text);}
void check(bool ok){checks++;if(!ok)failures++;}
void pump(){MSG m{};while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}}
void reset(){capture={};updateTimer();head=tail=0;}
LRESULT raw(WPARAM message,int x,DWORD flags){MSLLHOOKSTRUCT p{};p.pt={x,20};p.flags=flags;return hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&p));}
void publish(int x=20){auto revision=PanePolicyBegin();preparingGeometry=policyGeometry({x,20});auto g=preparingGeometry;PanePolicy(reinterpret_cast<intptr_t>(g.window),g.pid,x,20,96,0,1,0);PanePolicyCommit(revision);}
void configure(bool rules){
#ifndef PANE_BEFORE
 PanePressPolicyBegin(1,0,0,0,4,400,0);
 if(rules){wchar_t path[32768]{};GetModuleFileNameW(nullptr,path,32768);PanePressPolicyRule(path,nullptr,0,0);}
 PanePressPolicyCommit();
#else
 (void)rules;
#endif
}
void gesture(const char* scenario,int delay,DWORD flags){
 auto swallowed=raw(WM_RBUTTONDOWN,60,flags);if(delay)Sleep(delay);
 raw(WM_MOUSEMOVE,160,flags);raw(WM_RBUTTONUP,160,flags);
 Event e{};std::vector<int> events;while(PanePoll(&e))events.push_back(e.kind);
 bool sequence=events==std::vector<int>{pane::Begin,pane::Point,pane::End};
 check(swallowed==1);check(sequence);
 output<<scenario<<" delayMs="<<delay<<" flags="<<flags<<" swallowed="<<swallowed<<" sequence="<<sequence<<"\n";reset();
}
int main(int argc,char** argv){
 if(argc!=2)return 2;output.open(argv[1]);HWND a=nullptr,b=nullptr;HHOOK installed=nullptr;
 try{
  wchar_t own[256]{},input[256]{};DWORD needed=0;
  require(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,own,sizeof(own),&needed)&&wcsncmp(own,L"Pane027-",8)==0,"hidden desktop required");
  auto active=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);
  if(active){GetUserObjectInformationW(active,UOI_NAME,input,sizeof(input),&needed);CloseDesktop(active);}
  output<<"inputDesktopReadable="<<(input[0]!=0)<<" sameAsInput="<<(input[0]&&wcscmp(own,input)==0)<<"; SendInputCalls=0; systemDeliveredCallbacks=0\n";
  require(!input[0]||wcscmp(own,input)!=0,"test desktop must remain hidden");
  WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane032HiddenFixture";
  require(RegisterClassW(&wc)!=0,"class registration");
  a=CreateWindowExW(0,wc.lpszClassName,L"fixture",WS_POPUP,0,0,400,300,nullptr,nullptr,wc.hInstance,nullptr);
  require(a!=nullptr,"window creation");ShowWindow(a,SW_SHOWNOACTIVATE);pump();
  require(policyGeometry({60,20}).window==a,"real window under synthetic point");
  QueryPerformanceFrequency(&frequency);running=true;observe=false;configure(false);
  installed=SetWindowsHookExW(WH_MOUSE_LL,hook,GetModuleHandleW(nullptr),0);require(installed!=nullptr,"real hook installation");
  for(DWORD flags:{DWORD(0),DWORD(LLMHF_INJECTED)}){
   publish(60);LARGE_INTEGER start{},end{};QueryPerformanceCounter(&start);
   b=CreateWindowExW(WS_EX_TOPMOST,wc.lpszClassName,L"new",WS_POPUP,0,0,400,300,nullptr,nullptr,wc.hInstance,nullptr);
   require(b!=nullptr,"new window");ShowWindow(b,SW_SHOWNOACTIVATE);
   require(policyGeometry({60,20}).window==b,"new real target");QueryPerformanceCounter(&end);
   output<<"createAndShowUs="<<double(end.QuadPart-start.QuadPart)*1e6/frequency.QuadPart<<"\n";
   gesture("new-window",0,flags);DestroyWindow(b);b=nullptr;pump();
   for(int delay:{0,1,10,30}){publish();raw(WM_MOUSEMOVE,60,flags);gesture("fast-move",delay,flags);}
  }
#ifndef PANE_BEFORE
  for(bool rules:{false,true}){
   configure(rules);std::vector<double> times;
   for(int i=0;i<200;i++){reset();LARGE_INTEGER start{},end{};QueryPerformanceCounter(&start);auto result=raw(WM_RBUTTONDOWN,60,LLMHF_INJECTED);QueryPerformanceCounter(&end);require(result==1,"timed press captured");times.push_back(double(end.QuadPart-start.QuadPart)*1e6/frequency.QuadPart);}
   std::sort(times.begin(),times.end());output<<"directCallWithRealHook rules="<<rules<<" n=200 medianUs="<<(times[99]+times[100])/2<<" maxUs="<<times.back()<<"\n";
  }
#endif
  reset();running=false;bool removed=UnhookWindowsHookEx(installed)!=0;installed=nullptr;require(removed,"unhook");DestroyWindow(a);a=nullptr;
  output<<"assertions="<<checks<<" failures="<<failures<<" hookUninstalled=1\n";return failures?1:0;
 }catch(const std::exception& e){reset();running=false;if(installed)UnhookWindowsHookEx(installed);if(b)DestroyWindow(b);if(a)DestroyWindow(a);output<<"ERROR "<<e.what()<<"\n";return 2;}
}
