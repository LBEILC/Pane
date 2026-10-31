#pragma once
#include <cstdint>
// 仅使用回调自身耗时；不注册 Raw Input，也不推断丢失的按下或抬起。
struct HookHealth {
 static bool slow(uint64_t ticks,int64_t frequency){return frequency>0&&ticks>static_cast<uint64_t>(frequency);}
};
