#define NOMINMAX
#include <windows.h>
#include <iostream>
#include <stdexcept>
#include "../../src/Pane.Native/PolicyEvents.h"
#include "../../src/Pane.Native/PressPolicy.h"
#include "../../src/Pane.Native/IdentityCache.h"
#include "../../src/Pane.Native/ReplayInput.h"
PolicyGeometry fixture{reinterpret_cast<HWND>(123),reinterpret_cast<HWND>(123),456,{0,0,600,400},{0,0,1920,1080},96,true};
PolicyGeometry testGeometry(POINT){return fixture;}
int queries=0;
std::shared_ptr<ProcessIdentity> processFixture(DWORD pid,DWORD level){
 auto item=std::make_shared<ProcessIdentity>();item->pid=pid;item->created.dwLowDateTime=1;item->known=true;item->integrity=level;item->path=L"C:\\fixture.exe";
 // 事件对象只替代零等待的进程退出信号，不打开或修改外部进程。
 item->process=CreateEventW(nullptr,TRUE,FALSE,nullptr);return item;
}
std::shared_ptr<ProcessIdentity> queryFixture(DWORD pid){queries++;return processFixture(pid,SECURITY_MANDATORY_MEDIUM_RID);}
LRESULT nextHook(HHOOK,int,WPARAM,LPARAM){return 0;}
#define queryProcessIdentity queryFixture
#define policyGeometry testGeometry
#define CallNextHookEx nextHook
#include "../../src/Pane.Native/Hook.cpp"
#undef queryProcessIdentity
#undef policyGeometry
#undef CallNextHookEx
int checks=0;
void check(bool value,const char* name){checks++;if(!value)throw std::runtime_error(name);}
LRESULT input(UINT message,DWORD mouseData=0){MSLLHOOKSTRUCT p{};p.mouseData=mouseData;return hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&p));}
void reset(){capture={};updateTimer();head=tail=0;requestedPid=0;}
void publish(std::shared_ptr<ProcessIdentity> entry){auto cache=std::make_unique<IdentitySnapshot>();if(entry)cache->processes.push_back(entry);identityCache.publish(std::move(cache));}
int main(){try{
 running=true;observe=false;ownIntegrity=SECURITY_MANDATORY_MEDIUM_RID;
 PanePressPolicyBegin(15,0,0,0,4,400,0);PanePressPolicyRule(L"C:\\excluded.exe",nullptr,1,0);PanePressPolicyCommit();
 publish(nullptr);check(input(WM_RBUTTONDOWN)==0&&!capture.active,"unknown integrity passes safely");check(queries==0&&requestedPid==fixture.pid,"cold press queues query without opening process");reset();
 refreshIdentities(fixture.pid,{0,0});check(queries==1,"service performs full query");
 check(input(WM_RBUTTONDOWN)==1&&queries==1,"cached press does not query");reset();
 fixture.window=reinterpret_cast<HWND>(124);check(input(WM_RBUTTONDOWN)==1&&queries==1,"new window in cached process captures immediately");reset();
 auto unknownPath=processFixture(fixture.pid,SECURITY_MANDATORY_MEDIUM_RID);unknownPath->path.clear();publish(unknownPath);
 check(input(WM_RBUTTONDOWN)==1,"unknown path with known integrity still captures");reset();
 auto normal=processFixture(fixture.pid,SECURITY_MANDATORY_MEDIUM_RID);normal->path=L"C:\\excluded.exe";publish(normal);
 check(input(WM_RBUTTONDOWN)==0&&!capture.active,"cached exclusion honored");
 auto elevated=processFixture(fixture.pid,SECURITY_MANDATORY_HIGH_RID);publish(elevated);
 for(UINT message:{WM_RBUTTONDOWN,WM_MBUTTONDOWN,WM_XBUTTONDOWN}){
  check(input(message,XBUTTON1<<16)==0&&!capture.active,"higher integrity passes without capture");
  check(head==tail,"higher integrity never requests replay");
 }
 check(queries==1,"integrity never queried by callback");
 // 相同 PID 的旧实例已经退出，不能沿用它的提权判定或排除路径。
 SetEvent(elevated->process);check(input(WM_RBUTTONDOWN)==0&&!capture.active&&requestedPid==fixture.pid,"exited generation is unknown until refreshed");reset();
 auto fresh=processFixture(fixture.pid,SECURITY_MANDATORY_MEDIUM_RID);fresh->created.dwLowDateTime=2;publish(fresh);
 check(input(WM_RBUTTONDOWN)==1,"new process generation not denied by old integrity");reset();
 ReadSnapshot<int> snapshot;snapshot.publish(std::make_unique<int>(1));
 {auto old=snapshot.read();snapshot.publish(std::make_unique<int>(2));check(*old==1,"reader retains old generation");auto current=snapshot.read();check(*current==2,"new reader sees new generation");}
 snapshot.publish(std::make_unique<int>(3));check(*snapshot.read()==3,"writer can retire old snapshots");
 INPUT pair[2]{};RECT desktop{-1920,0,1920,1080};
 for(int button:{1,2,4,8}){
  check(replayInputs(pair,button,-100,20,96,{-98,20},desktop),"construct pair at original point");
  check(pair[0].mi.dwExtraInfo==pane::InputTag&&pair[1].mi.dwExtraInfo==pane::InputTag,"both replay events tagged");
  check(pair[0].mi.dx==pair[1].mi.dx&&pair[0].mi.dy==pair[1].mi.dy&&pair[0].mi.mouseData==pair[1].mi.mouseData,"paired coordinates and button data");
 }
 check((pair[0].mi.dwFlags&MOUSEEVENTF_XDOWN)&&(pair[1].mi.dwFlags&MOUSEEVENTF_XUP)&&pair[0].mi.mouseData==XBUTTON2,"side replay pair");
 check(!replayInputs(pair,1,0,0,96,{8,0},desktop),"movement threshold prevents pointer pullback");
 check(replayInputs(pair,1,0,0,192,{15,0},desktop),"DPI scaled movement preserved");
 check(!replayInputs(pair,1,0,0,192,{16,0},desktop),"DPI scaled threshold boundary");
 running=false;std::cout<<"assertions="<<checks<<" failures=0; realInput=0; globalHook=0\n";return 0;
 }catch(const std::exception& e){reset();running=false;std::cerr<<e.what()<<'\n';return 1;}}
