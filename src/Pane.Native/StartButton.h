#pragma once
#include <windows.h>
namespace pane {
// 任务栏开始按钮的位置（2026-11-28）：开着时后台线程用 UIA 每 3 秒找一次各任务栏里的 StartButton，钩子回调只读缓存。
// 只由键盘钩子宿主串行调用 StartButtonsWatch；StartButtonHit 在钩子线程调用，拿不到锁就当没点中。
void StartButtonsWatch(bool on);
bool StartButtonHit(POINT point);
}
