#define NOMINMAX
#include <windows.h>
#include <iostream>
#include <stdexcept>
#include "../../src/Pane.Native/PolicyEvents.h"
PolicyGeometry fixture{reinterpret_cast<HWND>(123),reinterpret_cast<HWND>(123),456,{0,0,600,400},{0,0,1920,1080},96,true};
PolicyGeometry testGeometry(POINT){return fixture;}
#define policyGeometry testGeometry
// 直接调用产品钩子状态机；下游链替换为空函数，绝不发送或转发真实输入。
LRESULT nextHook(HHOOK,int,WPARAM,LPARAM){return 0;}
#define CallNextHookEx nextHook
#include "../../src/Pane.Native/Hook.cpp"
#undef CallNextHookEx
#undef policyGeometry
int checks=0;
void check(bool value,const char* text){checks++;if(!value)throw std::runtime_error(text);}
void input(WPARAM message,int x=0,int y=0,DWORD data=0){
    MSLLHOOKSTRUCT value{};value.pt={x,y};value.mouseData=data;
    auto revision=PanePolicyBegin();PanePolicy(123,456,x,y,96,0,1,80);PanePolicyCommit(revision);
    hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&value));
}
void pump(unsigned milliseconds){auto end=GetTickCount64()+milliseconds;MSG msg{};do{while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE))dispatch(msg);Sleep(1);}while(GetTickCount64()<end);}
int main(){HANDLE signal=CreateEventW(nullptr,FALSE,FALSE,nullptr);try{
    MSG msg{};PeekMessageW(&msg,nullptr,0,0,PM_NOREMOVE);threadId=GetCurrentThreadId();running=true;observe=false;PaneSetWakeEvent(signal);
    pump(40);check(captureTimer==0,"idle timer off");check(WaitForSingleObject(signal,0)==WAIT_TIMEOUT,"idle event quiet");
    input(WM_RBUTTONDOWN);check(captureTimer!=0,"right down starts timer");check(WaitForSingleObject(signal,0)==WAIT_OBJECT_0,"begin wakes consumer");
    Event e{};check(PanePoll(&e)&&e.kind==pane::Begin,"begin queued before signal");
    pump(120);check(PanePoll(&e)&&e.kind==pane::Cancel,"stationary timeout still fires");
    input(WM_RBUTTONUP);check(captureTimer==0,"right up stops timer");while(PanePoll(&e)){}ResetEvent(signal);
    pump(40);check(WaitForSingleObject(signal,0)==WAIT_TIMEOUT,"idle after release quiet");
    PaneSuperPolicy(4,40);input(WM_XBUTTONDOWN,0,0,XBUTTON1<<16);check(captureTimer!=0,"side down starts timer");
    check(PanePoll(&e)&&e.kind==pane::Begin,"side begin");pump(65);check(PanePoll(&e)&&e.kind==pane::Super,"side hold fires");
    input(WM_XBUTTONUP,0,0,XBUTTON1<<16);check(captureTimer==0,"side up stops timer");while(PanePoll(&e)){}
    input(WM_RBUTTONDOWN);while(PanePoll(&e)){}PaneCancelCapture();pump(1);check(PanePoll(&e)&&e.kind==pane::Cancel,"cancel message wakes without waiting for timer");
    input(WM_RBUTTONUP);check(captureTimer==0,"cancelled release stops timer");while(PanePoll(&e)){}
    input(WM_XBUTTONDOWN,0,0,XBUTTON1<<16);while(PanePoll(&e)){}input(WM_XBUTTONUP,0,0,XBUTTON1<<16);check(PanePoll(&e)&&e.kind==pane::Replay,"short side click replay decision");
    observe=true;input(WM_RBUTTONDOWN);check(!capture.active&&!captureTimer,"observe never starts capture timer");
    PaneSetWakeEvent(nullptr);running=false;CloseHandle(signal);std::cout<<"PASS hook scheduling assertions="<<checks<<"; input=0; globalHook=0\n";return 0;
}catch(const std::exception& e){if(captureTimer)KillTimer(nullptr,captureTimer);PaneSetWakeEvent(nullptr);running=false;CloseHandle(signal);std::cerr<<e.what()<<"\n";return 1;}}
