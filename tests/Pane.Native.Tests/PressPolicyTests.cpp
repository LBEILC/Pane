#define NOMINMAX
#include <windows.h>
#include <iostream>
#ifdef PANE_BEFORE
#include "../../.codex-runs/032/before/PolicyEvents.h"
#else
#include "../../src/Pane.Native/PolicyEvents.h"
#endif
#include "../../src/Pane.Native/PressPolicy.h"
PolicyGeometry fixture{reinterpret_cast<HWND>(123),reinterpret_cast<HWND>(123),456,{0,0,600,400},{0,0,1920,1080},96,true};
PressIdentity fixtureIdentity{L"C:\\fixture.exe",L"Fixture",false,true};
PolicyGeometry testGeometry(POINT){return fixture;}
PressIdentity testIdentity(const PolicyGeometry&,bool){return fixtureIdentity;}
LRESULT nextHook(HHOOK,int,WPARAM,LPARAM){return 0;}
#define policyGeometry testGeometry
#define PANE_TEST_IDENTITY
#define cachedIdentity testIdentity
#define CallNextHookEx nextHook
#ifdef PANE_BEFORE
#include "../../.codex-runs/032/before/Hook.cpp"
#else
#include "../../src/Pane.Native/Hook.cpp"
#endif
#undef policyGeometry
#undef cachedIdentity
#undef CallNextHookEx
int checks=0,failures=0;
void check(bool value,const char* name){checks++;if(!value){failures++;std::cout<<"FAIL "<<name<<'\n';}}
void reset(){capture={};updateTimer();head=tail=0;}
void publish(){auto revision=PanePolicyBegin();PanePolicy(reinterpret_cast<intptr_t>(fixture.window),fixture.pid,0,0,96,0,1,0);PanePolicyCommit(revision);}
LRESULT raw(WPARAM message,int x=0,DWORD flags=0,ULONG_PTR tag=0){MSLLHOOKSTRUCT p{};p.pt={x,0};p.flags=flags;p.dwExtraInfo=tag;return hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&p));}
void configure(int paused=0){
#ifndef PANE_BEFORE
 PanePressPolicyBegin(1,0,1,paused,4,400,1);PanePressPolicyCommit();
#else
 (void)paused;
#endif
}
void gesture(const char* scenario,int delay,DWORD flags){
 check(raw(WM_RBUTTONDOWN,40,flags)==1,scenario);
 if(delay)Sleep(delay);
 raw(WM_MOUSEMOVE,140,flags);raw(WM_RBUTTONUP,140,flags);
 Event e{};int begin=0,point=0,end=0,replay=0;
 while(PanePoll(&e)){begin+=e.kind==pane::Begin;point+=e.kind==pane::Point;end+=e.kind==pane::End;replay+=e.kind==pane::Replay;}
 check(begin==1&&point==1&&end==1&&replay==0,"fast gesture emits ordered session without click replay");reset();
}
int main(){running=true;observe=false;configure();
 for(DWORD flags:{DWORD(0),DWORD(LLMHF_INJECTED)}){
  publish();fixture.window=reinterpret_cast<HWND>(reinterpret_cast<intptr_t>(fixture.window)+1);fixture.pid++;
  gesture("new window before asynchronous notification",0,flags);
  for(int delay:{0,1,10,30}){publish();raw(WM_MOUSEMOVE,40,flags);gesture("move invalidates policy immediately before press",delay,flags);}
  publish();windowEvent(nullptr,EVENT_OBJECT_SHOW,fixture.window,OBJID_WINDOW,0,0,0);gesture("window notification before background refresh",0,flags);
  publish();PanePolicyBegin();gesture("press while background refresh is in progress",0,flags);
 }
#ifndef PANE_BEFORE
 configure();publish();fixtureIdentity.path.clear();check(raw(WM_RBUTTONDOWN)==1,"unknown process path still captures");reset();
 fixtureIdentity.path=L"C:\\fixture.exe";
 PanePressPolicyBegin(1,0,1,0,4,400,1);PanePressPolicyRule(L"c:\\FIXTURE.exe",nullptr,1,0);PanePressPolicyCommit();
 check(raw(WM_RBUTTONDOWN)==0&&!capture.active,"confirmed disabled process passes through");
 PanePressPolicyBegin(1,0,1,0,4,400,1);PanePressPolicyRule(fixtureIdentity.path.c_str(),nullptr,1,0);PanePressPolicyRule(fixtureIdentity.path.c_str(),L"Fixture",0,0);PanePressPolicyCommit();
 check(raw(WM_RBUTTONDOWN)==1,"specific class overrides disabled path");reset();
 fixtureIdentity.windowClass=L"fixture";check(raw(WM_RBUTTONDOWN)==0,"class matching remains case sensitive");fixtureIdentity.windowClass=L"Fixture";
 configure(1);check(raw(WM_RBUTTONDOWN)==0,"pause respected");configure();
 fixture.rect=fixture.monitor;check(raw(WM_RBUTTONDOWN)==0,"fullscreen pause respected");
 fixtureIdentity.windowClass=L"WorkerW";check(raw(WM_RBUTTONDOWN)==1,"desktop is not fullscreen app");reset();
 fixture.rect={0,0,600,400};fixtureIdentity.windowClass=L"Fixture";
 check(raw(WM_RBUTTONDOWN,0,LLMHF_INJECTED,pane::InputTag)==0,"own replay never recaptured");
 check(raw(WM_RBUTTONDOWN,0,LLMHF_INJECTED)==1,"foreign injected right click captured");raw(WM_RBUTTONUP,0,LLMHF_INJECTED);
 Event e{};PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Replay,"ordinary foreign injected click replayed");reset();
 check(captureTimer==0,"no idle capture timer");
 fixtureIdentity.higherIntegrity=true;
 check(raw(WM_RBUTTONDOWN)==0&&!capture.active,"higher integrity right passes");
 check(raw(WM_MBUTTONDOWN)==0&&!capture.active,"higher integrity middle passes");
 MSLLHOOKSTRUCT side{};side.mouseData=XBUTTON1<<16;
 check(hook(HC_ACTION,WM_XBUTTONDOWN,reinterpret_cast<LPARAM>(&side))==0&&!capture.active,"higher integrity super passes");
 fixtureIdentity.higherIntegrity=false;
 check(!HookHealth::slow(0,1000),"zero duration healthy");
 check(!HookHealth::slow(1000,1000),"threshold not exceeded");
 check(HookHealth::slow(1001,1000),"slow callback requests repair");
 check(!HookHealth::slow(1001,0),"invalid clock ignored");
 check(HookHealth::slow(2000,1000),"long callback requests repair");
 auto process=queryProcessIdentity(GetCurrentProcessId());IdentitySnapshot cache;cache.processes.push_back(process);
 check(findIdentity(cache,GetCurrentProcessId())==process.get()&&process->known,"real identity cached and integrity known");
 check(findIdentity(cache,0)==nullptr,"cache miss");
 check(process->created.dwLowDateTime||process->created.dwHighDateTime,"creation identity recorded");
#endif
 running=false;std::cout<<"assertions="<<checks<<" failures="<<failures<<"; realInput=0; globalHook=0\n";return failures?1:0;
}
