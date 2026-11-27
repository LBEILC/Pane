#include <windows.h>
#include <shobjidl.h>
#include <cwchar>
#include <string>

extern "C" HRESULT __stdcall PickPaneFile(HWND, const wchar_t*, const wchar_t*,
    const COMDLG_FILTERSPEC*, UINT, wchar_t**) noexcept;

extern "C" HRESULT __stdcall PickPaneFiles(HWND, const wchar_t*, const wchar_t*,
    const COMDLG_FILTERSPEC*, UINT, wchar_t***, DWORD*) noexcept;
extern "C" HRESULT __stdcall PickPaneSave(HWND, const wchar_t*, const wchar_t*, const wchar_t*,
    const wchar_t*, const COMDLG_FILTERSPEC*, UINT, wchar_t**) noexcept;

static const wchar_t* title = L"Pane file picker 146";
static int command = IDCANCEL;
static bool foundFilters;

static BOOL CALLBACK checkCombo(HWND child, LPARAM) {
    wchar_t cls[64]{};
    GetClassNameW(child, cls, 64);
    if (wcscmp(cls, L"ComboBox") || SendMessageW(child, CB_GETCOUNT, 0, 0) != 2) return TRUE;
    const wchar_t* names[] = { L"Programs", L"Images" };
    for (int i = 0; i < 2; ++i) {
        wchar_t value[256]{};
        auto length = SendMessageW(child, CB_GETLBTEXTLEN, i, 0);
        if (length < 0 || length >= 256) return TRUE;
        SendMessageW(child, CB_GETLBTEXT, i, reinterpret_cast<LPARAM>(value));
        if (!wcsstr(value, names[i])) return TRUE;
    }
    foundFilters = true;
    return FALSE;
}

static BOOL CALLBACK handleDialog(HWND window, LPARAM) {
    wchar_t name[64]{};
    GetWindowTextW(window, name, 64);
    if (!wcscmp(name, title)) {
        EnumChildWindows(window, checkCombo, 0);
        if (foundFilters) PostMessageW(window, WM_COMMAND, command, 0);
    }
    return TRUE;
}

static void CALLBACK tick(HWND, UINT, UINT_PTR, DWORD) {
    EnumThreadWindows(GetCurrentThreadId(), handleDialog, 0);
}

#include "../../src/Pane.Native/ProcessErrorMode.h"
int wmain() {
    pane::diagnostics::Initialize();

    wchar_t desktop[128]{};
    DWORD needed{};
    if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME,
        desktop, sizeof desktop, &needed) || wcsncmp(desktop, L"Pane027-", 8)) return 2;
    COMDLG_FILTERSPEC filters[] = { {L"Programs", L"*.exe;*.lnk"}, {L"Images", L"*.png"} };
    auto timer = SetTimer(nullptr, 0, 100, tick);
    if (!timer) return 3;
    wchar_t* path{};
    auto hr = PickPaneFile(nullptr, nullptr, title, filters, 2, &path);

    bool ok = hr == S_OK && !path && foundFilters;
    CoTaskMemFree(path);
    if (!ok) return 1;

    title = L"Pane multiple files 147";
    foundFilters = false;
    wchar_t** paths{};
    DWORD count = MAXDWORD;
    hr = PickPaneFiles(nullptr, nullptr, title, filters, 2, &paths, &count);
    if (hr != S_OK || paths || count || !foundFilters) return 4;

    title = L"Pane save 147";
    foundFilters = false;
    // 使用通配过滤，确保返回的扩展名来自 SetDefaultExtension。
    COMDLG_FILTERSPEC saveFilters[] = { {L"Programs", L"*.*"}, {L"Images", L"*.png"} };
    hr = PickPaneSave(nullptr, L"example", L"txt", nullptr, title, saveFilters, 2, &path);
    if (hr != S_OK || path || !foundFilters) return 5;

    wchar_t folder[MAX_PATH]{};
    DWORD size = GetCurrentDirectoryW(MAX_PATH, folder);
    if (!size || size >= MAX_PATH) return 6;
    std::wstring name = L"Pane147-" + std::to_wstring(GetCurrentProcessId());
    std::wstring expected = std::wstring(folder) + L"\\" + name + L".txt";
    if (GetFileAttributesW(expected.c_str()) != INVALID_FILE_ATTRIBUTES) return 7;
    foundFilters = false;
    command = IDOK;
    hr = PickPaneSave(nullptr, name.c_str(), L"txt", folder, title, saveFilters, 2, &path);
    KillTimer(nullptr, timer);
    ok = hr == S_OK && path && !_wcsicmp(path, expected.c_str()) && foundFilters;
    CoTaskMemFree(path);
    return ok ? 0 : 8;
}
