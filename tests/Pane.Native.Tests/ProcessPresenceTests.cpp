#include <windows.h>
#include <iostream>
#include <stdexcept>
#include "../../src/Pane.Native/ProcessPresence.h"
const wchar_t* fixture=L"other.exe";NTSTATUS failure=0;bool malformed=false;int calls=0;
NTSTATUS NTAPI fakeQuery(SYSTEM_INFORMATION_CLASS kind,PVOID data,ULONG size,PULONG needed){
 calls++;if(kind!=SystemBasicProcessInformation)return -1;if(failure)return failure;
 const size_t length=wcslen(fixture)*sizeof(wchar_t);*needed=static_cast<ULONG>(sizeof(SYSTEM_BASICPROCESS_INFORMATION)+length);
 if(size<*needed)return static_cast<NTSTATUS>(0xC0000004L);
 auto row=static_cast<SYSTEM_BASICPROCESS_INFORMATION*>(data);*row={};row->ImageName.Length=static_cast<USHORT>(length);row->ImageName.Buffer=reinterpret_cast<PWSTR>(row+1);memcpy(row->ImageName.Buffer,fixture,length);
 if(malformed)row->ImageName.Buffer=reinterpret_cast<PWSTR>(1);return 0;
}
int checks=0;
void check(bool value,const char* name){checks++;if(!value)throw std::runtime_error(name);}
int main(){try{
 check(wgesturesPresent(fakeQuery)==0,"other process absent");fixture=L"WGesturesHelper.exe";check(wgesturesPresent(fakeQuery)==0,"exact executable only");fixture=L"wGESTURES.EXE";check(wgesturesPresent(fakeQuery)==1,"case insensitive match");
 malformed=true;check(wgesturesPresent(fakeQuery)==-1,"malformed name is not absence");malformed=false;failure=static_cast<NTSTATUS>(0xC0000003L);check(wgesturesPresent(fakeQuery)==-1,"old Windows requests managed fallback");failure=static_cast<NTSTATUS>(0xC0000022L);check(wgesturesPresent(fakeQuery)==-1,"access failure is not absence");check(wgesturesPresent(nullptr)==-1,"missing API requests fallback");check(calls==6,"one query per check");
 std::cout<<"PASS process presence assertions="<<checks<<"; realProcessChanges=0\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
