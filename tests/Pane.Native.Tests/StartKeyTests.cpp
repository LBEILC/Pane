// 代替开始菜单（2026-11-28）：单按 Win 的识别和键盘回调。不挂真钩子、不发真输入，可以直接在当前桌面跑。
#include <iostream>
#include <stdexcept>
#define NOMINMAX
#include <windows.h>
unsigned sent=0,lastCount=0;WORD lastKeys[3]{};DWORD lastFlags[3]{};ULONG_PTR lastTags[3]{};
UINT testSend(UINT count,LPINPUT inputs,int){sent++;lastCount=count;for(UINT i=0;i<count&&i<3;i++){lastKeys[i]=inputs[i].ki.wVk;lastFlags[i]=inputs[i].ki.dwFlags;lastTags[i]=inputs[i].ki.dwExtraInfo;}return count;}
LRESULT testNext(HHOOK,int,WPARAM,LPARAM){return 12345;}
SHORT modifierState=0;
SHORT testKeyState(int vk){return (vk==VK_CONTROL||vk==VK_MENU||vk==VK_SHIFT)?modifierState:0;}
#define SendInput testSend
#define CallNextHookEx testNext
#define GetAsyncKeyState testKeyState
#include "../../src/Pane.Native/KeyboardHook.cpp"
#undef SendInput
#undef CallNextHookEx
#undef GetAsyncKeyState
int checks=0;
void check(bool value,const char* name){++checks;if(!value)throw std::runtime_error(name);}
void unit(){
 using pane::WinTap;
 for(unsigned win:{0x5Bu,0x5Cu}){
  WinTap t;
  check(!t.feed(win,true,false,0,false),"down does not fire");
  check(!t.feed(win,true,false,0,false),"auto repeat ignored");
  check(t.feed(win,false,false,0,false),"lone tap fires on release");
  check(!t.feed(win,false,false,0,false),"stray release does not fire");
 }
 {WinTap t;t.feed(0x5B,true,false,0,false);t.feed('E',true,false,0,false);t.feed('E',false,false,0,false);check(!t.feed(0x5B,false,false,0,false),"Win+E is a combo");}
 {WinTap t;t.feed(0x5B,true,false,0,true);check(!t.feed(0x5B,false,false,0,true),"Ctrl+Win is a combo");}
 {WinTap t;t.feed(0x5B,true,false,0,false);t.feed(0x5C,true,false,0,false);check(!t.feed(0x5C,false,false,0,false),"left and right never mix");}
 {WinTap t;t.feed(0x5B,true,false,0,false);check(!t.feed(0x5B,false,false,1,false),"mouse press while held cancels");}
 {WinTap t;t.feed(0x5B,true,false,0,false);t.feed('V',true,true,0,false);check(!t.feed(0x5B,false,false,0,false),"own key while held cancels");}
 {WinTap t;t.feed(0x5B,true,false,0,false);t.feed('E',true,false,0,false);t.feed(0x5B,false,false,0,false);
  t.feed(0x5B,true,false,3,false);check(t.feed(0x5B,false,false,3,false),"next clean tap fires again");}
 {WinTap t;t.feed(0x5B,true,true,0,false);check(!t.feed(0x5B,false,true,0,false),"own Win is ignored");}
 {WinTap t;t.feed('A',true,false,0,false);t.feed('A',false,false,0,false);t.feed(0x5B,true,false,0,false);check(t.feed(0x5B,false,false,0,false),"earlier keys do not matter");}
}
LRESULT feed(unsigned vk,bool down,ULONG_PTR tag=0){
 KBDLLHOOKSTRUCT e{};e.vkCode=vk;e.dwExtraInfo=tag;
 return keyboard(0,down?WM_KEYDOWN:WM_KEYUP,reinterpret_cast<LPARAM>(&e));
}
unsigned posted(HWND window){
 unsigned n=0;MSG m{};
 while(PeekMessageW(&m,window,WM_APP+28,WM_APP+28,PM_REMOVE)){check(m.wParam==7&&m.lParam==1,"posts launcher id with generation");n++;}
 return n;
}
void callback(){
 HWND window=CreateWindowExW(0,L"STATIC",L"",0,0,0,0,0,HWND_MESSAGE,nullptr,nullptr,nullptr);
 check(window!=nullptr,"message window");
 target=window;message=WM_APP+28;generation=7;
 startFlags=0;winTap={};
 check(feed(VK_LWIN,true)==12345&&feed(VK_LWIN,false)==12345&&posted(window)==0&&sent==0,"off: Win passes through untouched");
 startFlags=1;winTap={};
 check(feed(VK_LWIN,true)==12345,"press always forwarded");
 check(feed(VK_LWIN,false)==1,"lone release swallowed");
 check(posted(window)==1,"lone tap posts launcher");
 check(sent==1&&lastCount==3&&lastKeys[0]==0xE8&&lastKeys[1]==0xE8&&lastKeys[2]==VK_LWIN,"replaced by dummy pair and Win release");
 check(!(lastFlags[0]&KEYEVENTF_KEYUP)&&(lastFlags[1]&KEYEVENTF_KEYUP)&&(lastFlags[2]&KEYEVENTF_KEYUP)&&(lastFlags[2]&KEYEVENTF_EXTENDEDKEY),"key flags");
 check(lastTags[0]==pane::InputTag&&lastTags[1]==pane::InputTag&&lastTags[2]==pane::InputTag,"injected input carries own tag");
 check(feed(0xE8,true,pane::InputTag)==12345&&feed(0xE8,false,pane::InputTag)==12345&&feed(VK_LWIN,false,pane::InputTag)==12345&&posted(window)==0,"own replacement passes through");
 check(feed(VK_RWIN,true)==12345&&feed('D',true)==12345&&feed('D',false)==12345&&feed(VK_RWIN,false)==12345&&posted(window)==0&&sent==1,"Win+D untouched");
 modifierState=static_cast<SHORT>(0x8000);
 check(feed(VK_LWIN,true)==12345&&feed(VK_LWIN,false)==12345&&posted(window)==0,"Shift+Win untouched");
 modifierState=0;
 startFlags=0;DestroyWindow(window);target=nullptr;
}
int main(){
 try {unit();callback();std::cout<<"PASS assertions="<<checks<<std::endl;return 0;}
 catch(const std::exception& e){std::cout<<"FAIL assertions="<<checks<<"; "<<e.what()<<std::endl;return 1;}
}
