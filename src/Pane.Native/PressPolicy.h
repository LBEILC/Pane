#pragma once
#include <memory>
#include <string>
#include <vector>
#include <shellscalingapi.h>
#pragma comment(lib,"shcore.lib")

inline unsigned pressDpi(POINT point){
 // 沿用托管 UnderCursor 的显示器有效 DPI，不采用目标进程自己的 DPI 感知值。
 UINT x=96,y=96;
 if(FAILED(GetDpiForMonitor(MonitorFromPoint(point,MONITOR_DEFAULTTONEAREST),MDT_EFFECTIVE_DPI,&x,&y))||!x)return 96;
 return x;
}

// 配置由服务线程构造后一次发布；按下只读取完整快照，不回调或等待托管服务。
struct PressRule {std::wstring path,windowClass;bool hasClass{},disabled{},super{};};
struct PressPolicy {
 int enabled=1,timeout=0,superButton=4,hold=400;
 bool paused=false,fullscreen=true,superFullscreen=true;
 std::vector<PressRule> rules;
};
struct PressIdentity {std::wstring path,windowClass;};
inline PressIdentity pressIdentity(const PolicyGeometry& g,bool needPath){
 PressIdentity result;wchar_t cls[256]{};
 if(GetClassNameW(g.window,cls,256))result.windowClass=cls;
 if(needPath){
  HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,g.pid);
  if(process){wchar_t path[32768]{};DWORD size=32768;
   if(QueryFullProcessImageNameW(process,0,path,&size))result.path.assign(path,size);
   CloseHandle(process);
  }
 }
 return result;
}
inline bool samePath(const std::wstring& a,const std::wstring& b){
 return !a.empty()&&!b.empty()&&CompareStringOrdinal(a.c_str(),-1,b.c_str(),-1,TRUE)==CSTR_EQUAL;
}
struct PressDecision {int enabled,superButton;};
inline PressDecision decidePress(const PressPolicy& policy,const PolicyGeometry& g,const PressIdentity& identity){
 bool full=g.valid&&identity.windowClass!=L"Progman"&&identity.windowClass!=L"WorkerW"
  &&g.rect.left<=g.monitor.left&&g.rect.top<=g.monitor.top&&g.rect.right>=g.monitor.right&&g.rect.bottom>=g.monitor.bottom;
 bool disabled=false,superDisabled=false;const PressRule* matched=nullptr;
 for(const auto& rule:policy.rules){
  if(!samePath(rule.path,identity.path))continue;
  if(rule.super){superDisabled=true;continue;}
  if(rule.hasClass&&rule.windowClass!=identity.windowClass)continue;
  if(!matched||(rule.hasClass&&!matched->hasClass))matched=&rule;
 }
 if(matched)disabled=matched->disabled;
 return {policy.paused||disabled||(policy.fullscreen&&full)?0:policy.enabled,
  policy.paused||superDisabled||(policy.superFullscreen&&full)?0:policy.superButton};
}
