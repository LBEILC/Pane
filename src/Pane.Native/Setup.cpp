#include <windows.h>
#include <shobjidl.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

// 直接使用 Shell Link COM API；不启动脚本解释器，兼容 NativeAOT。
extern "C" __declspec(dllexport) HRESULT __stdcall CreatePaneShortcut(
    const wchar_t* path, const wchar_t* executable, const wchar_t* directory) noexcept {
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return init;
    HRESULT result;
    {
        ComPtr<IShellLinkW> link;
        result = CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link));
        if (SUCCEEDED(result)) result = link->SetPath(executable);
        if (SUCCEEDED(result)) result = link->SetWorkingDirectory(directory);
        if (SUCCEEDED(result)) result = link->SetIconLocation(executable, 0);
        ComPtr<IPersistFile> file;
        if (SUCCEEDED(result)) result = link.As(&file);
        if (SUCCEEDED(result)) result = file->Save(path, TRUE);
    }
    if (SUCCEEDED(init)) CoUninitialize();
    return result;
}
