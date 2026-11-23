// 与 FolderJump 的前台识别和消息泵共用线程。位置、最小化、类型通知仅挂到目标线程。
#pragma once
struct DialogTracker {
    HWND dialog=nullptr,combo=nullptr;
    HWINEVENTHOOK location=nullptr,minimize=nullptr,state=nullptr;
    UINT_PTR timer=0,typeTimer=0;
    DialogCallback callback=nullptr;
    bool filter=false,readType=false,save=false;
    RECT lastRect{};
    int lastFlags=-1;
    std::wstring type,lastType;
    inline static thread_local DialogTracker* active=nullptr;

    static HWND TypeBox(HWND window){
        HWND box=GetDlgItem(window,0x470);
        if(Class(box)==L"ComboBox")return box;
        EnumChildWindows(window,[](HWND child,LPARAM data)->BOOL{
            if(Class(child)==L"ComboBox"&&IsWindowVisible(child)&&Class(GetParent(child))==L"FloatNotifySink"
                &&(GetWindowLongPtrW(child,GWL_STYLE)&3)==CBS_DROPDOWNLIST){
                *reinterpret_cast<HWND*>(data)=child;return FALSE;
            }
            return TRUE;
        },reinterpret_cast<LPARAM>(&box));
        return box;
    }
    static bool IsSave(HWND window){
        if(Class(GetParent(GetParent(Filename(window))))==L"FloatNotifySink")return true;
        // 旧式打开和保存共用控件树，用系统自己的本地化「保存」按钮资源。
        wchar_t label[128]{},systemSave[128]{};
        DWORD_PTR length=0;
        Send(GetDlgItem(window,IDOK),WM_GETTEXT,128,reinterpret_cast<LPARAM>(label),length);
        auto module=LoadLibraryExW(L"comdlg32.dll",nullptr,LOAD_LIBRARY_AS_DATAFILE|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(module){LoadStringW(module,369,systemSave,128);FreeLibrary(module);}
        return *systemSave&&CompareStringOrdinal(label,-1,systemSave,-1,TRUE)==CSTR_EQUAL;
    }
    static void CALLBACK Event(HWINEVENTHOOK hook,DWORD,HWND window,LONG object,LONG child,DWORD,DWORD){
        auto self=active;
        if(!self||!self->dialog)return;
        if(window!=self->dialog||(hook==self->location&&(object!=OBJID_WINDOW||child!=CHILDID_SELF)))return;
        if(!self->timer)self->timer=SetTimer(nullptr,0,16,nullptr);
    }
    HWINEVENTHOOK Hook(DWORD first,DWORD last){
        DWORD pid=0;auto thread=GetWindowThreadProcessId(dialog,&pid);
        return thread?SetWinEventHook(first,last,nullptr,Event,pid,thread,WINEVENT_OUTOFCONTEXT):nullptr;
    }
    static void Release(HWINEVENTHOOK& hook){if(hook)UnhookWinEvent(hook);hook=nullptr;}
    void StopTypeTimer(){if(typeTimer)KillTimer(nullptr,typeTimer);typeTimer=0;}
    void ReadType(){
        if(!IsWindow(combo))combo=TypeBox(dialog);
        type.clear();if(combo)ReadFilename(combo,type);
    }
    void CheckType(HWND foreground){
        ReadType();
        if(type!=lastType)Flush(foreground);
    }
    void Configure(const Configuration& config){
        callback=config.dialogChanged;
        if(filter!=config.filter){filter=config.filter;StopTypeTimer();type.clear();readType=filter;}
    }
    void Stop(bool closed){
        if(timer)KillTimer(nullptr,timer);timer=0;
        StopTypeTimer();Release(location);Release(minimize);Release(state);
        if(active==this)active=nullptr;
        auto old=dialog;dialog=combo=nullptr;lastFlags=-1;type.clear();lastType.clear();
        if(old&&callback)callback(nullptr,{},closed?0:-1,L"");
    }
    void Flush(HWND foreground){
        if(timer)KillTimer(nullptr,timer);timer=0;
        if(!dialog||!callback)return;
        RECT bounds{};
        if(FAILED(DwmGetWindowAttribute(dialog,DWMWA_EXTENDED_FRAME_BOUNDS,&bounds,sizeof(bounds)))&&!GetWindowRect(dialog,&bounds))return;
        int flags=(dialog==foreground?1:0)|(IsIconic(dialog)?2:0)|(save?4:0);
        // 标准组合框不保证发出选中值变化的 WinEvent。仅在目标前台、可见且启用筛选时复读类型。
        if(filter&&(flags&3)==1){
            if(!typeTimer){typeTimer=SetTimer(nullptr,0,250,nullptr);readType=true;}
        }else StopTypeTimer();
        if(readType){readType=false;ReadType();}
        if(flags==lastFlags&&EqualRect(&bounds,&lastRect)&&type==lastType)return;
        lastRect=bounds;lastFlags=flags;lastType=type;
        callback(dialog,bounds,flags,type.c_str());
    }
    void Track(const Configuration& config,HWND recognized,HWND foreground){
        if(!config.dialogChanged)return;
        if(recognized&&dialog!=recognized){
            // 直接从旧目标切到新目标；null 仅表示关闭。
            callback=nullptr;Stop(false);callback=config.dialogChanged;
            dialog=recognized;active=this;save=IsSave(dialog);
            location=Hook(EVENT_OBJECT_LOCATIONCHANGE,EVENT_OBJECT_LOCATIONCHANGE);
            minimize=Hook(EVENT_SYSTEM_MINIMIZESTART,EVENT_SYSTEM_MINIMIZEEND);
            state=Hook(EVENT_OBJECT_STATECHANGE,EVENT_OBJECT_STATECHANGE);
            readType=filter;
        }
        Flush(foreground);
    }
};
