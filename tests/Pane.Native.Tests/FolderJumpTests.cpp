#include "../../src/Pane.Native/FolderJump.cpp"
#include <commdlg.h>
#include <fstream>
#include <iostream>
#include <atomic>
#include <filesystem>

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
    legacy.erase(legacy.begin()+1);Check(pane::folderjump::Filename(L"#32770",legacy)==H(6),"missing file types accepted");
    auto open=std::vector<Control>{{H(1),nullptr,L"SHELLDLL_DefView",1121},{H(2),nullptr,L"Button",IDOK},{H(3),nullptr,L"ComboBox",0x47c},{H(4),H(3),L"Edit",0x47c}};
    Check(pane::folderjump::Filename(L"#32770",open)==H(4),"open without filter");
    auto save=std::vector<Control>{{H(1),nullptr,L"SHELLDLL_DefView",1121},{H(2),nullptr,L"Button",IDOK},{H(3),nullptr,L"FloatNotifySink",0},{H(4),H(3),L"ComboBox",0},{H(5),H(4),L"Edit",1001},{H(6),nullptr,L"FloatNotifySink",0},{H(7),H(6),L"ComboBox",0}};
    Check(pane::folderjump::Filename(L"#32770",save)==H(5),"save chooses editable combo not file type");
    save.push_back({H(8),H(7),L"Edit",1001,false});
    Check(pane::folderjump::Filename(L"#32770",save)==H(5),"hidden duplicate ignored");
    save.back().visible=true;Check(!pane::folderjump::Filename(L"#32770",save),"ambiguous save rejected");save.pop_back();
    save[4].visible=false;Check(!pane::folderjump::Filename(L"#32770",save),"hidden filename rejected");save[4].visible=true;
    save[3].visible=false;Check(!pane::folderjump::Filename(L"#32770",save),"hidden combo rejected");save[3].visible=true;
    save[0].visible=false;Check(!pane::folderjump::Filename(L"#32770",save),"hidden shell rejected");save[0].visible=true;
    save[1].visible=false;Check(!pane::folderjump::Filename(L"#32770",save),"hidden OK rejected");save[1].visible=true;
    save[2].type=L"Other";Check(!pane::folderjump::Filename(L"#32770",save),"unrelated editable combo rejected");save[2].type=L"FloatNotifySink";
    save.push_back({H(9),nullptr,L"ComboBox",0x47c});save.push_back({H(10),H(9),L"Edit",0x47c});
    Check(pane::folderjump::Filename(L"#32770",save)==H(10),"open filename priority");
    save.push_back({H(11),nullptr,L"Edit",0x480});Check(pane::folderjump::Filename(L"#32770",save)==H(11),"legacy filename priority");
    auto picker=std::vector<Control>{{H(1),nullptr,L"SHELLDLL_DefView",1121},{H(2),nullptr,L"Button",IDOK},{H(3),nullptr,L"Edit",0x480}};
    Check(pane::folderjump::Filename(L"#32770",picker)==H(3),"modern folder picker");
    picker[0].type=L"SysTabControl32";Check(!pane::folderjump::Filename(L"#32770",picker),"property sheet rejected");
    auto browse=std::vector<Control>{{H(1),nullptr,L"SHBrowseForFolder ShellNameSpace Control",0},{H(2),H(1),L"SysTreeView32",100},{H(3),nullptr,L"Button",IDOK},{H(4),nullptr,L"ComboBox",13006},{H(5),H(4),L"Edit",1001}};
    Check(!pane::folderjump::Filename(L"#32770",browse),"legacy folder browser with edit rejected");browse.resize(3);
    Check(!pane::folderjump::Filename(L"#32770",browse),"legacy tree only browser rejected");
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
    auto explorers=Select(entries,[](const auto&){return true;},false);
    Check(explorers.size()==2&&std::none_of(explorers.begin(),explorers.end(),[](const Entry& e){return e.recent;}),"recent disabled returns only Explorer entries");
    Check(selected.size()==10,"recent capped at eight after deduplication");
    Check(selected[0].path==L"C:\\New"&&selected[1].path==L"C:\\Old","activation order");
    Check(selected[2].path==L"C:\\recent11"&&selected.back().path==L"C:\\recent4","recent time order");
    Check(std::count_if(selected.begin(),selected.end(),[](const Entry& e){return Key(e.path)==L"c:\\new";})==1,"case and trailing slash deduplication");
    Check(Select(std::vector<Entry>{{nullptr,L"relative",L"",true,0}},[](const auto&){return true;}).empty(),"relative path rejected");
    Check(Select(std::vector<Entry>{{nullptr,L"\\\\server\\share",L"",false,0}},[](const auto&){return true;}).size()==1,"UNC accepted");
}
std::mutex folderMutex;std::wstring observed;
std::atomic<int> fileOk=0,overwrite=0;
struct Events : IFileDialogEvents {
    ULONG refs=1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {if(iid==IID_IUnknown||iid==IID_IFileDialogEvents){*output=this;AddRef();return S_OK;}*output=nullptr;return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE OnFileOk(IFileDialog*) override{++fileOk;return S_OK;}
    HRESULT STDMETHODCALLTYPE OnFolderChanging(IFileDialog*,IShellItem*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnFolderChange(IFileDialog* dialog) override {
        ComPtr<IShellItem> folder;PWSTR path=nullptr;
        if(SUCCEEDED(dialog->GetFolder(&folder))&&SUCCEEDED(folder->GetDisplayName(SIGDN_FILESYSPATH,&path))){std::lock_guard<std::mutex> lock(folderMutex);observed=path;CoTaskMemFree(path);}return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSelectionChange(IFileDialog*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnShareViolation(IFileDialog*,IShellItem*,FDE_SHAREVIOLATION_RESPONSE*) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE OnTypeChange(IFileDialog*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnOverwrite(IFileDialog*,IShellItem*,FDE_OVERWRITE_RESPONSE* response) override{++overwrite;*response=FDEOR_REFUSE;return S_OK;}
};
void RealDialog(int kind,const std::wstring& start,const std::wstring& target){
    bool legacy=kind==1;
    logFile<<"DIALOG kind="<<kind<<std::endl;
    fileOk=0;overwrite=0;HRESULT shown=E_ABORT;
    auto sentinel=std::filesystem::path(target)/L"原文件名.txt";
    if(kind==3||kind==4){std::ofstream file(sentinel);file<<"unchanged fixture";}
    {std::lock_guard<std::mutex> lock(folderMutex);observed.clear();}
    DWORD mainThread=GetCurrentThreadId();bool found=false,jumped=false,restored=false,navigated=false,background=false,foreground=false;
    std::thread driver([&]{
        HWND dialog=nullptr;
        for(int i=0;i<100&&!dialog;i++){
            EnumThreadWindows(mainThread,[](HWND w,LPARAM p)->BOOL{if(Class(w)==L"#32770"){*reinterpret_cast<HWND*>(p)=w;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&dialog));
            if(!dialog)Sleep(50);
        }
        if(!dialog){PostThreadMessageW(mainThread,WM_QUIT,0,0);return;}
        ShowWindow(dialog,SW_SHOWNOACTIVATE);Sleep(500);
        EnumChildWindows(dialog,[](HWND w,LPARAM)->BOOL{
            int depth=0;for(auto p=GetParent(w);p&&Class(p)!=L"#32770";p=GetParent(p))depth++;
            char type[128]{};GetClassNameA(w,type,128);logFile<<std::string(depth*2,' ')<<type<<" id="<<GetDlgCtrlID(w)<<" visible="<<IsWindowVisible(w)<<std::endl;return TRUE;
        },0);
        auto edit=Filename(dialog);found=edit!=nullptr;DWORD_PTR result=0;
        if(kind>=6){jumped=PaneFolderJumpTo(dialog,target.c_str())==1;logFile<<"browse recognized="<<found<<" jumped="<<jumped<<std::endl;PostMessageW(dialog,WM_COMMAND,IDCANCEL,0);return;}
        if(!found){PostMessageW(dialog,WM_CLOSE,0,0);return;}
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
    if(kind>=6){
        BROWSEINFOW info{};info.ulFlags=BIF_RETURNONLYFSDIRS|(kind==6?BIF_NEWDIALOGSTYLE|BIF_EDITBOX:0);
        PIDLIST_ABSOLUTE root=nullptr;SHParseDisplayName(start.c_str(),nullptr,&root,0,nullptr);info.pidlRoot=root;
        info.lpfn=[](HWND window,UINT message,LPARAM,LPARAM)->int{
            if(message==BFFM_INITIALIZED){ShowWindow(window,SW_SHOWNOACTIVATE);SetTimer(window,44,2000,[](HWND w,UINT,UINT_PTR id,DWORD){KillTimer(w,id);EndDialog(w,IDCANCEL);});}
            return 0;
        };
        auto result=SHBrowseForFolderW(&info);CoTaskMemFree(result);CoTaskMemFree(root);
    }else if(legacy){
        wchar_t file[1024]{};OPENFILENAMEW ofn{};ofn.lStructSize=sizeof(ofn);ofn.lpstrFile=file;ofn.nMaxFile=1024;ofn.lpstrInitialDir=start.c_str();ofn.lpstrFilter=L"All\0*.*\0\0";ofn.Flags=OFN_EXPLORER|OFN_NOCHANGEDIR|OFN_DONTADDTORECENT|OFN_ENABLEHOOK;ofn.lpfnHook=[](HWND,UINT,WPARAM,LPARAM)->UINT_PTR{return 0;};GetOpenFileNameW(&ofn);
    }else{
        ComPtr<IFileDialog> dialog;Check(SUCCEEDED(CoCreateInstance(kind==3||kind==4?CLSID_FileSaveDialog:CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog))),"create common item dialog");
        if(dialog){ComPtr<IShellItem> folder;SHCreateItemFromParsingName(start.c_str(),nullptr,IID_PPV_ARGS(&folder));dialog->SetFolder(folder.Get());dialog->SetOptions(FOS_DONTADDTORECENT|FOS_NOCHANGEDIR|FOS_FORCEFILESYSTEM|(kind==5?FOS_PICKFOLDERS:0)|((kind==3||kind==4)?FOS_OVERWRITEPROMPT:0));COMDLG_FILTERSPEC filters[]={ {L"All",L"*.*"} };if(kind==0||kind==3)dialog->SetFileTypes(1,filters);
            auto events=new Events;DWORD cookie=0;dialog->Advise(events,&cookie);events->Release();shown=dialog->Show(nullptr);dialog->Unadvise(cookie);
            // 模拟调用方保存：一旦错误提交，留下新文件并令验收失败，不以回调拦截掩盖误保存。
            if(SUCCEEDED(shown)&&(kind==3||kind==4)){std::ofstream file(std::filesystem::path(target)/L"unexpected-save.txt");file<<"unexpected";}}
    }
    driver.join();
    if(kind>=6){Check(!found&&!jumped,"real legacy folder browser not taken over");return;}
    Check(found,"real filename recognized");Check(background,"dialog not foreground before jump");Check(jumped,"jump returns success");Check(navigated,"real folder navigated");Check(restored,"filename restored");
    if(!legacy){Check(fileOk==0,"navigation did not submit selection");Check(overwrite==0,"navigation did not request overwrite");Check(shown==HRESULT_FROM_WIN32(ERROR_CANCELLED),"dialog remained open until fixture cancelled");}
    if(kind==3||kind==4){
        std::ifstream file(sentinel);std::string contents;std::getline(file,contents);file.close();
        Check(contents=="unchanged fixture","existing filename not overwritten");
        Check(std::distance(std::filesystem::directory_iterator(target),std::filesystem::directory_iterator{})==1,"save navigation created no files");
        std::filesystem::remove(sentinel);
    }
    logFile<<"fileOk="<<fileOk<<" overwrite="<<overwrite<<std::endl;
    logFile<<(legacy?"legacy":"modern")<<" found="<<found<<" background="<<background<<" jumped="<<jumped<<" navigated="<<navigated<<" restored="<<restored<<" foreground="<<foreground<<std::endl;
}

HWND fixtureForeground=nullptr;
HWND WINAPI FixtureForeground(){return fixtureForeground;}
int autoRequests=0,autoJumps=0,menus=0;
std::vector<Entry> fixtureExplorers;
Service* automaticService=nullptr;
bool changeBeforeJump=false;
void __stdcall AutomaticTrigger(HWND window,int command,int,int){
    if(command==3){
        ++autoRequests;
        if(changeBeforeJump){DWORD_PTR result=0;Send(Filename(window),WM_SETTEXT,0,reinterpret_cast<LPARAM>(L"user-input.txt"),result);}
        auto entries=Select(fixtureExplorers,[](const auto& path){return PathIsDirectoryW(path.c_str())!=FALSE;});
        if(!entries.empty()&&automaticService->AutomaticTo(window,entries.front().path.c_str()))++autoJumps;
    }else if(command==2)++menus;
}
void Lifecycle(){
    Service service([](HWND,int,int,int){});service.foreground=FixtureForeground;
    int registered=0,unregistered=0;std::vector<KeyBinding> bindings;
    service.registerKey=[&](int,KeyBinding key){++registered;bindings.push_back(key);return true;};
    service.unregisterKey=[&](int){++unregistered;};
    service.Apply({});service.Register(H(10));
    Check(registered==2&&unregistered==0,"enabled registers two keys");
    auto config=service.config;config.enabled=false;service.Apply(config);
    Check(registered==2&&unregistered==2&&!service.hook&&!service.destroyHook&&!service.timer,"disabled removes keys hooks and timer");
    config.enabled=true;service.Apply(config);service.Register(H(10));
    Check(registered==4&&unregistered==2,"reenabled registers two keys");
    config.jump={MOD_ALT,'J'};service.Apply(config);service.Register(H(10));
    Check(registered==6&&unregistered==4&&bindings[4]==config.jump,"changed combination immediately replaces keys");
    config.menu=config.jump;service.Apply(config);service.Register(H(10));
    Check(registered==7&&unregistered==6&&service.keys.direct&&!service.keys.menu,"identical bindings registered once");
    service.Register(nullptr);service.registerKey=[](int,KeyBinding){return false;};service.Register(H(10));
    Check(service.problem==1,"failed registration publishes problem mask");
    config.jump={MOD_ALT,'K'};config.menu={MOD_ALT,'L'};service.Apply(config);
    Check(service.problem==0,"changing binding clears failure without dialog");
    service.registerKey=[](int,KeyBinding){return true;};service.Register(H(10));Check(service.problem==0,"success clears failure");
    config.enabled=false;service.Apply(config);
    bool custom=RegisterKey(46,{MOD_ALT,'J'});Check(custom,"real Alt J registration");if(custom)UnregisterHotKey(nullptr,46);
}
void AutomaticDialog(int mode,const std::wstring& start,const std::wstring& target){
    // 5/6 验证另存为预填与延迟出现；2/7 在捕获初始值后模拟用户修改。
    bool save=mode>=5;changeBeforeJump=mode==2||mode==7;
    fileOk=0;overwrite=0;HRESULT shown=E_ABORT;
    autoRequests=autoJumps=menus=0;fixtureExplorers.clear();
    if(mode!=1){fixtureExplorers.push_back({H(1),start,L"",false,1});fixtureExplorers.push_back({H(2),target,L"",false,2});}
    DWORD mainThread=GetCurrentThreadId();bool found=false,navigated=false,inputPreserved=false;int repeats=0;
    std::thread driver([&]{
        HWND window=nullptr;
        for(int i=0;i<100&&!window;i++){
            EnumThreadWindows(mainThread,[](HWND w,LPARAM p)->BOOL{if(Filename(w)){*reinterpret_cast<HWND*>(p)=w;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&window));
            if(!window)Sleep(40);
        }
        if(!window){PostThreadMessageW(mainThread,WM_QUIT,0,0);return;}
        // 等测试对话框完成 Show 初始化，再模拟首次前台；与手动跳转夹具一致。
        Sleep(500);found=true;auto edit=Filename(window);DWORD_PTR result=0;
        if(!save)Send(edit,WM_SETTEXT,0,reinterpret_cast<LPARAM>(L""),result);
        std::wstring initialText;Check(ReadFilename(edit,initialText),"fixture reads initial displayed filename");
        if(save)Check(!initialText.empty(),"save fixture starts with prefilled filename");
        Service service(AutomaticTrigger);service.foreground=FixtureForeground;fixtureForeground=window;
        automaticService=&service;
        Configuration config;config.automatic=true;
        if(mode==3||mode==4||mode==6){
            ShowWindow(edit,SW_HIDE);service.Apply(config);
            Check(service.pending==window&&service.timer!=0,"unready dialog has bounded retry");
            if(mode==4){Sleep(2050);service.TryPending();Check(!service.pending&&!service.timer,"readiness deadline stops timer");}
            ShowWindow(edit,SW_SHOW);service.TryPending();
        }else service.Apply(config);
        while(service.pending){Sleep(40);service.TryPending();}
        fixtureForeground=nullptr;service.Foreground(nullptr);fixtureForeground=window;service.Foreground(window);
        repeats=autoJumps;
        config.jump={MOD_ALT,'J'};service.Apply(config);
        Check(service.keys.dialog==window&&service.keys.direct,"foreground dialog immediately uses custom binding");
        {std::lock_guard<std::mutex> lock(folderMutex);navigated=Key(observed)==Key(target);}
        wchar_t text[256]{};Send(edit,WM_GETTEXT,256,reinterpret_cast<LPARAM>(text),result);inputPreserved=std::wstring(text)==(changeBeforeJump?L"user-input.txt":initialText);
        Check(service.seen.count(window)==1&&!service.pending&&!service.timer,"attempt remembered and retry timer removed");
        service.Destroyed(window);Check(service.seen.count(window)==0,"destroy notification forgets HWND");
        Check(service.initialFilenames.count(window)==0,"destroy notification forgets initial filename");
        config.enabled=false;service.Apply(config);fixtureForeground=nullptr;PostMessageW(window,WM_CLOSE,0,0);
    });
    ComPtr<IFileDialog> dialog;Check(SUCCEEDED(CoCreateInstance(save?CLSID_FileSaveDialog:CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog))),"create auto fixture dialog");
    if(dialog){ComPtr<IShellItem> folder;SHCreateItemFromParsingName(start.c_str(),nullptr,IID_PPV_ARGS(&folder));dialog->SetFolder(folder.Get());dialog->SetOptions(FOS_DONTADDTORECENT|FOS_NOCHANGEDIR|FOS_FORCEFILESYSTEM);
        if(save)dialog->SetFileName(L"预填文件名.txt");
        auto events=new Events;DWORD cookie=0;dialog->Advise(events,&cookie);events->Release();shown=dialog->Show(nullptr);dialog->Unadvise(cookie);
        if(save&&SUCCEEDED(shown)){std::ofstream file(std::filesystem::path(target)/L"unexpected-auto-save.txt");file<<"unexpected";}}
    driver.join();Check(found,"automatic real dialog recognized");
    Check(autoRequests==(mode==4?0:1),"automatic request reaches Explorer substitute when initial text captured");
    Check(autoJumps==(mode==1||mode==2||mode==4||mode==7?0:1)&&repeats==autoJumps,"new dialog jumps once and never on return");
    Check(navigated==(mode==0||mode==3||mode==5||mode==6),"automatic selects most recently activated fixture folder");
    Check(menus==0,"automatic never falls back to menu");Check(inputPreserved,"automatic preserves user text");
    if(save){Check(fileOk==0&&overwrite==0,"automatic save navigation never submits or overwrites");Check(shown==HRESULT_FROM_WIN32(ERROR_CANCELLED),"automatic save stays open until cancelled");Check(std::filesystem::is_empty(target),"automatic save creates no files");}
    logFile<<"auto mode="<<mode<<" requests="<<autoRequests<<" jumps="<<autoJumps<<" navigated="<<navigated<<std::endl;
}
unsigned long long Cpu(){FILETIME creation{},exit{},kernel{},user{};GetProcessTimes(GetCurrentProcess(),&creation,&exit,&kernel,&user);return (static_cast<unsigned long long>(kernel.dwHighDateTime)<<32)+kernel.dwLowDateTime+(static_cast<unsigned long long>(user.dwHighDateTime)<<32)+user.dwLowDateTime;}
void DelayedRecognition(const std::wstring& root){
    WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"SHELLDLL_DefView";
    RegisterClassW(&wc);
    auto log=root+L"\\recognition.log";SetEnvironmentVariableW(L"PANE_FOLDER_JUMP_LOG",log.c_str());
    for(int mode=0;mode<6;mode++){
        HWND window=CreateWindowExW(0,L"#32770",L"fixture",WS_OVERLAPPEDWINDOW|WS_VISIBLE,0,0,200,160,nullptr,nullptr,wc.hInstance,nullptr);
        Check(window!=nullptr,"delayed fixture creates dialog before children");
        SetForegroundWindow(window);fixtureForeground=window;
        int requests=autoRequests;
        Service service(AutomaticTrigger);service.foreground=FixtureForeground;automaticService=&service;fixtureExplorers.clear();
        Configuration config;config.automatic=mode==1;service.Apply(config);
        Check(service.pending==window&&service.timer&&!service.keys.dialog,"unrecognized foreground waits regardless of auto setting");
        auto started=GetTickCount64();
        auto pump=[&](int ms){auto until=GetTickCount64()+ms;while(GetTickCount64()<until){MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){
            if(msg.message==WM_TIMER&&service.timer&&msg.wParam==service.timer)service.TryPending();
            else{TranslateMessage(&msg);DispatchMessageW(&msg);}
        }Sleep(5);}};
        pump(300);
        if(mode==2){pump(1800);Check(!service.pending&&!service.timer,"timeout removes retry timer");service.Foreground(window);Check(!service.timer,"duplicate foreground notification cannot restart timed out wait");}
        if(mode==3){fixtureForeground=nullptr;service.Foreground(nullptr);Check(!service.pending&&!service.timer,"foreground departure cancels immediately");}
        if(mode==4){DestroyWindow(window);service.Destroyed(window);Check(!service.pending&&!service.timer,"destroy cancels immediately");}
        if(mode==5){auto deadline=service.deadline;auto timer=service.timer;config.automatic=true;service.Apply(config);Check(service.pendingAutomatic&&service.deadline==deadline&&service.timer==timer,"enabling auto joins existing bounded wait");config.automatic=false;service.Apply(config);Check(service.pending==window&&service.timer,"turning auto off keeps recognition wait");}
        if(mode!=4){
            auto shell=CreateWindowExW(0,L"SHELLDLL_DefView",L"",WS_CHILD|WS_VISIBLE,0,0,50,50,window,nullptr,wc.hInstance,nullptr);
            auto accept=CreateWindowExW(0,L"Button",L"",WS_CHILD|WS_VISIBLE,0,60,40,20,window,reinterpret_cast<HMENU>(IDOK),wc.hInstance,nullptr);
            auto edit=CreateWindowExW(0,L"Edit",L"",WS_CHILD|WS_VISIBLE,0,90,100,20,window,reinterpret_cast<HMENU>(0x480),wc.hInstance,nullptr);
            Check(shell&&accept&&edit,"fixture creates delayed child controls");
            pump(150);
            bool recognized=mode==0||mode==1||mode==5;
            Check((service.keys.dialog==window)==recognized,"only active bounded wait recognizes delayed controls");
            if(recognized)Check(service.keys.direct&&service.keys.menu,"delayed recognition really registers both hotkeys");
            Check(autoRequests-requests==(mode==1?1:0),"automatic request only when enabled");
            logFile<<"delayed mode="<<mode<<" elapsedMs="<<GetTickCount64()-started<<std::endl;
        }
        Check(!service.pending&&!service.timer,"settled recognition leaves no timer");
        config.enabled=false;service.Apply(config);if(IsWindow(window))DestroyWindow(window);fixtureForeground=nullptr;
    }
    SetEnvironmentVariableW(L"PANE_FOLDER_JUMP_LOG",nullptr);
    std::ifstream file{std::filesystem::path(log)};std::string content((std::istreambuf_iterator<char>(file)),{});file.close();
    Check(content.find("delayed recognition succeeded elapsedMs=")!=std::string::npos,"delayed diagnostic includes elapsed milliseconds");DeleteFileW(log.c_str());
}
void Conflict(const std::wstring& root){
    auto path=root+L"\\hotkeys.log";SetEnvironmentVariableW(L"PANE_FOLDER_JUMP_LOG",path.c_str());
    bool blocked=RegisterHotKey(nullptr,99,MOD_CONTROL|MOD_NOREPEAT,'G')!=FALSE;Check(blocked,"fixture reserves Ctrl+G on hidden desktop");
    bool registered=true;
    if(blocked){std::thread contender([&]{registered=RegisterKey(1);if(registered)UnregisterHotKey(nullptr,1);});contender.join();UnregisterHotKey(nullptr,99);}
    Check(!registered,"real conflicting hotkey not registered");
    std::ifstream file{std::filesystem::path(path)};std::string line;std::getline(file,line);file.close();
    Check(line.find("RegisterHotKey failed key=Ctrl+G error=1409")!=std::string::npos,"conflict diagnostic contains key and Windows error");
    logFile<<"hotkey diagnostic: "<<line<<std::endl;
    SetEnvironmentVariableW(L"PANE_FOLDER_JUMP_LOG",nullptr);DeleteFileW(path.c_str());
    bool customBlocked=RegisterHotKey(nullptr,99,MOD_ALT|MOD_NOREPEAT,'J')!=FALSE;
    Check(customBlocked,"fixture reserves custom Alt J");
    if(customBlocked){
        std::thread contender([]{
            Service service([](HWND,int,int,int){});service.foreground=FixtureForeground;
            service.config.jump={MOD_ALT,'J'};service.config.menu={MOD_ALT,'K'};service.Register(H(10));
            Check(service.problem==1&&!service.keys.direct&&service.keys.menu,"real custom conflict sets service problem");
            auto config=service.config;config.jump={MOD_ALT,'L'};service.Apply(config);
            Check(service.problem==0,"changing real conflicting key clears problem");
            service.Register(H(10));Check(service.keys.direct&&service.keys.menu&&service.problem==0,"replacement custom keys register successfully");
            config.enabled=false;service.Apply(config);
        });contender.join();UnregisterHotKey(nullptr,99);
    }
}
void Idle(){
    auto start=Cpu();Sleep(5000);auto baseline=Cpu()-start;
    auto service=PaneFolderJumpStart([](HWND,int,int,int){});Check(service!=nullptr,"foreground service starts");
    Sleep(200);start=Cpu();Sleep(5000);auto enabled=Cpu()-start;
    PaneFolderJumpApply(service,0,MOD_CONTROL,'G',MOD_CONTROL|MOD_SHIFT,'G',0);
    auto state=static_cast<Service*>(service);Check(!state->hook&&!state->destroyHook&&!state->timer&&!state->keys.direct&&!state->keys.menu,"disabled native resources all released");
    start=Cpu();Sleep(5000);auto disabled=Cpu()-start;
    Check(disabled<=baseline+156250,"disabled idle CPU within one accounting tick");
    logFile<<"disabledCpuMs="<<disabled/10000.0<<std::endl;
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
    for(int kind=0;kind<8;kind++)RealDialog(kind,root,target);
    Check(!PaneFolderJumpTo(nullptr,target.c_str()),"closed dialog ignored");
    Lifecycle();for(int mode=0;mode<8;mode++)AutomaticDialog(mode,root,target);DelayedRecognition(root);Conflict(root);Idle();RemoveDirectoryW(target.c_str());RemoveDirectoryW(root.c_str());
    logFile<<"assertions="<<assertions<<" failures="<<failures<<std::endl;return failures?1:0;
}
