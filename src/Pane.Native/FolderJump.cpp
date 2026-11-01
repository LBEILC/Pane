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
#include <set>
#include <functional>
#include "FolderJumpPolicy.h"
using Microsoft::WRL::ComPtr;
using namespace pane::folderjump;
#define API extern "C" __declspec(dllexport)
namespace {
struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();} };
std::wstring Class(HWND h){wchar_t text[128]{};GetClassNameW(h,text,128);return text;}
HWND Filename(HWND h){
    std::vector<Control> children;
    EnumChildWindows(h,[](HWND child,LPARAM data)->BOOL{auto& list=*reinterpret_cast<std::vector<Control>*>(data);list.push_back({child,GetParent(child),Class(child),GetDlgCtrlID(child),IsWindowVisible(child)!=FALSE});return TRUE;},reinterpret_cast<LPARAM>(&children));
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
bool ReadFilename(HWND edit,std::wstring& text,UINT timeout=50){
    wchar_t buffer[32768]{};DWORD_PTR result=0;
    if(!SendMessageTimeoutW(edit,WM_GETTEXT,32768,reinterpret_cast<LPARAM>(buffer),SMTO_ABORTIFHUNG|SMTO_BLOCK,timeout,&result))return false;
    text=buffer;return true;
}
bool WriteFilename(HWND edit,const wchar_t* text){
    DWORD_PTR result=0;if(!Send(edit,WM_SETTEXT,0,reinterpret_cast<LPARAM>(text),result)||!result)return false;
    // WM_SETTEXT 不会产生组合框的编辑通知；另存为可能仍缓存 SetFileName 的预填值。
    auto combo=GetParent(edit);
    return Class(combo)!=L"ComboBox"||Send(GetParent(combo),WM_COMMAND,MAKEWPARAM(GetDlgCtrlID(combo),CBN_EDITCHANGE),reinterpret_cast<LPARAM>(combo),result);
}
struct InitialFilename {HWND edit=nullptr;std::wstring text;};
bool Jump(HWND dialog,const wchar_t* path,bool automatic=false,HWND (WINAPI *foreground)()=GetForegroundWindow,const InitialFilename* initial=nullptr){
    if(!path||!PathIsDirectoryW(path)||!Accessible(dialog))return false;
    HWND edit=Filename(dialog);if(!edit||!IsWindowEnabled(dialog))return false;
    DWORD pid=0;DWORD thread=GetWindowThreadProcessId(dialog,&pid);
    DWORD_PTR result=0;if(!Send(edit,WM_GETTEXTLENGTH,0,0,result)||result>32767)return false;
    std::vector<wchar_t> original(result+1);
    if(!Send(edit,WM_GETTEXT,original.size(),reinterpret_cast<LPARAM>(original.data()),result))return false;
    if(automatic&&(!initial||initial->edit!=edit||initial->text!=original.data()))return false;
    // 尾斜杠明确表示目录，避免目标名称被当成文件提交。消息定向发送，不借用剪贴板或全局键盘。
    std::wstring folder(path);if(folder.back()!=L'\\')folder+=L'\\';
    std::wstring latest;
    if(automatic&&(!ReadFilename(edit,latest)||latest!=initial->text||foreground()!=dialog))return false;
    bool written=WriteFilename(edit,folder.c_str());
    bool sent=written&&Send(dialog,WM_COMMAND,MAKEWPARAM(IDOK,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(dialog,IDOK)),result);
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
    // 自动跳转等待导航期间用户若输入新文字，不再用初始值覆盖。
    if(automatic&&alive){
        wchar_t text[32768]{};
        if(!Send(edit,WM_GETTEXT,32768,reinterpret_cast<LPARAM>(text),result))return false;
        if(text[0]&&folder!=text)return sent;
    }
    bool restored=alive&&WriteFilename(edit,original.data());
    if(sent&&restored&&(!automatic||foreground()==dialog))SetForegroundWindow(dialog);
    return sent&&restored;
}
using Trigger=void(__stdcall*)(HWND,int,int,int);
// 可选诊断文件由启动环境指定；不记录窗口标题、文件名或目录。
void Diagnostic(const std::wstring& message){
    auto line=L"Pane.FolderJump "+message+L"\r\n";OutputDebugStringW(line.c_str());
    wchar_t path[32768]{};DWORD size=GetEnvironmentVariableW(L"PANE_FOLDER_JUMP_LOG",path,32768);
    if(!size||size>=32768)return;
    HANDLE file=CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return;
    // 诊断字段均为 ASCII，文件可直接用文本编辑器查看。
    std::string bytes;for(auto c:line)bytes+=static_cast<char>(c);
    DWORD written=0;WriteFile(file,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr);CloseHandle(file);
}
struct KeyBinding {
    UINT modifiers=MOD_CONTROL,key='G';
    bool operator==(const KeyBinding& other)const{return modifiers==other.modifiers&&key==other.key;}
};
bool RegisterKey(int id,KeyBinding key={}){
    if(RegisterHotKey(nullptr,id,key.modifiers|MOD_NOREPEAT,key.key))return true;
    DWORD error=GetLastError();std::wstring name;
    if(key.modifiers&MOD_CONTROL)name+=L"Ctrl+";if(key.modifiers&MOD_ALT)name+=L"Alt+";
    if(key.modifiers&MOD_SHIFT)name+=L"Shift+";if(key.modifiers&MOD_WIN)name+=L"Win+";
    if(key.key>='A'&&key.key<='Z')name+=static_cast<wchar_t>(key.key);else name+=L"VK"+std::to_wstring(key.key);
    Diagnostic(L"RegisterHotKey failed key="+name+L" error="+std::to_wstring(error));return false;
}
struct Configuration {bool enabled=true,automatic=false;KeyBinding jump{},menu{MOD_CONTROL|MOD_SHIFT,'G'};};
struct ApplyRequest {Configuration config;HANDLE done;};
struct Service;
thread_local Service* current=nullptr;
struct Service {
    Trigger trigger;std::thread worker;DWORD thread=0;HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);HWINEVENTHOOK hook=nullptr,destroyHook=nullptr;
    Hotkeys keys;std::mutex mutex;std::map<HWND,unsigned long long> activated;unsigned long long sequence=0;
    std::function<bool(int,KeyBinding)> registerKey=RegisterKey;
    std::function<void(int)> unregisterKey=[](int id){UnregisterHotKey(nullptr,id);};
    Configuration config;std::set<HWND> seen;HWND pending=nullptr;ULONGLONG deadline=0;UINT_PTR timer=0;int problem=0;
    // 异步跳转线程只读取快照；销毁通知和首次捕获由事件线程维护。
    std::map<HWND,InitialFilename> initialFilenames;
    // 隐藏桌面夹具可替换查询；生产始终重新读取真实前台。
    HWND (WINAPI *foreground)()=GetForegroundWindow;
    explicit Service(Trigger cb):trigger(cb){}
    void Problem(int value){if(problem!=value){problem=value;trigger(nullptr,10,value,0);}}
    void CancelPending(){if(timer)KillTimer(nullptr,timer);timer=0;pending=nullptr;}
    bool AutomaticTo(HWND window,const wchar_t* path){
        InitialFilename initial;
        {std::lock_guard<std::mutex> lock(mutex);auto i=initialFilenames.find(window);if(i==initialFilenames.end())return false;initial=i->second;}
        return foreground()==window&&Jump(window,path,true,foreground,&initial);
    }
    void Register(HWND dialog){
        if(keys.dialog==dialog)return;int failed=0;
        keys.Change(dialog,[&](int id){
            if(id==2&&config.jump==config.menu)return false;
            bool ok=registerKey(id,id==1?config.jump:config.menu);if(!ok)failed|=id;return ok;
        },unregisterKey);
        if(dialog)Problem(failed);
    }
    void TryAutomatic(){
        if(!pending)return;HWND window=pending;
        if(!config.enabled||!config.automatic||foreground()!=window||!IsWindow(window)||GetTickCount64()>=deadline){CancelPending();return;}
        auto edit=Filename(window);if(!edit)return;
        std::wstring text;if(!ReadFilename(edit,text))return;
        {std::lock_guard<std::mutex> lock(mutex);initialFilenames.emplace(window,InitialFilename{edit,text});}
        Register(window);
        CancelPending();
        trigger(window,3,0,0);
    }
    void Foreground(HWND){
        if(!config.enabled)return;auto window=foreground();auto name=Class(window);
        if(pending&&pending!=window)CancelPending();
        if(name==L"CabinetWClass"||name==L"ExploreWClass"){
            std::lock_guard<std::mutex> lock(mutex);
            for(auto i=activated.begin();i!=activated.end();)if(!IsWindow(i->first))i=activated.erase(i);else ++i;
            activated[window]=++sequence;
        }
        auto dialog=Filename(window)?window:nullptr;
        if(dialog!=keys.dialog||(!dialog&&name==L"#32770"))Diagnostic(dialog?L"foreground recognized=1":L"foreground recognized=0");
        if(config.automatic&&name==L"#32770"&&seen.insert(window).second){
            pending=window;deadline=GetTickCount64()+1000;
            timer=SetTimer(nullptr,0,40,nullptr);TryAutomatic();
            if(!timer&&pending)CancelPending();
        }
        Register(dialog);
    }
    void Destroyed(HWND window){seen.erase(window);{std::lock_guard<std::mutex> lock(mutex);initialFilenames.erase(window);}if(pending==window)CancelPending();if(keys.dialog==window)Register(nullptr);}
    void Apply(Configuration next){
        bool changed=!(config.jump==next.jump)||!(config.menu==next.menu);
        if(changed||!next.enabled)Register(nullptr);
        if(changed||!next.enabled)Problem(0);config=next;
        if(!config.enabled){
            CancelPending();if(hook)UnhookWinEvent(hook);hook=nullptr;
            if(destroyHook)UnhookWinEvent(destroyHook);destroyHook=nullptr;return;
        }
        for(auto i=seen.begin();i!=seen.end();)if(!IsWindow(*i))i=seen.erase(i);else ++i;
        {std::lock_guard<std::mutex> lock(mutex);for(auto i=initialFilenames.begin();i!=initialFilenames.end();)if(!IsWindow(i->first))i=initialFilenames.erase(i);else ++i;}
        if(!config.automatic)CancelPending();
        if(!hook)hook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,nullptr,[](HWINEVENTHOOK,DWORD,HWND window,LONG,LONG,DWORD,DWORD){if(current)current->Foreground(window);},0,0,WINEVENT_OUTOFCONTEXT);
        if(!destroyHook)destroyHook=SetWinEventHook(EVENT_OBJECT_DESTROY,EVENT_OBJECT_DESTROY,nullptr,[](HWINEVENTHOOK,DWORD,HWND window,LONG object,LONG child,DWORD,DWORD){if(current&&object==OBJID_WINDOW&&child==CHILDID_SELF)current->Destroyed(window);},0,0,WINEVENT_OUTOFCONTEXT);
        Foreground(foreground());
    }
    void Run(){
        current=this;thread=GetCurrentThreadId();MSG msg{};PeekMessageW(&msg,nullptr,WM_USER,WM_USER,PM_NOREMOVE);
        std::vector<HWND> existing;
        EnumWindows([](HWND window,LPARAM data)->BOOL{auto type=Class(window);if(type==L"CabinetWClass"||type==L"ExploreWClass")reinterpret_cast<std::vector<HWND>*>(data)->push_back(window);return TRUE;},reinterpret_cast<LPARAM>(&existing));
        for(auto i=existing.rbegin();i!=existing.rend();++i)activated[*i]=++sequence;
        Apply(config);SetEvent(ready);
        while(GetMessageW(&msg,nullptr,0,0)>0){
            if(msg.message==WM_APP+46){auto request=reinterpret_cast<ApplyRequest*>(msg.lParam);Apply(request->config);SetEvent(request->done);}
            else if(msg.message==WM_TIMER&&timer&&msg.wParam==timer)TryAutomatic();
            else if(msg.message==WM_HOTKEY&&config.enabled&&keys.dialog&&foreground()==keys.dialog){
                auto edit=Filename(keys.dialog);RECT r{};
                if(edit&&GetWindowRect(edit,&r))trigger(keys.dialog,static_cast<int>(msg.wParam),r.left,r.bottom);
            }else{TranslateMessage(&msg);DispatchMessageW(&msg);}
        }
        auto disabled=config;disabled.enabled=false;Apply(disabled);current=nullptr;
    }
    ~Service(){if(worker.joinable()){PostThreadMessageW(thread,WM_QUIT,0,0);worker.join();}CloseHandle(ready);}
};
std::wstring Display(const std::wstring& path){
    ComPtr<IShellItem> item;PWSTR text=nullptr;
    if(SUCCEEDED(SHCreateItemFromParsingName(path.c_str(),nullptr,IID_PPV_ARGS(&item)))&&SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY,&text))){std::wstring result(text);CoTaskMemFree(text);return result;}
    return PathFindFileNameW(path.c_str());
}
std::vector<Entry> Entries(Service* service,bool showRecent=true){
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
    if(showRecent&&SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Recent,KF_FLAG_DEFAULT,nullptr,&recent))){
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
    auto result=Select(std::move(entries),[](const std::wstring& path){return PathIsDirectoryW(path.c_str())!=FALSE;},showRecent);
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

API void PaneFolderJumpApply(void* value,int enabled,UINT jumpMods,UINT jumpKey,UINT menuMods,UINT menuKey,int automatic){
    auto service=static_cast<Service*>(value);if(!service)return;
    ApplyRequest request{{enabled!=0,automatic!=0,{jumpMods,jumpKey},{menuMods,menuKey}},CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    if(request.done&&PostThreadMessageW(service->thread,WM_APP+46,0,reinterpret_cast<LPARAM>(&request)))WaitForSingleObject(request.done,INFINITE);
    if(request.done)CloseHandle(request.done);
}
API void PaneFolderJumpEntriesFiltered(void* value,EntryCallback callback,int recent){try{for(const auto& e:Entries(static_cast<Service*>(value),recent!=0))callback(e.path.c_str(),e.name.c_str(),e.recent?1:0);}catch(...){}}
API int PaneFolderJumpAutoTo(void* value,HWND dialog,const wchar_t* path){try{return value&&static_cast<Service*>(value)->AutomaticTo(dialog,path)?1:0;}catch(...){return 0;}}
