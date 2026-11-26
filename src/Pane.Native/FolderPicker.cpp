#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <string_view>
#include <limits>
using Microsoft::WRL::ComPtr;

static HRESULT configure(IFileDialog* dialog, const wchar_t* initial, const wchar_t* title,
    const COMDLG_FILTERSPEC* filters, UINT count, DWORD flags) noexcept {
    if (count && !filters) return E_INVALIDARG;
    for (UINT i = 0; i < count; ++i) {
        if (!filters[i].pszName || !filters[i].pszSpec) return E_INVALIDARG;
        // 明确接受快捷方式的调用方需要其路径，以保留参数和工作目录。
        std::wstring_view patterns(filters[i].pszSpec);
        while (!patterns.empty()) {
            auto end = patterns.find(L';');
            auto pattern = patterns.substr(0, end);
            if (pattern.size() == 5 && _wcsnicmp(pattern.data(), L"*.lnk", 5) == 0)
                flags |= FOS_NODEREFERENCELINKS;
            if (end == patterns.npos) break;
            patterns.remove_prefix(end + 1);
        }
    }
    FILEOPENDIALOGOPTIONS options{};
    HRESULT hr = dialog->GetOptions(&options);
    if (SUCCEEDED(hr)) hr = dialog->SetOptions(options | flags | FOS_FORCEFILESYSTEM);
    if (SUCCEEDED(hr) && count) hr = dialog->SetFileTypes(count, filters);
    if (SUCCEEDED(hr) && initial && *initial) {
        ComPtr<IShellItem> folder;
        hr = SHCreateItemFromParsingName(initial, nullptr, IID_PPV_ARGS(&folder));
        if (SUCCEEDED(hr)) hr = dialog->SetFolder(folder.Get());
    }
    if (SUCCEEDED(hr) && title && *title) hr = dialog->SetTitle(title);
    return hr;
}

template<class Dialog, class Action>
static HRESULT withDialog(REFCLSID clsid, Action action) noexcept {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) return hr;
    {
        ComPtr<Dialog> dialog;
        hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog));
        if (SUCCEEDED(hr)) hr = action(dialog.Get());
    }
    CoUninitialize();
    return hr;
}

static HRESULT getPath(IFileDialog* dialog, wchar_t** path) noexcept {
    ComPtr<IShellItem> selected;
    HRESULT hr = dialog->GetResult(&selected);
    return FAILED(hr) ? hr : selected->GetDisplayName(SIGDN_FILESYSPATH, path);
}

static HRESULT pick(HWND owner, const wchar_t* initial, const wchar_t* title,
    const COMDLG_FILTERSPEC* filters, UINT count, bool folders, wchar_t** path) noexcept {
    if (!path) return E_POINTER;
    *path = nullptr;
    return withDialog<IFileOpenDialog>(CLSID_FileOpenDialog, [&](IFileOpenDialog* dialog) {
        HRESULT hr = configure(dialog, initial, title, filters, count, folders ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST);
        if (FAILED(hr)) return hr;
        hr = dialog->Show(owner);
        if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED)) return folders ? S_FALSE : S_OK;
        return FAILED(hr) ? hr : getPath(dialog, path);
    });
}

extern "C" __declspec(dllexport) HRESULT __stdcall PickPaneFolder(
    HWND owner, const wchar_t* initial, const wchar_t* title, wchar_t** path) noexcept {
    return pick(owner, initial, title, nullptr, 0, true, path);
}

extern "C" __declspec(dllexport) HRESULT __stdcall PickPaneFile(HWND owner,
    const wchar_t* initial, const wchar_t* title, const COMDLG_FILTERSPEC* filters,
    UINT count, wchar_t** path) noexcept {
    return pick(owner, initial, title, filters, count, false, path);
}

static HRESULT getPaths(IFileOpenDialog* dialog, wchar_t*** paths, DWORD* length) noexcept {
    ComPtr<IShellItemArray> selected;
    HRESULT hr = dialog->GetResults(&selected);
    DWORD size{};
    if (SUCCEEDED(hr)) hr = selected->GetCount(&size);
    if (FAILED(hr) || !size) return hr;
    if (size > (std::numeric_limits<size_t>::max)() / sizeof(wchar_t*)) return E_OUTOFMEMORY;
    auto values = static_cast<wchar_t**>(CoTaskMemAlloc(size * sizeof(wchar_t*)));
    if (!values) return E_OUTOFMEMORY;
    for (DWORD i = 0; i < size; ++i) {
        ComPtr<IShellItem> item;
        hr = selected->GetItemAt(i, &item);
        if (SUCCEEDED(hr)) hr = item->GetDisplayName(SIGDN_FILESYSPATH, &values[i]);
        if (FAILED(hr)) {
            for (DWORD j = 0; j < i; ++j) CoTaskMemFree(values[j]);
            CoTaskMemFree(values);
            return hr;
        }
    }
    // 成功时将数组及每条路径的所有权一起移交给调用方。
    *paths = values;
    *length = size;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall PickPaneFiles(HWND owner,
    const wchar_t* initial, const wchar_t* title, const COMDLG_FILTERSPEC* filters,
    UINT count, wchar_t*** paths, DWORD* length) noexcept {
    if (!paths || !length) return E_POINTER;
    *paths = nullptr;
    *length = 0;
    return withDialog<IFileOpenDialog>(CLSID_FileOpenDialog, [&](IFileOpenDialog* dialog) {
        HRESULT hr = configure(dialog, initial, title, filters, count, FOS_FILEMUSTEXIST | FOS_ALLOWMULTISELECT);
        if (FAILED(hr)) return hr;
        hr = dialog->Show(owner);
        if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED)) return S_OK;
        return FAILED(hr) ? hr : getPaths(dialog, paths, length);
    });
}

extern "C" __declspec(dllexport) HRESULT __stdcall PickPaneSave(HWND owner,
    const wchar_t* suggestedName, const wchar_t* defaultExtension, const wchar_t* initial,
    const wchar_t* title, const COMDLG_FILTERSPEC* filters, UINT count, wchar_t** path) noexcept {
    if (!path) return E_POINTER;
    *path = nullptr;
    return withDialog<IFileSaveDialog>(CLSID_FileSaveDialog, [&](IFileSaveDialog* dialog) {
        HRESULT hr = configure(dialog, initial, title, filters, count, FOS_OVERWRITEPROMPT);
        if (SUCCEEDED(hr)) hr = dialog->SetDefaultExtension(defaultExtension);
        if (SUCCEEDED(hr)) hr = dialog->SetFileName(suggestedName);
        if (FAILED(hr)) return hr;
        hr = dialog->Show(owner);
        if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED)) return S_OK;
        return FAILED(hr) ? hr : getPath(dialog, path);
    });
}
