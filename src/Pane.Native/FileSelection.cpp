#define NOMINMAX
#include <windows.h>
#include <exdisp.h>
#include <shlobj.h>
#include <shlguid.h>
#include <wrl/client.h>
#include <string>
#include <cstring>
using Microsoft::WRL::ComPtr;
namespace {
std::wstring WindowClass(HWND window){wchar_t text[128]{};GetClassNameW(window,text,128);return text;}
bool DesktopClass(const std::wstring& name){return name==L"Progman"||name==L"WorkerW";}
bool ExplorerClass(const std::wstring& name){return name==L"CabinetWClass"||name==L"ExploreWClass";}
ComPtr<IFolderView> FolderView(HWND window){
    auto cls=WindowClass(window);
    if(!DesktopClass(cls)&&!ExplorerClass(cls))return {};
    ComPtr<IShellWindows> windows;ComPtr<IDispatch> dispatch;
    if(FAILED(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&windows))))return {};
    if(DesktopClass(cls)){
        VARIANT location{},root{};location.vt=VT_I4;location.lVal=CSIDL_DESKTOP;long handle=0;
        if(FAILED(windows->FindWindowSW(&location,&root,SWC_DESKTOP,&handle,SWFO_NEEDDISPATCH,&dispatch)))return {};
    }else{
        long count=0;windows->get_Count(&count);
        for(long i=0;i<count;i++){
            VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> candidate;ComPtr<IWebBrowser2> browser;
            if(FAILED(windows->Item(index,&candidate))||!candidate||FAILED(candidate.As(&browser)))continue;
            SHANDLE_PTR handle=0;
            if(SUCCEEDED(browser->get_HWND(&handle))&&reinterpret_cast<HWND>(handle)==window){dispatch=candidate;break;}
        }
    }
    if(!dispatch)return {};
    ComPtr<IServiceProvider> provider;ComPtr<IShellBrowser> browser;ComPtr<IShellView> view;ComPtr<IFolderView> folder;
    if(FAILED(dispatch.As(&provider))||FAILED(provider->QueryService(SID_STopLevelBrowser,IID_PPV_ARGS(&browser)))
        ||FAILED(browser->QueryActiveShellView(&view))||FAILED(view.As(&folder)))return {};
    // ShellWindows 的桌面注册可能属于另一桌面；只接受调用者指定的宿主。
    if(DesktopClass(cls)){HWND viewWindow=nullptr;if(FAILED(view->GetWindow(&viewWindow))||GetAncestor(viewWindow,GA_ROOT)!=window)return {};}
    return folder;
}
bool EditingClass(const std::wstring& cls){
    return cls==L"Edit"||cls.find(L"RichEdit")!=std::wstring::npos||cls.find(L"RICHEDIT")!=std::wstring::npos
        ||cls==L"Microsoft.UI.Content.DesktopChildSiteBridge"||cls==L"Windows.UI.Composition.DesktopWindowContentBridge";
}
wchar_t* SelectedPaths(PCIDLIST_ABSOLUTE parent,IEnumIDList* selected){
    std::wstring output;HRESULT status=S_OK;
    // 批量取 PIDL，仅一次取父目录；本地转路径，避免每个项目往返 Explorer COM。
    while(status==S_OK){
        PITEMID_CHILD children[128]{};ULONG fetched=0;
        struct FreeChildren {PITEMID_CHILD* values;~FreeChildren(){for(int i=0;i<128;i++)CoTaskMemFree(values[i]);}} release{children};
        status=selected->Next(128,children,&fetched);bool valid=true;
        for(ULONG i=0;i<fetched;i++){
            auto absolute=ILCombine(parent,children[i]);wchar_t path[32768]{};
            bool file=absolute&&SHGetPathFromIDListEx(absolute,path,32768,GPFIDL_DEFAULT);CoTaskMemFree(absolute);
            if(!file)valid=false;
            else{output+=path;output.push_back(L'\0');}
        }
        if(FAILED(status)||!valid)return nullptr;
    }
    output.push_back(L'\0');auto bytes=(output.size()+1)*sizeof(wchar_t);
    auto result=static_cast<wchar_t*>(CoTaskMemAlloc(bytes));if(result)memcpy(result,output.c_str(),bytes);return result;
}
}
extern "C" __declspec(dllexport) int PaneFileSelectionEditing(HWND window){
    GUITHREADINFO info{};info.cbSize=sizeof(info);
    if(!GetGUIThreadInfo(GetWindowThreadProcessId(window,nullptr),&info))return 0;
    // 文件列表的焦点是 DirectUIHWND；原生编辑框、现代地址栏的 XAML 输入宿主
    // 和持有插入符的窗口优先走既有文字读取，不为文件选区启动 UIA。
    return EditingClass(WindowClass(info.hwndFocus))||(info.hwndCaret&&GetAncestor(info.hwndCaret,GA_ROOT)==window);
}
extern "C" __declspec(dllexport) wchar_t* PaneFileSelectionRead(HWND window){
    try{
        auto view=FolderView(window);if(!view)return nullptr;
        ComPtr<IPersistFolder2> folder;PIDLIST_ABSOLUTE parent=nullptr;
        if(FAILED(view->GetFolder(IID_PPV_ARGS(&folder)))||FAILED(folder->GetCurFolder(&parent)))return nullptr;
        struct FreeParent {PIDLIST_ABSOLUTE value;~FreeParent(){CoTaskMemFree(value);}} release{parent};
        ComPtr<IEnumIDList> selected;
        if(FAILED(view->Items(SVGIO_SELECTION|SVGIO_FLAG_VIEWORDER,IID_PPV_ARGS(&selected))))return nullptr;
        return SelectedPaths(parent,selected.Get());
    }catch(...){return nullptr;}
}
