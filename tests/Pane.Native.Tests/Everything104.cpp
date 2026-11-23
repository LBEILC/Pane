#include "../../src/Pane.Native/Launcher.cpp"
#include <cstdio>
#include <future>
#include <thread>
// 启动器翻译单元还声明了剪贴板入口；此探针只连接 IPC。
extern "C" int PaneClipWrite(const wchar_t*,const wchar_t*,const wchar_t*,int,int){std::abort();}

namespace {
HWND receiver;
DWORD serial;
bool acceptQuery=false;
void sendEmptyPage(HWND window,HWND destination,DWORD requestId){
    DWORD empty[5]={7,0,0,0x53,14};
    COPYDATASTRUCT reply{requestId,sizeof(empty),empty};
    SendMessageW(destination,WM_COPYDATA,reinterpret_cast<WPARAM>(window),reinterpret_cast<LPARAM>(&reply));
}
LRESULT CALLBACK respond(HWND window,UINT message,WPARAM w,LPARAM l){
    if(message==WM_COPYDATA){
        if(!acceptQuery)return FALSE;
        auto packet=reinterpret_cast<COPYDATASTRUCT*>(l);
        serial=static_cast<DWORD*>(packet->lpData)[1];receiver=reinterpret_cast<HWND>(w);
        SetTimer(window,1,225,nullptr);
        return TRUE;
    }
    if(message==WM_TIMER){
        KillTimer(window,1);
        sendEmptyPage(window,receiver,serial);
        return 0;
    }
    return DefWindowProcW(window,message,w,l);
}
LRESULT CALLBACK respondSynchronously(HWND window,UINT message,WPARAM w,LPARAM l){
    if(message==WM_COPYDATA){
        if(GetWindowLongPtrW(window,GWLP_USERDATA))return FALSE;
        auto packet=reinterpret_cast<const COPYDATASTRUCT*>(l);
        DWORD requestId=static_cast<const DWORD*>(packet->lpData)[1];
        Sleep(225);
        sendEmptyPage(window,reinterpret_cast<HWND>(w),requestId);
        return TRUE;
    }
    return DefWindowProcW(window,message,w,l);
}
// 同线程直接调用窗口过程，无法检验发送超时。用独立线程模拟 Everything。
int synchronousQuery(DWORD budget,bool reject,int& total){
    std::promise<HWND> ready;
    auto endpointReady=ready.get_future();
    std::thread server([&]{
        HWND window=CreateWindowW(L"PaneTest104Sync",L"",0,0,0,0,0,HWND_MESSAGE,nullptr,GetModuleHandleW(nullptr),nullptr);
        if(window)SetWindowLongPtrW(window,GWLP_USERDATA,reject);
        ready.set_value(window);
        if(!window)return;
        MSG msg;
        while(GetMessageW(&msg,nullptr,0,0)>0)DispatchMessageW(&msg);
        DestroyWindow(window);
    });
    HWND window=endpointReady.get();
    int info[5]{};
    int result=queryPage(window,L"",0,0,14,nullptr,&total,budget,nullptr,info);
    if(window)PostThreadMessageW(GetWindowThreadProcessId(window,nullptr),WM_QUIT,0,0);
    server.join();
    return result;
}
}
int main(){
    WNDCLASSW wc{};wc.lpfnWndProc=respond;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaneTest104";
    if(!RegisterClassW(&wc))return 2;
    auto target=CreateWindowW(wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
    if(!target)return 2;
    int total=0,info[5]{},checks=0,failed=0;
    auto check=[&](bool ok){++checks;if(!ok)++failed;};
    auto query=[&](HWND window,DWORD budget,HANDLE cancel=nullptr){
        return queryPage(window,L"",0,0,14,nullptr,&total,budget,cancel,info);
    };
    check(query(nullptr,3000)==0);
    check(query(target,3000)==-1);
    acceptQuery=true;
    check(query(target,150)==-2);
    check(query(target,3000)==1&&total==7);
    HANDLE cancel=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    // 等待期间由调用方置位，提前结束三秒预算。
    std::thread caller([cancel]{Sleep(40);SetEvent(cancel);});
    auto start=GetTickCount64();check(query(target,3000,cancel)==-3&&GetTickCount64()-start<200);
    caller.join();CloseHandle(cancel);DestroyWindow(target);
    wc.lpfnWndProc=respondSynchronously;wc.lpszClassName=L"PaneTest104Sync";
    if(!RegisterClassW(&wc))return 2;
    check(synchronousQuery(150,false,total)==-2);
    total=0;
    check(synchronousQuery(3000,false,total)==1&&total==7);
    check(synchronousQuery(3000,true,total)==-1);
    std::printf("Everything IPC assertions=%d failures=%d\n",checks,failed);
    return failed?1:0;
}
