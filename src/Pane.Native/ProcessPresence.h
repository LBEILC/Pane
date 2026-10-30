#pragma once
#include <winternl.h>
#include <vector>
using ProcessQuery=NTSTATUS(NTAPI*)(SYSTEM_INFORMATION_CLASS,PVOID,ULONG,PULONG);
inline ProcessQuery processQuery(){
 auto address=GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtQuerySystemInformation");ProcessQuery query=nullptr;
 static_assert(sizeof(query)==sizeof(address));memcpy(&query,&address,sizeof(query));return query;
}
// Windows 11 新增的基础快照不采集线程计时，避免每秒同步所有处理器。
// 不支持此信息类的旧系统返回 -1，由托管端继续使用既有查询。
inline int wgesturesPresent(ProcessQuery query=processQuery()){
 if(!query)return -1;
 thread_local std::vector<BYTE> bytes(65536);ULONG needed=0;NTSTATUS status=0;
 for(int attempt=0;attempt<4;attempt++){
  status=query(SystemBasicProcessInformation,bytes.data(),static_cast<ULONG>(bytes.size()),&needed);
  if(status!=static_cast<NTSTATUS>(0xC0000004L))break;
  if(needed>32*1024*1024)return -1;
  bytes.resize(needed>bytes.size()?needed:bytes.size()*2);
 }
 if(status<0||needed>bytes.size()||needed<sizeof(SYSTEM_BASICPROCESS_INFORMATION))return -1;
 constexpr wchar_t name[]=L"WGestures.exe";
 for(size_t offset=0;offset+sizeof(SYSTEM_BASICPROCESS_INFORMATION)<=needed;){
  auto& row=*reinterpret_cast<const SYSTEM_BASICPROCESS_INFORMATION*>(bytes.data()+offset);
  auto begin=reinterpret_cast<uintptr_t>(bytes.data()),text=reinterpret_cast<uintptr_t>(row.ImageName.Buffer);
  if(row.ImageName.Length){
   if(text<begin||text>begin+needed||row.ImageName.Length>begin+needed-text||row.ImageName.Length%sizeof(wchar_t))return -1;
   if(row.ImageName.Length==sizeof(name)-sizeof(wchar_t)&&_wcsnicmp(row.ImageName.Buffer,name,row.ImageName.Length/sizeof(wchar_t))==0)return 1;
  }
  if(!row.NextEntryOffset)return 0;
  if(row.NextEntryOffset<sizeof(row)||row.NextEntryOffset>needed-offset)return -1;
  offset+=row.NextEntryOffset;
 }
 return -1;
}
