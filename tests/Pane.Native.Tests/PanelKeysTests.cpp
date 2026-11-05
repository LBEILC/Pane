#define NOMINMAX
#include <windows.h>
#include <array>
#include <fstream>
#include <vector>
#include <algorithm>
#include <stdexcept>
#include <iostream>
bool failInstall=false,realInput=false;
std::array<bool,256>* physical=nullptr;
std::vector<INPUT> sent;
SHORT testState(int k);
UINT testSend(UINT n,LPINPUT p,int size){if(realInput)return SendInput(n,p,size);sent.insert(sent.end(),p,p+n);return n;}
HHOOK testInstall(int id,HOOKPROC proc,HMODULE module,DWORD tid){if(failInstall){SetLastError(5);return nullptr;}return SetWindowsHookExW(id,proc,module,tid);}
LRESULT testDefault(HWND,UINT,WPARAM,LPARAM);
LRESULT panelNext(HHOOK,int,WPARAM,LPARAM);
bool chainDouble=false;
#define GetAsyncKeyState testState
#define SetWindowsHookExW testInstall
#define SendInput testSend
#define DefWindowProcW testDefault
#define CallNextHookEx panelNext
#include "../../src/Pane.Native/PanelKeys.cpp"
#undef CallNextHookEx
#include "../../src/Pane.Native/KeyboardHook.cpp"
#undef GetAsyncKeyState
#undef SetWindowsHookExW
#undef SendInput
#undef DefWindowProcW
#include "../../src/Pane.Native/DoubleTap.h"
std::array<bool,256> keys{};
SHORT testState(int k){return physical&&(*physical)[k]?static_cast<SHORT>(0x8000):0;}
int checks=0;unsigned notices=0,doubleNotices=0;LPARAM lastNotice=0;
LRESULT panelNext(HHOOK h,int code,WPARAM w,LPARAM l){if(realInput)return CallNextHookEx(h,code,w,l);return chainDouble?keyboard(code,w,l):0;}
void check(bool ok,const char* what){checks++;if(!ok)throw std::runtime_error(what);}
void benchmark(std::vector<double>& samples){
 LARGE_INTEGER freq;QueryPerformanceFrequency(&freq);samples.reserve(10000);
 for(int i=0;i<10000;i++){
  KBDLLHOOKSTRUCT k{};k.vkCode=i%4<2?VK_DOWN:'A';bool down=i%2==0;
  LARGE_INTEGER a,b;QueryPerformanceCounter(&a);panelkeys::keyboard(0,down?WM_KEYDOWN:WM_KEYUP,reinterpret_cast<LPARAM>(&k));QueryPerformanceCounter(&b);
  samples.push_back((b.QuadPart-a.QuadPart)*1000000.0/freq.QuadPart);
  // 重放与下一轮准备不计入回调耗时；整段在拥有钩子的线程执行。
  if(i%4==3)panelkeys::drain(false);
 }
}
LRESULT testDefault(HWND h,UINT m,WPARAM w,LPARAM l){
 if(m==WM_APP+90)return panelkeys::keyboard(0,w,l);
 if(m==WM_APP+91){benchmark(*reinterpret_cast<std::vector<double>*>(l));return 0;}
 if(m==WM_APP+92){
  for(auto e:*reinterpret_cast<std::vector<std::pair<unsigned,bool>>*>(l)){KBDLLHOOKSTRUCT k{};k.vkCode=e.first;panelkeys::keyboard(0,e.second?WM_KEYDOWN:WM_KEYUP,reinterpret_cast<LPARAM>(&k));}
  return 0;
 }
 return DefWindowProcW(h,m,w,l);
}
LRESULT CALLBACK receiver(HWND h,UINT m,WPARAM w,LPARAM l){if(m==WM_APP+59){notices++;lastNotice=l;return 0;}if(m==WM_APP+60){doubleNotices++;return 0;}return DefWindowProcW(h,m,w,l);}
void pump(unsigned ms){auto end=GetTickCount64()+ms;MSG m{};do{while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}Sleep(1);}while(GetTickCount64()<end);}
LRESULT key(unsigned vk,bool down,ULONG_PTR tag=0,DWORD flags=0){KBDLLHOOKSTRUCT k{};k.vkCode=vk;k.scanCode=MapVirtualKeyW(vk,MAPVK_VK_TO_VSC);k.dwExtraInfo=tag;k.flags=flags;return SendMessageW(panelkeys::commandWindow,WM_APP+90,down?WM_KEYDOWN:WM_KEYUP,reinterpret_cast<LPARAM>(&k));}
void unit(){
 using namespace pane;
 std::array<bool,256> held{};
 struct Row {unsigned vk;PanelModifiers mods;PanelAction action;};
 const Row rows[]={
  {0x26,{},PanelUp},{0x28,{},PanelDown},{13,{},PanelEnter},{13,{false,false,false,true},PanelShiftEnter},
  {0x33,{false,true},PanelQuick},{0x50,{true},PanelPin},{9,{},PanelSend},{27,{},PanelEscape},
  {0x41,{},PanelType},{0x41,{false,false,false,true},PanelType},
  {0x26,{false,false,false,true},PanelNone},{0x28,{true},PanelNone},{13,{false,true},PanelNone},
  {0x33,{true,true},PanelNone},{0x33,{false,true,true},PanelNone},{0x50,{true,false,false,true},PanelNone},
  {9,{true},PanelNone},{27,{false,false,true},PanelNone},{0x43,{true},PanelNone},{0x41,{true,true},PanelNone},
  {0x61,{false,true},PanelNone},{8,{},PanelNone},{46,{},PanelNone},{37,{},PanelNone},{39,{},PanelNone},
  {36,{},PanelNone},{35,{},PanelNone},{33,{},PanelNone},{34,{},PanelNone},{0x70,{},PanelNone},{0xA4,{},PanelNone}
 };
 for(auto row:rows){PanelKeys s;s.begin(true,held);auto r=s.feed({row.vk,0,true},row.mods,10);check(r.action==row.action&&r.swallow==(row.action!=PanelNone),"routing row");if(row.action==PanelQuick)check(r.number==3,"quick digit");}
 for(unsigned k:{0x30u,0x39u,0x5Au,0x20u,0x60u,0x69u,0x6Au,0x6Fu,0xBAu,0xC0u,0xDBu,0xDFu,0xE2u})check(PanelKeys::character(k),"character virtual keys");
 for(unsigned vk:{0x26u,0x28u,13u,9u,27u})for(unsigned bits=1;bits<16;bits++){
  PanelModifiers m{(bits&1)!=0,(bits&2)!=0,(bits&4)!=0,(bits&8)!=0};PanelKeys s;s.begin(true,held);
  auto r=s.feed({vk,0,true},m,0);check(r.swallow==(vk==13&&bits==8),"all forbidden navigation modifiers");
 }
 for(unsigned bits=0;bits<16;bits++){
  PanelModifiers m{(bits&1)!=0,(bits&2)!=0,(bits&4)!=0,(bits&8)!=0};
  for(unsigned vk=0x31;vk<=0x39;vk++){
   PanelKeys s;s.begin(true,held);auto r=s.feed({vk,0,true},m,0);
   auto expected=m.alt&&!m.ctrl&&!m.win?PanelQuick:!m.ctrl&&!m.alt&&!m.win?PanelType:PanelNone;
   check(r.action==expected&&r.number==(expected==PanelQuick?vk-0x30:0),"all quick digit modifier combinations");
  }
  PanelKeys pin;pin.begin(true,held);auto expected=bits==1?PanelPin:bits==0||bits==8?PanelType:PanelNone;
  check(pin.feed({'P',0,true},m,0).action==expected,"all pin modifiers");
  PanelKeys text;text.begin(true,held);check(text.feed({'A',0,true},m,0).action==(bits==0||bits==8?PanelType:PanelNone),"all typing modifiers including AltGr");
  PanelKeys tab;tab.begin(false,held);check(!tab.feed({9,0,true},m,0).swallow,"disabled tab all modifiers");
 }
 {PanelKeys s;s.begin(false,held);check(!s.feed({9,0,true},{},0).swallow,"disabled send passes tab");}
 {PanelKeys s;s.begin(true,held);for(int i=0;i<5;i++)check(s.feed({0x28,0,true},{},0).action==PanelDown,"repeat notification");s.end();check(s.feed({0x28,0,true},{},0).swallow,"repeat after end swallowed");check(s.feed({0x28,0,false},{},0).swallow&&!s.pending(),"release after end swallowed");}
 for(unsigned vk:{0xA4u,0xA5u,0x5Bu,0x5Cu}){PanelKeys s;held[vk]=true;s.begin(true,held);held[vk]=false;s.end();check(s.feed({vk,0,false},{},0).mask&&!s.pending(),"begin modifiers masked after end");}
 {PanelKeys s;s.begin(true,held);s.feed({0xA4,0,true},{},0);check(!s.feed({0xA4,0,false},{},0).mask,"plain alt not masked");}
 for(unsigned side:{1u,2u,3u}){PanelKeys s;s.begin(true,held);auto r=s.feed({'3',0,true},{false,true,false,false,side},0);s.end();check(r.action==PanelQuick,"quick marks alt responsibility");if(side&1)check(s.feed({0xA4,0,false},{},0).mask,"left alt quick masked");if(side&2)check(s.feed({0xA5,0,false},{},0).mask,"right alt quick masked");}
 {PanelKeys s;s.begin(true,held);check(!s.feed({0x41,0,true,false,true},{},0).swallow,"own ignored");check(s.feed({0x41,30,true},{false,false,false,true},20).action==PanelType,"foreign injection accepted");
  check(s.feed({0xA0,42,false},{},21).swallow&&s.feed({0x41,30,false},{},22).swallow&&s.feed({0x42,48,true},{},23).swallow,"queue all events");
  std::vector<PanelQueued> q(s.queued.begin(),s.queued.end());
  check(s.count==4&&q[0].event.vk==0x41&&q[1].event.vk==0xA0&&!q[2].event.down&&q[3].event.vk==0x42,"queue exact order");
  check(!s.expired(519)&&s.expired(520),"timeout exact boundary");s.replayed();check(!s.active&&!s.pending()&&!s.typing,"replay clears state");}
 {pane::DoubleTap d;d.feed(0xA2,true,0,false,0);d.feed(0xA2,false,10,false,1);d.feed(0xA2,true,20,false,1);check(!d.feed(0xA2,false,30,false,1),"swallowed chord invalidates doubletap");}
}
int main(int argc,char** argv){
 if(argc<2)return 2;std::ofstream log(argv[1]);HWND window=nullptr;
 try{
  unit();log<<"unit assertions="<<checks<<std::endl;
  wchar_t desktop[128]{};DWORD size=0;check(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&size)&&wcsncmp(desktop,L"Pane027-",8)==0,"hidden desktop required");
  physical=&keys;
  WNDCLASSW wc{};wc.lpfnWndProc=receiver;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane059Fixture";RegisterClassW(&wc);
  window=CreateWindowExW(0,wc.lpszClassName,L"",WS_POPUP,0,0,500,400,nullptr,nullptr,wc.hInstance,nullptr);check(window!=nullptr,"fixture created");
  failInstall=true;check(!PanePanelKeysBegin(window,WM_APP+59,1,1)&&!PanePanelKeysInstalled(),"hook installation failure");failInstall=false;
  auto begin=[&]{check(PanePanelKeysBegin(window,WM_APP+59,1,1)&&PanePanelKeysActive()&&PanePanelKeysInstalled(),"begin real hook");};
  begin();PanePanelKeysEnd();check(!PanePanelKeysActive()&&!PanePanelKeysInstalled(),"end uninstalls immediately");
  begin();check(key(VK_DOWN,true)==1,"callback swallows down");pump(5);check(notices==1&&lastNotice==pane::PanelDown,"post notification");
  PanePanelKeysEnd();check(PanePanelKeysInstalled(),"end retains swallowed release");check(key(VK_DOWN,false)==1,"release swallowed after end");SendMessageW(panelkeys::commandWindow,WM_NULL,0,0);pump(5);check(!PanePanelKeysInstalled(),"last release unhooks");
  keys[VK_LMENU]=true;begin();PanePanelKeysEnd();keys[VK_LMENU]=false;sent.clear();check(key(VK_LMENU,false)==1,"alt release held for mask");SendMessageW(panelkeys::commandWindow,WM_NULL,0,0);pump(5);
  check(sent.size()==3&&sent[0].ki.wVk==0xE8&&sent[1].ki.dwFlags==KEYEVENTF_KEYUP&&sent[2].ki.wVk==VK_LMENU,"mask before original alt release");
  check(!PanePanelKeysInstalled(),"mask cleanup unhooks");
  keys[VK_LMENU]=true;begin();keys[VK_LMENU]=false;key(VK_LMENU,false);SendMessageW(panelkeys::commandWindow,WM_NULL,0,0);pump(5);check(PanePanelKeysActive()&&PanePanelKeysInstalled(),"opening Alt release keeps routing active");PanePanelKeysEnd();
  for(unsigned next:{static_cast<unsigned>('A'),static_cast<unsigned>(VK_DOWN),static_cast<unsigned>(VK_F1)}){
   keys[VK_LMENU]=true;begin();sent.clear();notices=0;
   std::vector<std::pair<unsigned,bool>> events={{VK_LMENU,false},{next,true},{next,false}};
   SendMessageW(panelkeys::commandWindow,WM_APP+92,0,reinterpret_cast<LPARAM>(&events));keys[VK_LMENU]=false;pump(5);
   if(next=='A'){
    check(notices==1&&lastNotice==pane::PanelType&&sent.empty()&&PanePanelKeysActive(),"typing immediately after masked Alt waits for search replay");
    PanePanelKeysReplay();check(sent.size()==5&&sent[2].ki.wVk==VK_LMENU&&sent[3].ki.wVk=='A',"masked Alt and first character stay in order");
   }else if(next==VK_DOWN){check(notices==1&&lastNotice==pane::PanelDown&&sent.size()==3&&PanePanelKeysActive(),"navigation immediately after masked Alt stays routed");PanePanelKeysEnd();}
   else {check(notices==0&&sent.size()==5&&sent[3].ki.wVk==VK_F1,"unhandled keys after masked Alt forward in order");PanePanelKeysEnd();}
  }
  begin();keys[VK_LMENU]=true;sent.clear();key('3',true);PanePanelKeysEnd();key('3',false);keys[VK_LMENU]=false;key(VK_LMENU,false);SendMessageW(panelkeys::commandWindow,WM_NULL,0,0);pump(5);check(sent.size()==3&&sent[0].ki.wVk==0xE8,"alt quick mask after end");
  begin();keys[VK_LMENU]=true;key(VK_LMENU,true);keys[VK_LMENU]=false;sent.clear();key(VK_LMENU,false);check(sent.empty(),"plain alt passes without mask");PanePanelKeysEnd();
  begin();sent.clear();key('A',true);key('A',false);key(VK_LSHIFT,true);key('B',true);key(VK_LSHIFT,false);PanePanelKeysReplay();
  check(sent.size()==5&&sent[0].ki.wVk=='A'&&sent[1].ki.dwFlags==KEYEVENTF_KEYUP&&sent[2].ki.wVk==VK_LSHIFT&&sent[3].ki.wVk=='B'&&sent[4].ki.dwFlags==KEYEVENTF_KEYUP,"native replay order");
  check(std::all_of(sent.begin(),sent.end(),[](const INPUT& i){return i.ki.dwExtraInfo==pane::InputTag&&(i.ki.dwFlags&KEYEVENTF_UNICODE)==0;}),"tagged virtual keys preserve IME path");check(!PanePanelKeysInstalled(),"replay removes hook, physical future ups pass");
  begin();sent.clear();key('C',true);pump(600);check(!PanePanelKeysActive()&&!PanePanelKeysInstalled()&&sent.size()==1,"timer replays without UI");
  begin();notices=0;key(VK_DOWN,true,pane::InputTag);key(VK_DOWN,true,0x50414E45303130ULL);{pane::SimulationScope scope;key(VK_DOWN,true);}pump(5);check(notices==0,"both own tags and simulation ignored");
  key(VK_DOWN,true,123,LLKHF_INJECTED);pump(5);check(notices==1,"foreign injected callback routed");key(VK_DOWN,false);PanePanelKeysEnd();
  check(PaneKeyboardStart(window,WM_APP+60,8,VK_LCONTROL,0)==0&&PaneKeyboardActive(),"054 hook installed alongside panel");begin();chainDouble=true;
  for(int i=0;i<2;i++){keys[VK_LCONTROL]=true;key(VK_LCONTROL,true);key('P',true);key('P',false);keys[VK_LCONTROL]=false;key(VK_LCONTROL,false);}pump(5);check(doubleNotices==0,"swallowed Ctrl P does not become double tap");
  for(int i=0;i<2;i++){keys[VK_LCONTROL]=true;key(VK_LCONTROL,true);keys[VK_LCONTROL]=false;key(VK_LCONTROL,false);}pump(5);check(doubleNotices==1,"054 double tap still routes while panel hook active");chainDouble=false;PaneKeyboardStop();PanePanelKeysEnd();check(!PaneKeyboardActive()&&!PanePanelKeysInstalled(),"both hooks stop independently");
  // 基线/收起后：同一进程同一线程各等一秒，记录 CPU；消息线程阻塞无计时器。
  auto cpu=[](HANDLE h){FILETIME c,e,k,u;GetThreadTimes(h,&c,&e,&k,&u);return ((uint64_t(k.dwHighDateTime)<<32)|k.dwLowDateTime)+((uint64_t(u.dwHighDateTime)<<32)|u.dwLowDateTime);};
  auto thread=panelkeys::worker.native_handle();auto before=cpu(thread);Sleep(1000);log<<"closed worker CPU ms="<<(cpu(thread)-before)/10000.0<<"; hook="<<PanePanelKeysInstalled()<<std::endl;
  begin();std::vector<double> samples;
  SendMessageW(panelkeys::commandWindow,WM_APP+91,0,reinterpret_cast<LPARAM>(&samples));
  std::sort(samples.begin(),samples.end());log<<"callback mixed samples="<<samples.size()<<" us median="<<samples[samples.size()/2]<<" p99="<<samples[samples.size()*99/100]<<std::endl;PanePanelKeysEnd();pump(5);
  keys[VK_LWIN]=true;begin();sent.clear();PanePanelKeysDispose();check(sent.size()==2&&sent[0].ki.wVk==0xE8&&sent[1].ki.dwFlags==KEYEVENTF_KEYUP,"dispose masks held Win without releasing it");keys[VK_LWIN]=false;check(!PanePanelKeysInstalled()&&!panelkeys::worker.joinable(),"dispose joins worker");
  log<<"PASS assertions="<<checks<<"; panelkeys=0; worker=0; clipboard=0"<<std::endl;
  if(argc>2){realInput=true;physical=nullptr;begin();INPUT i[2]{};for(auto& v:i){v.type=INPUT_KEYBOARD;v.ki.wVk=VK_F24;}i[1].ki.dwFlags=KEYEVENTF_KEYUP;SetLastError(0);auto n=SendInput(2,i,sizeof(INPUT));log<<"real SendInput sent="<<n<<" error="<<GetLastError()<<std::endl;PanePanelKeysDispose();if(n!=2)return 3;}
  DestroyWindow(window);return 0;
 }catch(const std::exception& e){log<<"FAIL "<<e.what()<<std::endl;PaneKeyboardStop();PanePanelKeysDispose();if(window)DestroyWindow(window);return 1;}
}
