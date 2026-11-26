// 复用 038 的隐藏桌面、真实系统对话框和失败计数。前台查询是隐藏桌面唯一的替身。
struct Notice107 { HWND window; RECT bounds; int flags; std::wstring type,appName; };
std::mutex noticeMutex;
std::vector<Notice107> notices;
std::atomic<HWND> requestedForeground=nullptr;
HWND WINAPI TestForeground107(){return requestedForeground.load();}
void __stdcall Changed107(HWND window,RECT bounds,int flags,const wchar_t* type,const wchar_t* appName){
    std::lock_guard<std::mutex> lock(noticeMutex);notices.push_back({window,bounds,flags,type,appName});
}
template<typename Predicate> bool Await107(Predicate matches){
    for(int n=0;n<60;n++){
        {std::lock_guard<std::mutex> lock(noticeMutex);if(!notices.empty()&&matches(notices.back()))return true;}
        Sleep(10);
    }
    return false;
}
RECT Bounds107(HWND window){
    RECT r{};if(FAILED(DwmGetWindowAttribute(window,DWMWA_EXTENDED_FRAME_BOUNDS,&r,sizeof(r))))GetWindowRect(window,&r);
    return r;
}
void SearchDialog107(bool legacy,bool save,int mode,const std::wstring& start,const std::wstring& target){
    logFile<<"legacy="<<legacy<<" save="<<save<<" mode="<<mode<<std::endl;
    auto service=std::make_unique<Service>([](HWND,int,int,int){});
    service->foreground=TestForeground107;
    service->worker=std::thread([&]{service->Run();});
    WaitForSingleObject(service->ready,INFINITE);
    auto apply=[&](bool enabled,bool filter){PaneFolderJumpApplySearch(service.get(),1,MOD_CONTROL,'G',MOD_CONTROL|MOD_SHIFT,'G',0,enabled?Changed107:nullptr,filter);};
    DWORD ui=GetCurrentThreadId();
    WNDCLASSW wc{};wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane107Search";
    wc.lpfnWndProc=[](HWND w,UINT msg,WPARAM wp,LPARAM lp)->LRESULT{
        if(msg==WM_APP+107){ShowWindow(w,SW_SHOWNOACTIVATE);SetActiveWindow(w);return 0;}
        return DefWindowProcW(w,msg,wp,lp);
    };
    RegisterClassW(&wc);
    auto search=CreateWindowExW(0,wc.lpszClassName,L"fixture",WS_OVERLAPPEDWINDOW,
        0,0,120,40,nullptr,nullptr,wc.hInstance,nullptr);
    {std::lock_guard<std::mutex> lock(noticeMutex);notices.clear();}
    auto selected=std::filesystem::path(target)/L"match.txt";
    if(!save&&mode==2){std::ofstream file(selected);file<<"fixture";}
    std::wstring result;
    std::thread driver([&]{
        HWND window=nullptr;
        for(int i=0;i<150&&!window;i++){
            EnumThreadWindows(ui,[](HWND w,LPARAM p)->BOOL{if(Filename(w)){*reinterpret_cast<HWND*>(p)=w;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&window));
            if(!window)Sleep(20);
        }
        Check(window!=nullptr,"recognized actual dialog");if(!window){PostThreadMessageW(ui,WM_QUIT,0,0);return;}
        ShowWindow(window,SW_SHOWNOACTIVATE);
        // 等待 Shell 完成初次布局，否则它自己的显示动作可能还原刚最小化的窗口。
        Sleep(400);
        requestedForeground=window;apply(true,true);
        Check(Await107([&](auto& n){return n.window==window&&(n.flags&1)&&bool(n.flags&4)==save;}),"appearance, foreground and save kind");
        if(mode>=3){
            bool picker=mode==4;
            Check(Await107([&](auto& n){return n.window==window&&bool(n.flags&8)==picker&&
                bool(n.flags&4)==save&&n.appName==L"FolderJumpTests";}),"125 folder picker, save flag and process name");
            PostMessageW(window,WM_CLOSE,0,0);return;
        }
        if(mode==0){
            auto checkRect=[&]{auto bounds=Bounds107(window);return Await107([&](auto& n){return n.window==window&&EqualRect(&n.bounds,&bounds);});};
            Check(checkRect(),"initial physical bounds");
            RECT r{};GetWindowRect(window,&r);
            SetWindowPos(window,nullptr,r.left+35,r.top+29,0,0,SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE);
            Check(checkRect(),"moved bounds");
            SetWindowPos(window,nullptr,0,0,r.right-r.left+60,r.bottom-r.top+40,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
            Check(checkRect(),"resized bounds");
            ShowWindow(window,SW_MINIMIZE);Check(Await107([](auto& n){return (n.flags&2)!=0;}),"minimized");
            ShowWindow(window,SW_RESTORE);Check(Await107([](auto& n){return (n.flags&2)==0;}),"restored");
            requestedForeground=nullptr;apply(true,true);
            Check(Await107([&](auto& n){return n.window==window&&!(n.flags&1);}),"lost foreground retains target");
            requestedForeground=window;apply(true,true);
            auto box=DialogTracker::TypeBox(window);
            Check(box&&Await107([](auto& n){return n.type.find(L"*.*")!=std::wstring::npos;}),"all files label");
            int index=0;
            for(auto pattern:{L"*.txt",L"*.png;*.jpg"}){
                DWORD_PTR ignored=0;
                Send(box,CB_SETCURSEL,++index,0,ignored);
                Send(GetParent(box),WM_COMMAND,MAKEWPARAM(GetDlgCtrlID(box),CBN_SELCHANGE),reinterpret_cast<LPARAM>(box),ignored);
                bool updated=Await107([&](auto& n){return n.type.find(pattern)!=std::wstring::npos;});
                std::wstring actual;ReadFilename(box,actual);Send(box,CB_GETCURSEL,0,0,ignored);
                char label[512]{};WideCharToMultiByte(CP_UTF8,0,actual.c_str(),-1,label,512,nullptr,nullptr);
                logFile<<"selected="<<ignored<<" text="<<label<<" event="<<updated<<std::endl;
                Check(updated,"type key changed snapshot");
            }
            apply(true,false);Check(Await107([](auto& n){return n.type.empty();}),"filter disabled");
            apply(false,false);Check(!service->tracker.location&&!service->tracker.timer&&!service->tracker.typeTimer,"search disabled releases scoped hooks");
            apply(true,true);
        }
        auto edit=Filename(window);
        WriteFilename(edit,L"keep.txt");
        // 由界面线程激活占位搜索框；驱动服务的线程保持独立的输入队列。
        DWORD_PTR ignored=0;Send(search,WM_APP+107,0,0,ignored);
        GUITHREADINFO before{sizeof(GUITHREADINFO)};GetGUIThreadInfo(ui,&before);
        Check(before.hwndActive==search,"fixture search window active before navigation");
        HANDLE cancel=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        Check(PaneDialogNavigate(window,target.c_str(),mode?L"match.txt":nullptr,mode==2,cancel)==1,"navigation completed");
        CloseHandle(cancel);
        if(mode!=2){
            std::wstring name;ReadFilename(edit,name);
            Check(name==(mode?L"match.txt":L"keep.txt"),"final filename");
            if(legacy){wchar_t path[32768]{};DWORD_PTR size=0;Send(window,CDM_GETFOLDERPATH,32768,reinterpret_cast<LPARAM>(path),size);Check(Key(path)==Key(target),"legacy final folder");}
            else{std::lock_guard<std::mutex> lock(folderMutex);Check(Key(observed)==Key(target),"modern final folder");}
            // 跨线程的激活由目标消息队列处理。
            DWORD_PTR acknowledged=0;Send(window,WM_NULL,0,0,acknowledged);
            GUITHREADINFO gui{sizeof(GUITHREADINFO)};
            for(int i=0;i<60;i++){GetGUIThreadInfo(ui,&gui);if(gui.hwndActive==window)break;Sleep(10);}
            logFile<<"actualForeground="<<(GetForegroundWindow()==window)<<" desktopActive="<<(gui.hwndActive==window)<<std::endl;
            Check(GetForegroundWindow()==window||gui.hwndActive==window,"dialog active on hidden desktop");
        }else for(int i=0;i<100&&IsWindow(window);i++)Sleep(10);
        if(IsWindow(window))PostMessageW(window,WM_CLOSE,0,0);
        Check(Await107([](auto& n){return !n.window&&n.flags==0;}),"close sends null");
        requestedForeground=nullptr;
    });
    if(legacy){
        wchar_t path[1024]{};OPENFILENAMEW ofn{};ofn.lStructSize=sizeof(ofn);ofn.lpstrFile=path;ofn.nMaxFile=1024;
        ofn.lpstrInitialDir=start.c_str();ofn.lpstrFilter=L"All (*.*)\0*.*\0Text (*.txt)\0*.txt\0Image (*.png;*.jpg)\0*.png;*.jpg\0\0";
        ofn.Flags=OFN_EXPLORER|OFN_NOCHANGEDIR|OFN_DONTADDTORECENT|OFN_ENABLEHOOK|OFN_ENABLESIZING;
        ofn.lpfnHook=[](HWND,UINT,WPARAM,LPARAM)->UINT_PTR{return 0;};
        if(save?GetSaveFileNameW(&ofn):GetOpenFileNameW(&ofn))result=path;
    }else{
        ComPtr<IFileDialog> dialog;
        if(SUCCEEDED(CoCreateInstance(save?CLSID_FileSaveDialog:CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog)))){
            ComPtr<IShellItem> folder;SHCreateItemFromParsingName(start.c_str(),nullptr,IID_PPV_ARGS(&folder));dialog->SetFolder(folder.Get());
            dialog->SetOptions(FOS_DONTADDTORECENT|FOS_NOCHANGEDIR|FOS_FORCEFILESYSTEM|(mode==4?FOS_PICKFOLDERS:0));
            COMDLG_FILTERSPEC filters[]={{L"All (*.*)",L"*.*"},{L"Text (*.txt)",L"*.txt"},{L"Image (*.png;*.jpg)",L"*.png;*.jpg"}};
            if(mode<3)dialog->SetFileTypes(3,filters);
            auto events=new Events;DWORD cookie=0;dialog->Advise(events,&cookie);events->Release();
            if(SUCCEEDED(dialog->Show(nullptr))){
                ComPtr<IShellItem> item;PWSTR path=nullptr;
                if(SUCCEEDED(dialog->GetResult(&item))&&SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&path))){result=path;CoTaskMemFree(path);}
            }
            dialog->Unadvise(cookie);
        }
    }
    driver.join();service.reset();DestroyWindow(search);
    Check(mode==2?Key(result)==Key(selected.wstring()):result.empty(),"dialog returned expected file only on confirmation");
    if(!save&&mode==2)std::filesystem::remove(selected);
}
int MetadataSuite(){
    auto root=std::filesystem::temp_directory_path();
    for(int kind=0;kind<3;++kind)SearchDialog107(false,kind==1,kind==2?4:3,root.wstring(),root.wstring());
    logFile<<"assertions="<<assertions<<" failures="<<failures<<std::endl;return failures?1:0;
}
int SearchSuite(){
    auto root=std::filesystem::temp_directory_path()/(L"Pane107-"+std::to_wstring(GetCurrentProcessId()));
    auto target=root/L"target";std::filesystem::create_directories(target);
    for(bool legacy:{false,true})for(bool save:{false,true})for(int mode=0;mode<3;mode++)SearchDialog107(legacy,save,mode,root.wstring(),target.wstring());
    std::filesystem::remove(target);std::filesystem::remove(root);
    logFile<<"assertions="<<assertions<<" failures="<<failures<<std::endl;return failures?1:0;
}
