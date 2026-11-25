#pragma once
#include <memory>
#include <algorithm>
#include <climits>
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
struct PressIdentity {std::wstring path,windowClass;bool higherIntegrity=false,integrityKnown=false;};
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
inline bool samePrefix(const std::wstring& a,const std::wstring& b,size_t length){
 return length<=a.size()&&length<=b.size()&&
  CompareStringOrdinal(a.data(),static_cast<int>(length),b.data(),static_cast<int>(length),TRUE)==CSTR_EQUAL;
}
inline std::wstring normalizeProgramPath(std::wstring path){
 if(path.find_first_not_of(L" \t\r\n")==std::wstring::npos)return {};
 std::replace(path.begin(),path.end(),L'/',L'\\');
 if(samePrefix(path,L"\\\\?\\UNC\\",8))path=L"\\\\"+path.substr(8);
 else if(path.size()>6&&path.compare(0,4,L"\\\\?\\")==0&&path[5]==L':')path.erase(0,4);
 DWORD capacity=GetFullPathNameW(path.c_str(),0,nullptr,nullptr);
 if(!capacity)return {};
 std::wstring full(capacity,L'\0');
 DWORD length=GetFullPathNameW(path.c_str(),capacity,full.data(),nullptr);
 if(!length||length>=capacity)return {};
 full.resize(length);
 while(!full.empty()&&full.back()==L'\\')full.pop_back();
 return full;
}
inline int programMatchRank(const std::wstring& rule,const std::wstring& target){
 if(rule.empty()||target.empty())return 0;
 if(rule.size()==target.size()&&samePrefix(rule,target,rule.size()))return INT_MAX;
 auto end=rule.rfind(L'\\');
 if(end==std::wstring::npos||end==0||(end==2&&rule[1]==L':'))return 0;
 static const std::wstring windows=[]{
  wchar_t path[MAX_PATH+1]{};
  UINT length=GetWindowsDirectoryW(path,MAX_PATH+1);
  return length&&length<=MAX_PATH?normalizeProgramPath(path):std::wstring{};
 }();
 if(windows.empty()||(windows.size()<=end&&rule[windows.size()]==L'\\'&&samePrefix(rule,windows,windows.size())))return 0;
 // 同时命中的文件夹都在目标路径上，前缀越长，层级越深。
 return target.size()>end&&target[end]==L'\\'&&samePrefix(rule,target,end)?static_cast<int>(end):0;
}
inline bool pressFullscreen(const PolicyGeometry& g,const wchar_t* name){
 return g.valid&&wcscmp(name,L"Progman")!=0&&wcscmp(name,L"WorkerW")!=0
  &&g.rect.left<=g.monitor.left&&g.rect.top<=g.monitor.top&&g.rect.right>=g.monitor.right&&g.rect.bottom>=g.monitor.bottom;
}
struct PressDecision {int enabled,superButton;};
inline PressDecision decidePress(const PressPolicy& policy,const PolicyGeometry& g,const PressIdentity& identity){
 bool full=pressFullscreen(g,identity.windowClass.c_str());
 bool disabled=false,superDisabled=false;const PressRule* matched=nullptr;
 int bestRank=0;
 auto target=policy.rules.empty()?std::wstring{}:normalizeProgramPath(identity.path);
 for(const auto& rule:policy.rules){
  int rank=programMatchRank(rule.path,target);
  if(!rank)continue;
  if(rule.super){superDisabled=true;continue;}
  if(rule.hasClass&&rule.windowClass!=identity.windowClass)continue;
  if(rank>bestRank||(rank==bestRank&&rule.hasClass&&!matched->hasClass)){matched=&rule;bestRank=rank;}
 }
 if(matched)disabled=matched->disabled;
 return {policy.paused||disabled||(policy.fullscreen&&full)?0:policy.enabled,
  policy.paused||superDisabled||(policy.superFullscreen&&full)?0:policy.superButton};
}
