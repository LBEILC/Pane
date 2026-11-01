#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

// 成功返回 S_OK 和 CoTaskMemAlloc 路径（调用方释放）；取消返回 S_FALSE 和空指针。
// 其余失败保留 HRESULT，必须从 STA 线程调用。
extern "C" __declspec(dllexport) HRESULT __stdcall PickPaneFolder(
    HWND owner, const wchar_t* initial, const wchar_t* title, wchar_t** path) noexcept {
    if (!path) return E_POINTER;
    *path = nullptr;
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init)) return init;
    HRESULT result;
    {
        ComPtr<IFileOpenDialog> dialog;
        result = CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog));
        FILEOPENDIALOGOPTIONS options = {};
        if (SUCCEEDED(result)) result = dialog->GetOptions(&options);
        if (SUCCEEDED(result)) result = dialog->SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
        if (SUCCEEDED(result) && initial && *initial) {
            ComPtr<IShellItem> folder;
            result = SHCreateItemFromParsingName(initial, nullptr, IID_PPV_ARGS(&folder));
            if (SUCCEEDED(result)) result = dialog->SetFolder(folder.Get());
        }
        if (SUCCEEDED(result) && title && *title) result = dialog->SetTitle(title);
        if (SUCCEEDED(result)) result = dialog->Show(owner);
        if (SUCCEEDED(result)) {
            ComPtr<IShellItem> selected;
            result = dialog->GetResult(&selected);
            if (SUCCEEDED(result)) result = selected->GetDisplayName(SIGDN_FILESYSPATH, path);
        }
        if (result == HRESULT_FROM_WIN32(ERROR_CANCELLED)) result = S_FALSE;
    }
    CoUninitialize();
    return result;
}
