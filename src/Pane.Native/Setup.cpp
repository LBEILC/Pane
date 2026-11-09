#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shldisp.h>
#include <exdisp.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

namespace {
    struct TextVariant : VARIANT {
        explicit TextVariant(const wchar_t* text) noexcept {
            VariantInit(this); vt = VT_BSTR; bstrVal = SysAllocString(text);
        }
        ~TextVariant() { VariantClear(this); }
        TextVariant(const TextVariant&) = delete;
        TextVariant& operator=(const TextVariant&) = delete;
    };
}

// 必须取得桌面进程里的对象；直接创建 Shell.Application 不能保证由 Explorer 启动。
static HRESULT LaunchFromDesktop(const wchar_t* executable, const wchar_t* arguments,
    const wchar_t* directory, BOOL foreground) {
    if (!GetShellWindow()) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    ComPtr<IShellWindows> windows;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
    if (FAILED(hr)) return hr;
    VARIANT location{}, empty{};
    location.vt = VT_I4; location.lVal = CSIDL_DESKTOP;
    long window = 0;
    ComPtr<IDispatch> desktop;
    hr = windows->FindWindowSW(&location, &empty, SWC_DESKTOP, &window, SWFO_NEEDDISPATCH, &desktop);
    if (FAILED(hr)) return hr;
    if (!desktop) return E_NOINTERFACE;
    ComPtr<IServiceProvider> provider;
    hr = desktop.As(&provider);
    if (FAILED(hr)) return hr;
    ComPtr<IShellBrowser> browser;
    hr = provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
    if (FAILED(hr)) return hr;
    ComPtr<IShellView> view;
    hr = browser->QueryActiveShellView(&view);
    if (FAILED(hr)) return hr;
    ComPtr<IDispatch> dispatch;
    hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&dispatch));
    if (FAILED(hr)) return hr;
    ComPtr<IShellFolderViewDual> folder;
    hr = dispatch.As(&folder);
    if (FAILED(hr)) return hr;
    ComPtr<IDispatch> application;
    hr = folder->get_Application(&application);
    if (FAILED(hr)) return hr;
    ComPtr<IShellDispatch2> shell;
    hr = application.As(&shell);
    if (FAILED(hr)) return hr;
    // 点击安装完成页时把前台资格交给外壳；后台更新不请求焦点。
    // 无前台资格不代表启动失败，不能因此重复启动。
    if (foreground) CoAllowSetForegroundWindow(shell.Get(), nullptr);
    TextVariant file(executable), parameters(arguments), workingDirectory(directory), operation(L"open");
    if (!file.bstrVal || !parameters.bstrVal || !workingDirectory.bstrVal || !operation.bstrVal) return E_OUTOFMEMORY;
    VARIANT show{}; show.vt = VT_I4; show.lVal = SW_SHOWNORMAL;
    return shell->ShellExecute(file.bstrVal, parameters, workingDirectory, operation, show);
}

extern "C" __declspec(dllexport) HRESULT __stdcall LaunchPaneFromDesktop(
    const wchar_t* executable, const wchar_t* arguments, const wchar_t* directory, BOOL foreground) noexcept {
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return init;
    HRESULT result;
    try { result = LaunchFromDesktop(executable, arguments, directory, foreground); }
    catch (...) { result = E_FAIL; }
    if (SUCCEEDED(init)) CoUninitialize();
    return result;
}

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
