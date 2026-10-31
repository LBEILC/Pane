#pragma once
#include <algorithm>
#pragma comment(lib,"advapi32.lib")
// 句柄保留进程对象，创建时间区分实例；退出的实例不能再次命中。
struct ProcessIdentity {
 DWORD pid{};FILETIME created{};HANDLE process{};std::wstring path;DWORD integrity{};bool known{};
 ~ProcessIdentity(){if(process)CloseHandle(process);}
};
struct IdentitySnapshot {
 std::vector<std::shared_ptr<ProcessIdentity>> processes;
 std::vector<std::pair<HMONITOR,unsigned>> monitors;
};
inline DWORD processIntegrity(HANDLE process){
 HANDLE token{};if(!OpenProcessToken(process,TOKEN_QUERY,&token))return 0;
 DWORD size=0;GetTokenInformation(token,TokenIntegrityLevel,nullptr,0,&size);
 std::vector<BYTE> bytes(size);DWORD level=0;
 if(size&&GetTokenInformation(token,TokenIntegrityLevel,bytes.data(),size,&size)){
  auto sid=reinterpret_cast<TOKEN_MANDATORY_LABEL*>(bytes.data())->Label.Sid;
  if(IsValidSid(sid))level=*GetSidSubAuthority(sid,*GetSidSubAuthorityCount(sid)-1);
 }
 CloseHandle(token);return level;
}
inline std::shared_ptr<ProcessIdentity> queryProcessIdentity(DWORD pid){
 auto value=std::make_shared<ProcessIdentity>();value->pid=pid;
 value->process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);
 if(value->process){FILETIME exit{},kernel{},user{};
  if(!GetProcessTimes(value->process,&value->created,&exit,&kernel,&user))return value;
  wchar_t path[32768]{};DWORD size=32768;
  if(QueryFullProcessImageNameW(value->process,0,path,&size))value->path.assign(path,size);
  value->integrity=processIntegrity(value->process);value->known=value->integrity!=0;
 }
 return value;
}
inline const ProcessIdentity* findIdentity(const IdentitySnapshot& cache,DWORD pid){
 for(const auto& entry:cache.processes)
  if(entry->pid==pid&&entry->process&&WaitForSingleObject(entry->process,0)==WAIT_TIMEOUT)return entry.get();
 return nullptr;
}
