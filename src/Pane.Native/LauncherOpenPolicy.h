#pragma once
#include <windows.h>
#include <shellapi.h>

namespace pane {
enum class LauncherOpenAction { Open, Default, Stop };

constexpr const wchar_t* launcherOpenVerb(LauncherOpenAction action) {
 return action==LauncherOpenAction::Open?L"open":nullptr;
}

// 优先采用 GetLastError 的精确错误；兼容码仅在没有精确错误时兜底。
// 默认动作之后必定结束，Shell 位置保留原来的单次 open 路径。
constexpr LauncherOpenAction nextLauncherOpenAction(LauncherOpenAction current,bool succeeded,
 DWORD error,INT_PTR shellError,bool shellLocation) {
 if(current!=LauncherOpenAction::Open||succeeded||shellLocation)return LauncherOpenAction::Stop;
 if(error==ERROR_NO_ASSOCIATION||(error==ERROR_SUCCESS&&shellError==SE_ERR_NOASSOC))
  return LauncherOpenAction::Default;
 return LauncherOpenAction::Stop;
}
}
