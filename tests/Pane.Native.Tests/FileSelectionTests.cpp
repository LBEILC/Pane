#include "../../src/Pane.Native/FileSelection.cpp"
#include <fstream>
#include <shlwapi.h>
#include <filesystem>
#include <chrono>
#include <algorithm>
namespace fs=std::filesystem;
namespace {
int checks=0;std::ofstream logFile;
void Check(bool ok,const char* name){checks++;if(!ok){logFile<<"FAIL "<<name<<std::endl;throw std::runtime_error(name);}}
// 假 Shell 枚举边界：真实 PIDL、可控顺序、虚拟项和枚举失败，不碰用户选区。
struct FakeItems : IEnumIDList {
    std::vector<PIDLIST_RELATIVE> items;size_t at=0;bool fail=false;ULONG refs=1;
    ~FakeItems(){for(auto p:items)CoTaskMemFree(p);}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p) override{if(id==IID_IUnknown||id==IID_IEnumIDList){*p=this;AddRef();return S_OK;}*p=nullptr;return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{return --refs;}
    HRESULT STDMETHODCALLTYPE Next(ULONG count,PITEMID_CHILD* output,ULONG* fetched) override{
        if(fail){if(fetched)*fetched=0;return E_FAIL;}ULONG copied=0;
        while(copied<count&&at<items.size())output[copied++]=ILClone(items[at++]);
        if(fetched)*fetched=copied;return copied==count?S_OK:S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Reset() override{at=0;return S_OK;}
    HRESULT STDMETHODCALLTYPE Clone(IEnumIDList**) override{return E_NOTIMPL;}
};
void Boundary(const fs::path& root){
    Check(DesktopClass(L"Progman")&&DesktopClass(L"WorkerW")&&!DesktopClass(L"CabinetWClass"),"desktop routing");
    Check(ExplorerClass(L"CabinetWClass")&&ExplorerClass(L"ExploreWClass")&&!ExplorerClass(L"Other"),"explorer routing");
    for(const auto* name:{L"Edit",L"RichEditD2DPT",L"RICHEDIT50W",L"Microsoft.UI.Content.DesktopChildSiteBridge",L"Windows.UI.Composition.DesktopWindowContentBridge"})Check(EditingClass(name),"editing focus class");
    Check(!EditingClass(L"DirectUIHWND"),"file list does not use text path");
    ITEMIDLIST desktop{};FakeItems empty;auto result=SelectedPaths(&desktop,&empty);Check(result&&!*result,"empty shell list");CoTaskMemFree(result);
    FakeItems files;for(const auto* name:{L"file0001.txt",L"file0000.txt"}){PIDLIST_ABSOLUTE pidl=nullptr;Check(SUCCEEDED(SHParseDisplayName((root/name).c_str(),nullptr,&pidl,0,nullptr)),"fixture pidl");files.items.push_back(pidl);}
    result=SelectedPaths(&desktop,&files);Check(result&&std::wstring(result)==(root/L"file0001.txt").wstring(),"fake order retained");CoTaskMemFree(result);
    PIDLIST_ABSOLUTE computer=nullptr;SHGetKnownFolderIDList(FOLDERID_ComputerFolder,0,nullptr,&computer);files.items.push_back(computer);files.Reset();
    result=SelectedPaths(&desktop,&files);Check(result==nullptr,"one virtual item makes entire result empty");CoTaskMemFree(result);
    files.Reset();files.fail=true;result=SelectedPaths(&desktop,&files);Check(result==nullptr,"shell failure returns empty");CoTaskMemFree(result);
}
void Pump(int ms){auto end=GetTickCount64()+ms;do{MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}Sleep(5);}while(GetTickCount64()<end);}
std::vector<std::wstring> Read(HWND window){auto text=PaneFileSelectionRead(window);std::vector<std::wstring> rows;if(text){for(auto p=text;*p;p+=wcslen(p)+1)rows.emplace_back(p);CoTaskMemFree(text);}return rows;}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;logFile.open(fs::path(argv[1]));HRESULT apartment=OleInitialize(nullptr);int code=0;
    ComPtr<IWebBrowser2> browser;fs::path root=fs::path(argv[1]).parent_path()/(L"selection-fixture-"+std::to_wstring(GetCurrentProcessId()));
    try{
        fs::create_directories(root);for(int i=0;i<1000;i++){wchar_t name[32]{};swprintf_s(name,L"file%04d.txt",i);std::ofstream(root/name)<<"fixture";}
        Boundary(root);
        ComPtr<IShellWindows> windows;HRESULT hr=CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&windows));logFile<<"shellWindows hr="<<std::hex<<hr<<std::dec<<std::endl;Check(SUCCEEDED(hr),"real shell windows");
        // 仅打开测试目录；资源管理器进程继承测试的隐藏桌面，不调用用户已有窗口。
        wchar_t system[MAX_PATH]{};GetWindowsDirectoryW(system,MAX_PATH);std::wstring executable=std::wstring(system)+L"\\explorer.exe";
        std::wstring command=L"\""+executable+L"\" /separate,/n,\""+root.wstring()+L"\"";
        STARTUPINFOW startup{};startup.cb=sizeof(startup);startup.dwFlags=STARTF_USESHOWWINDOW;startup.wShowWindow=SW_HIDE;PROCESS_INFORMATION process{};
        Check(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&process)!=FALSE,"start own explorer");CloseHandle(process.hThread);CloseHandle(process.hProcess);

        for(int attempt=0;attempt<160&&!browser;attempt++){
            Pump(50);long total=0;windows->get_Count(&total);
            for(long i=0;i<total;i++){
                VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;ComPtr<IWebBrowser2> candidate;
                if(FAILED(windows->Item(index,&dispatch))||!dispatch||FAILED(dispatch.As(&candidate)))continue;
                BSTR url=nullptr;if(SUCCEEDED(candidate->get_LocationURL(&url))&&url){wchar_t path[32768]{};DWORD size=32768;
                    if(SUCCEEDED(PathCreateFromUrlW(url,path,&size,0))&&root.wstring()==path)browser=candidate;SysFreeString(url);
                }
            }
        }
        Check(browser!=nullptr,"find only own folder browser");browser->put_Visible(VARIANT_FALSE);
        SHANDLE_PTR value=0;browser->get_HWND(&value);HWND window=reinterpret_cast<HWND>(value);ComPtr<IFolderView> view;int count=0;
        for(int i=0;i<200;i++){Pump(25);view=FolderView(window);if(view&&SUCCEEDED(view->ItemCount(SVGIO_ALLVIEW,&count))&&count==1000)break;}
        logFile<<"view="<<(view?1:0)<<" items="<<count<<std::endl;Check(view&&count==1000,"own explorer contains fixture only");
        view->SelectItem(-1,SVSI_DESELECTOTHERS);Check(Read(window).empty(),"empty selection");
        ComPtr<IShellFolder> folder;view->GetFolder(IID_PPV_ARGS(&folder));
        for(int size:{100,1000}){
            view->SelectItem(-1,SVSI_DESELECTOTHERS);std::vector<std::wstring> expected;
            for(int i=0;i<size;i++){view->SelectItem(i,SVSI_SELECT);PITEMID_CHILD child=nullptr;view->Item(i,&child);ComPtr<IShellItem> item;SHCreateItemWithParent(nullptr,folder.Get(),child,IID_PPV_ARGS(&item));CoTaskMemFree(child);PWSTR path=nullptr;item->GetDisplayName(SIGDN_FILESYSPATH,&path);expected.emplace_back(path);CoTaskMemFree(path);}
            view->SelectItem(size-1,SVSI_SELECT|SVSI_FOCUSED);Pump(30);
            std::vector<double> times;for(int i=0;i<10;i++){auto start=std::chrono::steady_clock::now();auto rows=Read(window);times.push_back(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count());Check(rows==expected,"selection display order exact paths");}
            std::sort(times.begin(),times.end());logFile<<"selection count="<<size<<" samples=10 medianMs="<<times[5]<<" maxMs="<<times.back()<<std::endl;
        }
        HWND own=CreateWindowExW(0,L"STATIC",L"test",WS_OVERLAPPEDWINDOW,0,0,100,100,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        Check(Read(own).empty(),"non explorer empty");DestroyWindow(own);
        logFile<<"PASS shell assertions="<<checks<<" userExplorerSelectionReads=0 clipboard=0"<<std::endl;
    }catch(const std::exception& e){logFile<<"ERROR "<<e.what()<<std::endl;code=1;}
    if(browser){browser->Quit();browser.Reset();Pump(100);}std::error_code error;fs::remove_all(root,error);if(SUCCEEDED(apartment))OleUninitialize();return code;
}
