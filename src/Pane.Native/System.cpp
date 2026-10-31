#define NOMINMAX
#include <windows.h>
#include <UIAutomation.h>
#include <exdisp.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <atomic>
#include <mutex>
#include <chrono>
#include "Capture.h"
#include "ReplayInput.h"
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
std::atomic<unsigned> commandEpoch=0;
std::recursive_timed_mutex inputMutex;
std::atomic_flag selectionBusy=ATOMIC_FLAG_INIT;
struct SelectionLease {~SelectionLease(){selectionBusy.clear();}};
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
bool key(WORD vk,bool down){INPUT i{};i.type=INPUT_KEYBOARD;i.ki.wVk=vk;i.ki.dwFlags=down?0:KEYEVENTF_KEYUP;i.ki.dwExtraInfo=pane::InputTag;return SendInput(1,&i,sizeof(i))==1;}
struct Snapshot {UINT format;std::vector<BYTE> bytes;};
bool snapshot(std::vector<Snapshot>& data){
 UINT f=0;SetLastError(0);
 while((f=EnumClipboardFormats(f))!=0){
  // 只接受可逐字节恢复的内存格式；OLE、私有、图形和延迟渲染一律放弃。
  if(f!=CF_UNICODETEXT&&f!=CF_TEXT&&f!=CF_OEMTEXT&&f!=CF_LOCALE&&f!=CF_DIB&&f!=CF_DIBV5&&f!=CF_HDROP)return false;
  HANDLE h=GetClipboardData(f);if(!h)return false;SIZE_T n=GlobalSize(h);if(!n||n>32*1024*1024)return false;
  auto p=static_cast<BYTE*>(GlobalLock(h));if(!p)return false;data.push_back({f,std::vector<BYTE>(p,p+n)});GlobalUnlock(h);
 }
 return GetLastError()==0;
}
std::wstring clipboardText(){auto h=GetClipboardData(CF_UNICODETEXT);if(!h)return {};auto p=static_cast<wchar_t*>(GlobalLock(h));if(!p)return {};size_t max=GlobalSize(h)/sizeof(wchar_t),n=0;while(n<max&&p[n])n++;std::wstring s(p,n);GlobalUnlock(h);return s;}
}
API int PaneKey(unsigned short vk,int down,int unicode){INPUT i{};i.type=INPUT_KEYBOARD;i.ki.dwExtraInfo=pane::InputTag;
 if(unicode){i.ki.wScan=vk;i.ki.dwFlags=KEYEVENTF_UNICODE;}else{i.ki.wVk=vk;if(vk==VK_RCONTROL||vk==VK_RMENU||(vk>=VK_PRIOR&&vk<=VK_DELETE)||vk==VK_LWIN||vk==VK_RWIN)i.ki.dwFlags|=KEYEVENTF_EXTENDEDKEY;}
 if(!down)i.ki.dwFlags|=KEYEVENTF_KEYUP;return SendInput(1,&i,sizeof(i))==1;
}
API int PaneReplay(int button){if(pane::inputBypass)return 0;pane::SimulationScope simulation;INPUT input[2]{};for(auto& i:input){i.type=INPUT_MOUSE;i.mi.dwExtraInfo=pane::InputTag;i.mi.mouseData=button==4?1:button==8?2:0;}
 input[0].mi.dwFlags=button==1?MOUSEEVENTF_RIGHTDOWN:button==2?MOUSEEVENTF_MIDDLEDOWN:MOUSEEVENTF_XDOWN;
 input[1].mi.dwFlags=button==1?MOUSEEVENTF_RIGHTUP:button==2?MOUSEEVENTF_MIDDLEUP:MOUSEEVENTF_XUP;return SendInput(2,input,sizeof(INPUT))==2;
}
// 保留按下位置；异步补发前移开超过原门槛时不把指针拉回。
API int PaneReplayAt(int button,int x,int y,unsigned dpi){
 POINT cursor{};if(!GetCursorPos(&cursor))return 0;
 int left=GetSystemMetrics(SM_XVIRTUALSCREEN),top=GetSystemMetrics(SM_YVIRTUALSCREEN);
 int width=GetSystemMetrics(SM_CXVIRTUALSCREEN),height=GetSystemMetrics(SM_CYVIRTUALSCREEN);
 INPUT inputs[2]{};
 if(!replayInputs(inputs,button,x,y,dpi,cursor,{left,top,left+width,top+height}))return 0;
 pane::SimulationScope simulation;
 if(pane::inputBypass)return 0;
 return SendInput(2,inputs,sizeof(INPUT))==2;
}
API int PaneWindow(HWND h,int action){
 if(!IsWindow(h))return 0;
 if(action==0){WINDOWPLACEMENT p{sizeof(p)};GetWindowPlacement(h,&p);p.showCmd=IsZoomed(h)?SW_SHOWNOACTIVATE:SW_SHOWMAXIMIZED;return SetWindowPlacement(h,&p);}
 if(action==1)return ShowWindowAsync(h,SW_SHOWMINNOACTIVE);
 if(action==2)return PostMessageW(h,WM_CLOSE,0,0);
 if(action==3)return SetWindowPos(h,(GetWindowLongPtrW(h,GWL_EXSTYLE)&WS_EX_TOPMOST)?HWND_NOTOPMOST:HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
 MONITORINFO info{sizeof(info)};if(!GetMonitorInfoW(MonitorFromWindow(h,MONITOR_DEFAULTTONEAREST),&info))return 0;
 if(IsZoomed(h)||IsIconic(h)){WINDOWPLACEMENT p{sizeof(p)};if(!GetWindowPlacement(h,&p))return 0;p.showCmd=SW_SHOWNOACTIVATE;if(!SetWindowPlacement(h,&p))return 0;}
 auto r=info.rcWork;int half=(r.right-r.left)/2;
 return SetWindowPos(h,nullptr,action==4?r.left:r.left+half,r.top,half,r.bottom-r.top,SWP_NOZORDER|SWP_NOACTIVATE);
}
API int PaneExplorer(HWND target,wchar_t* output,int capacity){Apartment apartment;ComPtr<IShellWindows> windows;
 if(FAILED(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&windows))))return 0;
 long count=0;windows->get_Count(&count);
 for(long i=0;i<count;i++){VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;if(FAILED(windows->Item(index,&dispatch)))continue;
 ComPtr<IWebBrowser2> browser;if(FAILED(dispatch.As(&browser)))continue;SHANDLE_PTR h=0;browser->get_HWND(&h);if(reinterpret_cast<HWND>(h)!=target)continue;
 BSTR url=nullptr;if(SUCCEEDED(browser->get_LocationURL(&url))&&url){DWORD n=capacity;HRESULT hr=PathCreateFromUrlW(url,output,&n,0);SysFreeString(url);return SUCCEEDED(hr);}}
 return 0;
}
API int PaneSelection(HWND target,wchar_t* output,int capacity){
 if(selectionBusy.test_and_set())return -6;SelectionLease lease;
 auto epoch=commandEpoch.load();auto began=GetTickCount64();
 Apartment apartment;ComPtr<IUIAutomation> automation;std::wstring value;
 if(SUCCEEDED(CoCreateInstance(CLSID_CUIAutomation,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation)))){
  ComPtr<IUIAutomationElement> focus;ComPtr<IUIAutomationTextPattern> pattern;ComPtr<IUIAutomationTextRangeArray> selection;
  if(GetForegroundWindow()==target&&SUCCEEDED(automation->GetFocusedElement(&focus))&&focus&&SUCCEEDED(focus->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&pattern)))&&pattern&&SUCCEEDED(pattern->GetSelection(&selection))&&selection){
   int n=0;selection->get_Length(&n);if(n>0){ComPtr<IUIAutomationTextRange> range;BSTR text=nullptr;if(SUCCEEDED(selection->GetElement(0,&range))&&SUCCEEDED(range->GetText(capacity-1,&text))&&text){value=text;SysFreeString(text);}}
  }
 }
 if(commandEpoch!=epoch||GetTickCount64()-began>700)return -2;
 if(!value.empty()){if(GetForegroundWindow()!=target)return -2;wcsncpy_s(output,capacity,value.c_str(),_TRUNCATE);return 1;}
 if(GetForegroundWindow()!=target)return -2;
 HWND owner=CreateWindowExW(0,L"STATIC",L"",0,0,0,0,0,HWND_MESSAGE,nullptr,GetModuleHandleW(nullptr),nullptr);
 if(!owner)return -3;
 std::vector<Snapshot> saved;
 if(!OpenClipboard(owner)){DestroyWindow(owner);return -3;}
 DWORD before=GetClipboardSequenceNumber();bool complete=snapshot(saved);CloseClipboard();
 if(!complete||GetForegroundWindow()!=target||GetClipboardSequenceNumber()!=before||commandEpoch!=epoch||GetTickCount64()-began>700){DestroyWindow(owner);return -4;}
 // 不清空剪贴板；先完整拍快照，复制后仅在序列号与焦点均未变化时恢复。
 std::unique_lock<std::recursive_timed_mutex> inputLock(inputMutex,std::defer_lock);
 if(!inputLock.try_lock_for(std::chrono::milliseconds(500))||commandEpoch!=epoch||GetForegroundWindow()!=target||GetClipboardSequenceNumber()!=before){DestroyWindow(owner);return -4;}
 std::vector<WORD> held;for(int k:{VK_LSHIFT,VK_RSHIFT,VK_LCONTROL,VK_RCONTROL,VK_LMENU,VK_RMENU,VK_LWIN,VK_RWIN})if(GetAsyncKeyState(k)&0x8000)held.push_back(static_cast<WORD>(k));
 bool ok=true;for(auto k:held)ok=key(k,false)&&ok;
 ok=key(VK_LCONTROL,true)&&ok;ok=key('C',true)&&ok;ok=key('C',false)&&ok;ok=key(VK_LCONTROL,false)&&ok;for(auto k:held)ok=key(k,true)&&ok;
 inputLock.unlock();
 if(!ok){DestroyWindow(owner);return -5;}
 auto start=GetTickCount64();while(GetClipboardSequenceNumber()==before&&GetTickCount64()-start<700&&GetForegroundWindow()==target&&commandEpoch==epoch)Sleep(10);
 DWORD copied=GetClipboardSequenceNumber();
 if(copied==before||GetForegroundWindow()!=target){DestroyWindow(owner);return -2;}
 if(!OpenClipboard(owner)){DestroyWindow(owner);return -3;}
 if(GetClipboardSequenceNumber()!=copied){CloseClipboard();DestroyWindow(owner);return -4;}
 value=clipboardText();
 std::vector<HGLOBAL> memory;
 for(auto& s:saved){HGLOBAL h=GlobalAlloc(GMEM_MOVEABLE,s.bytes.size());if(!h){ok=false;break;}auto p=GlobalLock(h);if(!p){GlobalFree(h);ok=false;break;}memcpy(p,s.bytes.data(),s.bytes.size());GlobalUnlock(h);memory.push_back(h);}
 if(ok&&GetForegroundWindow()==target&&GetClipboardSequenceNumber()==copied&&EmptyClipboard()){
  for(size_t i=0;i<memory.size();i++){if(SetClipboardData(saved[i].format,memory[i]))memory[i]=nullptr;else ok=false;}
 }else ok=false;
 for(auto h:memory)if(h)GlobalFree(h);CloseClipboard();DestroyWindow(owner);
 if(!ok)return -4;if(value.empty())return 0;wcsncpy_s(output,capacity,value.c_str(),_TRUNCATE);return 1;
}
API void PaneCancelCommands(){commandEpoch++;}
API int PaneBeginInput(){return inputMutex.try_lock_for(std::chrono::milliseconds(500));}
API void PaneEndInput(){inputMutex.unlock();}
