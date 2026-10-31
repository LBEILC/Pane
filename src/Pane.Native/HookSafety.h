#pragma once
#include <cstdint>
// 钩子线程独占的滑动窗口；熔断只由下一次非注入按下解除。
struct HookSafety {
 bool tripped=false;
 uint64_t replays[20]{},repairs[3]{};unsigned replayCount=0,repairCount=0;
 bool allow(uint64_t now,bool repair){
  if(tripped)return false;
  auto values=repair?repairs:replays;auto& count=repair?repairCount:replayCount;
  unsigned kept=0;for(unsigned n=0;n<count;n++)if(now-values[n]<(repair?10000ULL:1000ULL))values[kept++]=values[n];
  count=kept;if(count==(repair?3U:20U)){tripped=true;return false;}
  values[count++]=now;return true;
 }
 bool physicalDown(){if(!tripped)return false;*this={};return true;}
};
