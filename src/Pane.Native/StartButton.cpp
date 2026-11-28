#define NOMINMAX
#include <windows.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <mutex>
#include <thread>
#include <vector>
#include "StartButton.h"
using Microsoft::WRL::ComPtr;
namespace {
std::mutex lock;
std::vector<RECT> rects;
std::thread watcher;
HANDLE stop=nullptr;
std::vector<HWND> taskbars(){
 std::vector<HWND> out;
 if(HWND main=FindWindowW(L"Shell_TrayWnd",nullptr))out.push_back(main);
 for(HWND h=nullptr;(h=FindWindowExW(nullptr,h,L"Shell_SecondaryTrayWnd",nullptr))!=nullptr;)out.push_back(h);
 return out;
}
std::vector<RECT> find(IUIAutomation* automation,IUIAutomationCondition* condition){
 std::vector<RECT> out;
 for(HWND bar:taskbars()){
  ComPtr<IUIAutomationElement> root,button;RECT r{};
  if(FAILED(automation->ElementFromHandle(bar,&root))||!root)continue;
  if(FAILED(root->FindFirst(TreeScope_Descendants,condition,&button))||!button)continue;
  if(SUCCEEDED(button->get_CurrentBoundingRectangle(&r))&&r.right>r.left&&r.bottom>r.top)out.push_back(r);
 }
 return out;
}
bool taskbarClass(HWND window){
 wchar_t name[64]{};GetClassNameW(GetAncestor(window,GA_ROOT),name,64);
 return wcscmp(name,L"Shell_TrayWnd")==0||wcscmp(name,L"Shell_SecondaryTrayWnd")==0;
}
}
void pane::StartButtonsWatch(bool on){
 if(watcher.joinable()){SetEvent(stop);watcher.join();}
 if(stop){CloseHandle(stop);stop=nullptr;}
 {std::lock_guard guard(lock);rects.clear();}
 if(!on)return;
 stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 if(!stop)return;
 HANDLE signal=stop;
 try {watcher=std::thread([signal]{
  SetThreadDescription(GetCurrentThread(),L"Pane 开始按钮");
  HRESULT apartment=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
  {
   ComPtr<IUIAutomation> automation;ComPtr<IUIAutomationCondition> condition;
   if(SUCCEEDED(CoCreateInstance(CLSID_CUIAutomation,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation)))){
    VARIANT id{};id.vt=VT_BSTR;id.bstrVal=SysAllocString(L"StartButton");
    if(id.bstrVal)automation->CreatePropertyCondition(UIA_AutomationIdPropertyId,id,&condition);
    VariantClear(&id);
    // 资源管理器重启、任务栏改对齐或换显示器后，下一轮就找到新位置。
    if(condition)do{auto found=find(automation.Get(),condition.Get());std::lock_guard guard(lock);rects=std::move(found);}
     while(WaitForSingleObject(signal,3000)==WAIT_TIMEOUT);
   }
  }
  if(SUCCEEDED(apartment))CoUninitialize();
 });}catch(...){CloseHandle(stop);stop=nullptr;}
}
bool pane::StartButtonHit(POINT point){
 {
  std::unique_lock guard(lock,std::try_to_lock);
  if(!guard.owns_lock())return false;
  bool inside=false;
  for(auto& r:rects)if(PtInRect(&r,point)){inside=true;break;}
  if(!inside)return false;
 }
 // 开始按钮上面盖着别的窗口（如全屏程序、开着的菜单）时不算。
 return taskbarClass(WindowFromPoint(point));
}
