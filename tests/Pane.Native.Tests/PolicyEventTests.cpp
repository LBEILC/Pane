#define main schedulingMain
#include "HookTimerTests.cpp"
#undef main
void publish(int blocked=0,int enabled=1){auto revision=PanePolicyBegin();PanePolicy(reinterpret_cast<intptr_t>(fixture.window),fixture.pid,0,0,96,blocked,enabled,0);PanePolicyCommit(revision);}
void raw(WPARAM message,int x=0,int y=0,DWORD data=0){MSLLHOOKSTRUCT value{};value.pt={x,y};value.mouseData=data;hook(HC_ACTION,message,reinterpret_cast<LPARAM>(&value));}
void reset(){capture={};updateTimer();head=tail=0;}
int main(){try{
 check(schedulingMain()==0,"existing scheduling tests");
 running=true;observe=false;PaneSuperPolicy(0,400);publish();
 Sleep(100);raw(WM_RBUTTONDOWN);check(capture.active,"stationary snapshot survives old 75ms deadline");raw(WM_RBUTTONUP);Event e{};PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Replay,"ordinary right click replay");reset();
 windowEvent(nullptr,EVENT_OBJECT_SHOW,fixture.window,OBJID_WINDOW,0,0,0);check(PanePolicyDirty()!=0,"stationary window change invalidates");raw(WM_RBUTTONDOWN);check(!capture.active,"stale policy fails open");publish();raw(WM_RBUTTONDOWN);check(capture.active,"refreshed window permits capture");reset();
 auto revision=PanePolicyBegin();windowEvent(nullptr,EVENT_SYSTEM_FOREGROUND,fixture.window,0,0,0,0);PanePolicy(123,456,0,0,96,0,1,0);PanePolicyCommit(revision);check(PanePolicyDirty()!=0,"event during refresh cannot be acknowledged");publish();
 fixture.foreground=reinterpret_cast<HWND>(987);raw(WM_RBUTTONDOWN);check(!capture.active&&PanePolicyDirty(),"foreground change before event delivery rejects");publish();raw(WM_RBUTTONDOWN);check(capture.active,"foreground refresh recovers");reset();
 fixture.rect=fixture.monitor;raw(WM_RBUTTONDOWN);check(!capture.active&&PanePolicyDirty(),"fullscreen geometry change before event rejects");publish(1,0);raw(WM_RBUTTONDOWN);check(!capture.active,"fullscreen policy blocks");
 fixture.rect={0,0,600,400};windowEvent(nullptr,EVENT_OBJECT_LOCATIONCHANGE,fixture.window,OBJID_WINDOW,0,0,0);publish();raw(WM_RBUTTONDOWN);check(capture.active,"fullscreen exit restores");reset();
 fixture.window=reinterpret_cast<HWND>(234);fixture.pid=567;raw(WM_RBUTTONDOWN);check(!capture.active,"new program before event delivery rejects");publish(1,0);raw(WM_RBUTTONDOWN);check(!capture.active,"disabled program stays blocked");publish();
 fixture.windowClass=99;raw(WM_RBUTTONDOWN);check(!capture.active&&PanePolicyDirty(),"reused window with changed class rejects");publish();
 revision=PanePolicyBegin();raw(WM_RBUTTONDOWN);check(!capture.active,"refresh in progress cannot use previous decision");PanePolicy(234,567,0,0,96,0,1,0);PanePolicyCommit(revision);
 raw(WM_MOUSEMOVE,9,0);check(PanePolicyDirty(),"movement over eight pixels invalidates");publish();
 raw(WM_RBUTTONDOWN);raw(WM_MOUSEWHEEL,0,0,120u<<16);PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Modifier&&e.value==1,"wheel through event policy");reset();
 for(auto message:{WM_LBUTTONDOWN,WM_MBUTTONDOWN}){publish();raw(WM_RBUTTONDOWN);raw(message);PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Modifier,"button modifier through event policy");reset();}
 publish();raw(WM_RBUTTONDOWN);raw(WM_MOUSEMOVE,80,0);PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Point,"gesture movement through event policy");raw(WM_RBUTTONUP,80,0);reset();
 PaneSuperPolicy(4,30);publish();raw(WM_XBUTTONDOWN,0,0,XBUTTON1<<16);pump(60);PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Super,"long hold through event policy");raw(WM_XBUTTONUP,0,0,XBUTTON1<<16);reset();
 publish();raw(WM_XBUTTONDOWN,0,0,XBUTTON1<<16);raw(WM_XBUTTONUP,0,0,XBUTTON1<<16);PanePoll(&e);check(PanePoll(&e)&&e.kind==pane::Replay,"short side through event policy");reset();
 publish();auto stable=policyRevision.load();windowEvent(nullptr,EVENT_OBJECT_LOCATIONCHANGE,fixture.window,OBJID_CARET,0,0,0);check(policyRevision==stable,"caret events ignored");
 fixture={};publish();check(!PanePolicyDirty(),"empty desktop settles without refresh loop");raw(WM_RBUTTONDOWN);check(!capture.active,"empty desktop never captures");
 running=false;std::cout<<"PASS event policy assertions="<<checks<<"; realInput=0; globalHook=0\n";return 0;
 }catch(const std::exception& e){reset();running=false;std::cerr<<e.what()<<'\n';return 1;}}
