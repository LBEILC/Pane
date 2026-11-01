#include "../../src/Pane.Native/FolderJump.cpp"
#include <commdlg.h>
#include <fstream>
#include <iostream>
#include <atomic>

namespace {
int assertions=0,failures=0;
std::ofstream logFile;
void Check(bool value,const char* name){assertions++;if(!value){failures++;logFile<<"FAIL "<<name<<std::endl;}}
HWND H(int value){return reinterpret_cast<HWND>(static_cast<INT_PTR>(value));}
void Policy(){
    std::vector<Control> modern={{H(1),nullptr,L"SHELLDLL_DefView",0},{H(2),nullptr,L"ComboBox",0x470},{H(3),nullptr,L"Button",IDOK},{H(4),nullptr,L"ComboBox",0x47c},{H(5),H(4),L"Edit",1001}};
    Check(pane::folderjump::Filename(L"#32770",modern)==H(5),"modern filename");
    Check(!pane::folderjump::Filename(L"Other",modern),"other top level rejected");
    auto legacy=modern;legacy.resize(3);legacy.push_back({H(6),nullptr,L"Edit",0x480});
    Check(pane::folderjump::Filename(L"#32770",legacy)==H(6),"legacy filename");
    Check(!pane::folderjump::Filename(L"#32770",{{H(1),nullptr,L"Button",IDOK},{H(2),nullptr,L"Static",0}}),"message box rejected");
    modern.erase(modern.begin());Check(!pane::folderjump::Filename(L"#32770",modern),"missing shell rejected");
    legacy.erase(legacy.begin()+1);Check(!pane::folderjump::Filename(L"#32770",legacy),"missing file types rejected");
    Hotkeys keys;int registrations=0,unregistrations=0;std::vector<int> freed;
    auto reg=[&](int id){registrations++;return id==1;};auto unreg=[&](int id){unregistrations++;freed.push_back(id);};
    keys.Change(nullptr,reg,unreg);Check(registrations==0,"ordinary foreground no registration");
    keys.Change(H(1),reg,unreg);Check(registrations==2&&keys.direct&&!keys.menu,"conflicting key abandoned");
    keys.Change(H(1),reg,unreg);Check(registrations==2,"same dialog not retried");
    keys.Change(nullptr,reg,unreg);Check(unregistrations==1&&freed[0]==1&&!keys.direct&&!keys.menu,"leave unregisters successful keys");
    auto all=[&](int){registrations++;return true;};keys.Change(H(1),all,unreg);keys.Change(H(2),all,unreg);
    Check(unregistrations==3&&keys.dialog==H(2),"dialog switch unregisters both");
    keys.Change(nullptr,all,unreg);Check(unregistrations==5,"shutdown unregisters both");
    keys.Change(nullptr,all,unreg);Check(unregistrations==5,"repeat stop safe");
    std::vector<Entry> entries={{H(1),L"C:\\Old",L"",false,1},{H(2),L"C:\\New",L"",false,9},{H(3),L"shell:MyComputerFolder",L"",false,20},{nullptr,L"c:\\NEW\\",L"",true,100},{nullptr,L"C:\\missing",L"",true,90},{nullptr,L"C:\\file.txt",L"",true,80}};
    for(int i=0;i<12;i++)entries.push_back({nullptr,L"C:\\recent"+std::to_wstring(i),L"",true,static_cast<unsigned long long>(i)});
    auto selected=Select(entries,[](const std::wstring& p){return p!=L"C:\\missing"&&p!=L"C:\\file.txt";});
    Check(selected.size()==10,"recent capped at eight after deduplication");
    Check(selected[0].path==L"C:\\New"&&selected[1].path==L"C:\\Old","activation order");
    Check(selected[2].path==L"C:\\recent11"&&selected.back().path==L"C:\\recent4","recent time order");
    Check(std::count_if(selected.begin(),selected.end(),[](const Entry& e){return Key(e.path)==L"c:\\new";})==1,"case and trailing slash deduplication");
    Check(Select(std::vector<Entry>{{nullptr,L"relative",L"",true,0}},[](const auto&){return true;}).empty(),"relative path rejected");
    Check(Select(std::vector<Entry>{{nullptr,L"\\\\server\\share",L"",false,0}},[](const auto&){return true;}).size()==1,"UNC accepted");
}
std::mutex folderMutex;std::wstring observed;
struct Events : IFileDialogEvents {
    ULONG refs=1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {if(iid==IID_IUnknown||iid==IID_IFileDialogEvents){*output=this;AddRef();return S_OK;}*output=nullptr;return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE OnFileOk(IFileDialog*) override{return S_FALSE;}
    HRESULT STDMETHODCALLTYPE OnFolderChanging(IFileDialog*,IShellItem*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnFolderChange(IFileDialog* dialog) override {
        ComPtr<IShellItem> folder;PWSTR path=nullptr;
        if(SUCCEEDED(dialog->GetFolder(&folder))&&SUCCEEDED(folder->GetDisplayName(SIGDN_FILESYSPATH,&path))){std::lock_guard<std::mutex> lock(folderMutex);observed=path;CoTaskMemFree(path);}return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSelectionChange(IFileDialog*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnShareViolation(IFileDialog*,IShellItem*,FDE_SHAREVIOLATION_RESPONSE*) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE OnTypeChange(IFileDialog*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnOverwrite(IFileDialog*,IShellItem*,FDE_OVERWRITE_RESPONSE*) override{return E_NOTIMPL;}
};
void RealDialog(bool legacy,const std::wstring& start,const std::wstring& target){
    {std::lock_guard<std::mutex> lock(folderMutex);observed.clear();}
    DWORD mainThread=GetCurrentThreadId();bool found=false,jumped=false,restored=false,navigated=false,background=false,foreground=false;
    std::thread driver([&]{
        HWND dialog=nullptr;
        for(int i=0;i<100&&!dialog;i++){
            EnumThreadWindows(mainThread,[](HWND w,LPARAM p)->BOOL{if(Class(w)==L"#32770"&&Filename(w)){*reinterpret_cast<HWND*>(p)=w;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&dialog));
            if(!dialog)Sleep(50);
        }
        if(!dialog){PostThreadMessageW(mainThread,WM_QUIT,0,0);return;}
        found=true;Sleep(200);auto edit=Filename(dialog);DWORD_PTR result=0;
        Send(edit,WM_SETTEXT,0,reinterpret_cast<LPARAM>(L"原文件名.txt"),result);
        // 模拟菜单已经拿走前台，仍然只在隐藏桌面创建和激活测试窗口。
        HWND other=CreateWindowExW(0,L"STATIC",L"fixture",WS_OVERLAPPEDWINDOW|WS_VISIBLE,0,0,100,100,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        SetForegroundWindow(other);background=GetForegroundWindow()!=dialog;
        jumped=PaneFolderJumpTo(dialog,target.c_str())==1;
        wchar_t text[256]{};Send(edit,WM_GETTEXT,256,reinterpret_cast<LPARAM>(text),result);restored=std::wstring(text)==L"原文件名.txt";
        foreground=GetForegroundWindow()==dialog;
        if(legacy){wchar_t path[32768]{};Send(dialog,CDM_GETFOLDERPATH,32768,reinterpret_cast<LPARAM>(path),result);navigated=Key(path)==Key(target);logFile<<"legacy path result="<<result<<" length="<<wcslen(path)<<" matchesStart="<<(Key(path)==Key(start))<<std::endl;}
        else{std::lock_guard<std::mutex> lock(folderMutex);navigated=Key(observed)==Key(target);}
        DestroyWindow(other);PostMessageW(dialog,WM_CLOSE,0,0);
    });
    if(legacy){
        wchar_t file[1024]{};OPENFILENAMEW ofn{};ofn.lStructSize=sizeof(ofn);ofn.lpstrFile=file;ofn.nMaxFile=1024;ofn.lpstrInitialDir=start.c_str();ofn.lpstrFilter=L"All\0*.*\0\0";ofn.Flags=OFN_EXPLORER|OFN_NOCHANGEDIR|OFN_DONTADDTORECENT|OFN_ENABLEHOOK;ofn.lpfnHook=[](HWND,UINT,WPARAM,LPARAM)->UINT_PTR{return 0;};GetOpenFileNameW(&ofn);
    }else{
        ComPtr<IFileOpenDialog> dialog;Check(SUCCEEDED(CoCreateInstance(CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog))),"create common item dialog");
        if(dialog){ComPtr<IShellItem> folder;SHCreateItemFromParsingName(start.c_str(),nullptr,IID_PPV_ARGS(&folder));dialog->SetFolder(folder.Get());dialog->SetOptions(FOS_DONTADDTORECENT|FOS_NOCHANGEDIR|FOS_FORCEFILESYSTEM);COMDLG_FILTERSPEC filters[]={ {L"All",L"*.*"} };dialog->SetFileTypes(1,filters);
            auto events=new Events;DWORD cookie=0;dialog->Advise(events,&cookie);events->Release();dialog->Show(nullptr);dialog->Unadvise(cookie);}
    }
    driver.join();Check(found,"real filename recognized");Check(background,"dialog not foreground before jump");Check(jumped,"jump returns success");Check(navigated,"real folder navigated");Check(restored,"filename restored");
    logFile<<(legacy?"legacy":"modern")<<" found="<<found<<" background="<<background<<" jumped="<<jumped<<" navigated="<<navigated<<" restored="<<restored<<" foreground="<<foreground<<std::endl;
}
unsigned long long Cpu(){FILETIME creation{},exit{},kernel{},user{};GetProcessTimes(GetCurrentProcess(),&creation,&exit,&kernel,&user);return (static_cast<unsigned long long>(kernel.dwHighDateTime)<<32)+kernel.dwLowDateTime+(static_cast<unsigned long long>(user.dwHighDateTime)<<32)+user.dwLowDateTime;}
void Idle(){
    auto start=Cpu();Sleep(5000);auto baseline=Cpu()-start;
    auto service=PaneFolderJumpStart([](HWND,int,int,int){});Check(service!=nullptr,"foreground service starts");
    Sleep(200);start=Cpu();Sleep(5000);auto enabled=Cpu()-start;
    PaneFolderJumpStop(service);
    logFile<<"idle baselineCpuMs="<<baseline/10000.0<<" enabledCpuMs="<<enabled/10000.0<<" wallMs=5000 each"<<std::endl;
    Check(enabled<=baseline+156250,"idle CPU within one scheduler accounting tick");
}
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 2;wchar_t desktop[256]{};DWORD needed=0;
    if(!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop,sizeof(desktop),&needed)||wcsncmp(desktop,L"Pane027-",8)!=0)return 2;
    logFile.open(argv[1]);Apartment apartment;Policy();
    wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);auto root=std::wstring(temp)+L"Pane038-"+std::to_wstring(GetCurrentProcessId());auto target=root+L"\\目标目录";
    CreateDirectoryW(root.c_str(),nullptr);CreateDirectoryW(target.c_str(),nullptr);
    RealDialog(false,root,target);RealDialog(true,root,target);
    Check(!PaneFolderJumpTo(nullptr,target.c_str()),"closed dialog ignored");
    Idle();RemoveDirectoryW(target.c_str());RemoveDirectoryW(root.c_str());
    logFile<<"assertions="<<assertions<<" failures="<<failures<<std::endl;return failures?1:0;
}
