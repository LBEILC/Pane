#pragma once
#include <windows.h>
#include <atomic>
#include <functional>
#include <memory>

// 最多四个未完成调用，每个占一个槽；超时不取消系统调用，也不创建排队线程。
class BoundedOpen {
    std::atomic<unsigned> inFlight{0};
    struct Request {
        BoundedOpen* owner;
        std::function<int()> action;
        HANDLE done=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        int result=0;
        HMODULE module=nullptr;
        ~Request(){if(done)CloseHandle(done);}
    };
    static DWORD WINAPI execute(void* parameter){
        HMODULE module=nullptr;
        {
            std::unique_ptr<std::shared_ptr<Request>> holder(static_cast<std::shared_ptr<Request>*>(parameter));
            auto request=*holder;
            module=request->module;
            try {request->result=request->action();} catch(...) {request->result=0;}
            // action 返回前完成 COM 清理；卡在清理里也受同一超时保护。
            request->action=nullptr;
            request->owner->inFlight.fetch_sub(1);
            SetEvent(request->done);
        }
        if(module)FreeLibraryAndExitThread(module,0);
        return 0;
    }
public:
    int run(std::function<int()> action,DWORD timeout=5000){
        const auto started=GetTickCount64();
        auto count=inFlight.load();
        do {
            if(count>=4)return 0;
        } while(!inFlight.compare_exchange_weak(count,count+1));
        try {
            auto request=std::make_shared<Request>();
            request->owner=this;request->action=std::move(action);
            if(!request->done){inFlight.fetch_sub(1);return 0;}
            auto parameter=std::make_unique<std::shared_ptr<Request>>(request);
            // 工作线程持有模块引用，避免调用者超时返回后 DLL 被卸载。
            if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&execute),&request->module)){inFlight.fetch_sub(1);return 0;}
            HANDLE thread=CreateThread(nullptr,0,execute,parameter.get(),0,nullptr);
            if(!thread){FreeLibrary(request->module);inFlight.fetch_sub(1);return 0;}
            parameter.release();CloseHandle(thread);
            auto elapsed=GetTickCount64()-started;
            DWORD remaining=elapsed>=timeout?0:timeout-static_cast<DWORD>(elapsed);
            return WaitForSingleObject(request->done,remaining)==WAIT_OBJECT_0?request->result:0;
        } catch(...) {inFlight.fetch_sub(1);return 0;}
    }
};
