#define NOMINMAX
#include "../../src/Pane.Native/LauncherOpenPolicy.h"
#include <cstdio>
#include <stdexcept>
#include <cwchar>
#include <initializer_list>

int checks=0;
void check(bool value,const char* label){++checks;if(!value)throw std::runtime_error(label);}
int main(){try{
 using namespace pane;
 using A=LauncherOpenAction;
 check(wcscmp(launcherOpenVerb(A::Open),L"open")==0,"first verb is open");
 check(launcherOpenVerb(A::Default)==nullptr,"retry uses default verb");
 check(nextLauncherOpenAction(A::Open,true,ERROR_NO_ASSOCIATION,SE_ERR_NOASSOC,false)==A::Stop,"success never retries");
 check(nextLauncherOpenAction(A::Open,false,ERROR_NO_ASSOCIATION,SE_ERR_NOASSOC,false)==A::Default,"no association retries");
 check(nextLauncherOpenAction(A::Open,false,ERROR_NO_ASSOCIATION,0,false)==A::Default,"precise error suffices");
 check(nextLauncherOpenAction(A::Open,false,ERROR_SUCCESS,SE_ERR_NOASSOC,false)==A::Default,"legacy fallback");
 for(DWORD error:{ERROR_CANCELLED,ERROR_ACCESS_DENIED,ERROR_FILE_NOT_FOUND,ERROR_PATH_NOT_FOUND,ERROR_DDE_FAIL,ERROR_GEN_FAILURE,ERROR_SHARING_VIOLATION,ERROR_SUCCESS}){
  check(nextLauncherOpenAction(A::Open,false,error,0,false)==A::Stop,"other errors do not retry");
  if(error!=ERROR_SUCCESS)check(nextLauncherOpenAction(A::Open,false,error,SE_ERR_NOASSOC,false)==A::Stop,"precise error takes priority");
 }
 for(bool success:{false,true})for(DWORD error:{ERROR_SUCCESS,ERROR_NO_ASSOCIATION,ERROR_CANCELLED}){
  check(nextLauncherOpenAction(A::Default,success,error,SE_ERR_NOASSOC,false)==A::Stop,"at most one retry");
  check(nextLauncherOpenAction(A::Stop,success,error,SE_ERR_NOASSOC,false)==A::Stop,"stopped stays stopped");
 }
 check(nextLauncherOpenAction(A::Open,false,ERROR_NO_ASSOCIATION,SE_ERR_NOASSOC,true)==A::Stop,"shell location unchanged");
 printf("PASS launcher open policy assertions=%d; shellExecutions=0; windows=0\n",checks);return 0;
 }catch(const std::exception& e){printf("FAIL %s\n",e.what());return 1;}}
