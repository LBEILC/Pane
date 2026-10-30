#define NOMINMAX
#include <windows.h>
#include <richedit.h>
#include <thread>
#include <atomic>
#include <iostream>
extern "C" __declspec(dllimport) int PaneSelectionFixture(HWND,HWND,wchar_t*,int);
std::atomic<int> status=-1;HWND edit{};
LRESULT CALLBACK proc(HWND h,UINT m,WPARAM w,LPARAM l){if(m==WM_CLOSE||m==WM_KEYDOWN&&w==VK_ESCAPE){DestroyWindow(h);return 0;}if(m==WM_DESTROY){PostQuitMessage(0);return 0;}return DefWindowProcW(h,m,w,l);}
int main(){
 auto module=LoadLibraryW(L"Msftedit.dll");if(!module)return 1;
 WNDCLASSW wc{};wc.lpfnWndProc=proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"Pane012SelectionTest";RegisterClassW(&wc);
 HWND window=CreateWindowExW(WS_EX_TOOLWINDOW,wc.lpszClassName,L"Pane selection test - Esc exits",WS_OVERLAPPEDWINDOW,60,60,420,150,nullptr,nullptr,wc.hInstance,nullptr);
 edit=CreateWindowExW(0,MSFTEDIT_CLASS,L"",WS_CHILD|WS_VISIBLE|ES_MULTILINE,5,5,390,80,window,nullptr,wc.hInstance,nullptr);
 SetWindowTextW(edit,L"Pane selection fixture");SendMessageW(edit,EM_SETSEL,5,14);ShowWindow(window,SW_SHOWNOACTIVATE);
 std::thread reader([window]{Sleep(200);wchar_t text[1024]{};int result=PaneSelectionFixture(window,edit,text,1024);std::wcout<<L"UIA result="<<result<<L";text="<<text<<L"\n";status=result==0&&!wcscmp(text,L"selection")?0:1;PostMessageW(window,WM_CLOSE,0,0);});
 SetTimer(window,1,3000,nullptr);MSG msg{};while(GetMessageW(&msg,nullptr,0,0)>0){if(msg.message==WM_TIMER)PostMessageW(window,WM_CLOSE,0,0);TranslateMessage(&msg);DispatchMessageW(&msg);}
 reader.join();UnregisterClassW(wc.lpszClassName,wc.hInstance);FreeLibrary(module);std::cout<<"systemClipboard=0;input=0;windows=0\n";return status;
}
