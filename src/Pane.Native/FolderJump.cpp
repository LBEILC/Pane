#define NOMINMAX
#include <windows.h>
#include <exdisp.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <thread>
#include <mutex>
#include <map>
#include <memory>
#include "FolderJumpPolicy.h"
using Microsoft::WRL::ComPtr;
using namespace pane::folderjump;
#define API extern "C" __declspec(dllexport)
namespace {
struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();} };
std::wstring Class(HWND h){wchar_t text[128]{};GetClassNameW(h,text,128);return text;}
HWND Filename(HWND h){
    std::vector<Control> children;
    EnumChildWindows(h,[](HWND child,LPARAM data)->BOOL{auto& list=*reinterpret_cast<std::vector<Control>*>(data);list.push_back({child,GetParent(child),Class(child),GetDlgCtrlID(child)});return TRUE;},reinterpret_cast<LPARAM>(&children));
    return pane::folderjump::Filename(Class(h),children);
}
DWORD Integrity(HANDLE process){
    HANDLE token=nullptr;if(!OpenProcessToken(process,TOKEN_QUERY,&token))return MAXDWORD;
    DWORD size=0;GetTokenInformation(token,TokenIntegrityLevel,nullptr,0,&size);std::vector<BYTE> bytes(size);DWORD level=MAXDWORD;
    if(size&&GetTokenInformation(token,TokenIntegrityLevel,bytes.data(),size,&size)){
        auto label=reinterpret_cast<TOKEN_MANDATORY_LABEL*>(bytes.data());auto sid=label->Label.Sid;
        level=*GetSidSubAuthority(sid,*GetSidSubAuthorityCount(sid)-1);
    }CloseHandle(token);return level;
}
bool Accessible(HWND h){
    DWORD pid=0;GetWindowThreadProcessId(h,&pid);HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!process)return false;
    DWORD target=Integrity(process),self=Integrity(GetCurrentProcess());CloseHandle(process);return target!=MAXDWORD&&self!=MAXDWORD&&target<=self;
}
bool Send(HWND h,UINT message,WPARAM w,LPARAM l,DWORD_PTR& result){return SendMessageTimeoutW(h,message,w,l,SMTO_ABORTIFHUNG|SMTO_BLOCK,1500,&result)!=0;}
bool Jump(HWND dialog,const wchar_t* path){
    if(!path||!PathIsDirectoryW(path)||!Accessible(dialog))return false;
    HWND edit=Filename(dialog);if(!edit||!IsWindowEnabled(dialog))return false;
    DWORD pid=0;DWORD thread=GetWindowThreadProcessId(dialog,&pid);
    DWORD_PTR result=0;if(!Send(edit,WM_GETTEXTLENGTH,0,0,result)||result>32767)return false;
    std::vector<wchar_t> original(result+1);
    if(!Send(edit,WM_GETTEXT,original.size(),reinterpret_cast<LPARAM>(original.data()),result))return false;
    // 尾斜杠明确表示目录，避免目标名称被当成文件提交。消息定向发送，不借用剪贴板或全局键盘。
    std::wstring folder(path);if(folder.back()!=L'\\')folder+=L'\\';
    if(!Send(edit,WM_SETTEXT,0,reinterpret_cast<LPARAM>(folder.c_str()),result)||!result)return false;
    bool sent=Send(dialog,WM_COMMAND,MAKEWPARAM(IDOK,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(dialog,IDOK)),result);
    // 系统对话框会把目录导航排入自己的消息队列；等它消费输入后再还原。
    // 仅在本次跳转期间短暂等待，空闲时没有定时器或周期唤醒。
    if(sent){
        auto deadline=GetTickCount64()+2000;
        while(IsWindow(edit)&&GetTickCount64()<deadline){
            wchar_t text[32768]{};
            if(!Send(edit,WM_GETTEXT,32768,reinterpret_cast<LPARAM>(text),result)||folder!=text)break;
            Sleep(20);
        }
    }
    DWORD nowPid=0;bool alive=GetWindowThreadProcessId(dialog,&nowPid)==thread&&nowPid==pid&&Filename(dialog)==edit;
    bool restored=alive&&Send(edit,WM_SETTEXT,0,reinterpret_cast<LPARAM>(original.data()),result)&&result;
    if(sent&&restored)SetForegroundWindow(dialog);
    return sent&&restored;
}
using Trigger=void(__stdcall*)(HWND,int,int,int);
struct Service;
thread_local Service* current=nullptr;
struct Service {
    Trigger trigger;std::thread worker;DWORD thread=0;HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);HWINEVENTHOOK hook=nullptr;
    Hotkeys keys;std::mutex mutex;std::map<HWND,unsigned long long> activated;unsigned long long sequence=0;
    explicit Service(Trigger cb):trigger(cb){}
    void Foreground(HWND window){
        // 事件可能排队；始终重新确认当前前台，避免把热键留给已经离开的对话框。
        window=GetForegroundWindow();auto name=Class(window);
        if(name==L"CabinetWClass"||name==L"ExploreWClass"){
            std::lock_guard<std::mutex> lock(mutex);
            for(auto i=activated.begin();i!=activated.end();)if(!IsWindow(i->first))i=activated.erase(i);else ++i;
            activated[window]=++sequence;
        }
        keys.Change(Filename(window)?window:nullptr,[](int id){return RegisterHotKey(nullptr,id,MOD_CONTROL|MOD_NOREPEAT|(id==2?MOD_SHIFT:0),'G')!=0;},[](int id){UnregisterHotKey(nullptr,id);});
    }
    void Run(){
        current=this;thread=GetCurrentThreadId();MSG msg{};PeekMessageW(&msg,nullptr,WM_USER,WM_USER,PM_NOREMOVE);
        // 启动前的激活事件无法追溯，用现有窗口 Z 序作初始顺序；之后只记前台事件。
        std::vector<HWND> existing;
        EnumWindows([](HWND window,LPARAM data)->BOOL{auto type=Class(window);if(type==L"CabinetWClass"||type==L"ExploreWClass")reinterpret_cast<std::vector<HWND>*>(data)->push_back(window);return TRUE;},reinterpret_cast<LPARAM>(&existing));
        for(auto i=existing.rbegin();i!=existing.rend();++i)activated[*i]=++sequence;
        hook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,nullptr,[](HWINEVENTHOOK,DWORD,HWND window,LONG,LONG,DWORD,DWORD){if(current)current->Foreground(window);},0,0,WINEVENT_OUTOFCONTEXT);
        if(hook)Foreground(GetForegroundWindow());SetEvent(ready);
        while(GetMessageW(&msg,nullptr,0,0)>0){
            if(msg.message==WM_HOTKEY&&keys.dialog&&GetForegroundWindow()==keys.dialog){
                auto edit=Filename(keys.dialog);RECT r{};
                if(edit&&GetWindowRect(edit,&r))trigger(keys.dialog,static_cast<int>(msg.wParam),r.left,r.bottom);
            }else{TranslateMessage(&msg);DispatchMessageW(&msg);}
        }
        keys.Change(nullptr,[](int){return false;},[](int id){UnregisterHotKey(nullptr,id);});
        if(hook)UnhookWinEvent(hook);current=nullptr;
    }
    ~Service(){if(worker.joinable()){PostThreadMessageW(thread,WM_QUIT,0,0);worker.join();}CloseHandle(ready);}
};
std::wstring Display(const std::wstring& path){
    ComPtr<IShellItem> item;PWSTR text=nullptr;
    if(SUCCEEDED(SHCreateItemFromParsingName(path.c_str(),nullptr,IID_PPV_ARGS(&item)))&&SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY,&text))){std::wstring result(text);CoTaskMemFree(text);return result;}
    return PathFindFileNameW(path.c_str());
}
std::vector<Entry> Entries(Service* service){
    Apartment apartment;std::vector<Entry> entries;std::map<HWND,unsigned long long> activated;
    {std::lock_guard<std::mutex> lock(service->mutex);activated=service->activated;}
    ComPtr<IShellWindows> windows;
    if(SUCCEEDED(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&windows)))){
        long count=0;windows->get_Count(&count);
        for(long i=0;i<count;i++){
            VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;ComPtr<IWebBrowser2> browser;
            if(FAILED(windows->Item(index,&dispatch))||!dispatch||FAILED(dispatch.As(&browser)))continue;
            SHANDLE_PTR handle=0;browser->get_HWND(&handle);auto window=reinterpret_cast<HWND>(handle);auto type=Class(window);
            if(type!=L"CabinetWClass"&&type!=L"ExploreWClass")continue;
            BSTR url=nullptr;if(SUCCEEDED(browser->get_LocationURL(&url))&&url){
                wchar_t path[32768]{};DWORD size=32768;
                if(SUCCEEDED(PathCreateFromUrlW(url,path,&size,0)))entries.push_back({window,path,L"",false,activated[window]});
                SysFreeString(url);
            }
        }
    }
    PWSTR recent=nullptr;
    if(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Recent,KF_FLAG_DEFAULT,nullptr,&recent))){
        std::wstring root(recent);CoTaskMemFree(recent);WIN32_FIND_DATAW data{};
        HANDLE find=FindFirstFileW((root+L"\\*.lnk").c_str(),&data);
        if(find!=INVALID_HANDLE_VALUE){do{
            if(data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)continue;
            ComPtr<IShellLinkW> link;ComPtr<IPersistFile> file;
            if(FAILED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link)))||FAILED(link.As(&file))||FAILED(file->Load((root+L"\\"+data.cFileName).c_str(),STGM_READ)))continue;
            // 不 Resolve：不搜索失效目标，不弹 Shell 提示。
            wchar_t path[32768]{};if(SUCCEEDED(link->GetPath(path,32768,nullptr,SLGP_RAWPATH)))entries.push_back({nullptr,path,L"",true,(static_cast<unsigned long long>(data.ftLastWriteTime.dwHighDateTime)<<32)|data.ftLastWriteTime.dwLowDateTime});
        }while(FindNextFileW(find,&data));FindClose(find);}
    }
    auto result=Select(std::move(entries),[](const std::wstring& path){return PathIsDirectoryW(path.c_str())!=FALSE;});
    for(auto& e:result)e.name=Display(e.path);return result;
}
}
API void* PaneFolderJumpStart(Trigger trigger){
    try{auto service=std::make_unique<Service>(trigger);service->worker=std::thread([s=service.get()]{s->Run();});WaitForSingleObject(service->ready,INFINITE);if(!service->hook)return nullptr;return service.release();}catch(...){return nullptr;}
}
API void PaneFolderJumpStop(void* value){delete static_cast<Service*>(value);}
using EntryCallback=void(__stdcall*)(const wchar_t*,const wchar_t*,int);
API void PaneFolderJumpEntries(void* value,EntryCallback callback){try{for(const auto& e:Entries(static_cast<Service*>(value)))callback(e.path.c_str(),e.name.c_str(),e.recent?1:0);}catch(...){}}
API int PaneFolderJumpTo(HWND dialog,const wchar_t* path){try{return Jump(dialog,path)?1:0;}catch(...){return 0;}}
