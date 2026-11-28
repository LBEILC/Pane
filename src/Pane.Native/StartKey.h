#pragma once
#include <cstdint>
namespace pane {
// 单按 Win（2026-11-28）：Win 按下到松开之间没有别的键、没有鼠标按下、按下时 Ctrl / Alt / Shift 都没按着，松开那一下算一次。
// 左右 Win 混按、Win+E 这类组合键都不算；自动重复的按下不打断。
struct WinTap {
 unsigned held=0; bool clean=false; uint64_t mouseAt=0;
 static bool isWin(unsigned vk){return vk==0x5B||vk==0x5C;}
 bool feed(unsigned vk,bool down,bool own,uint64_t mouse,bool modifiers){
  if(own){if(held&&!isWin(vk))clean=false;return false;}
  if(isWin(vk)){
   if(down){
    if(!held){held=vk;clean=!modifiers;mouseAt=mouse;}
    else if(held!=vk)clean=false;
    return false;
   }
   bool tap=held==vk&&clean&&mouse==mouseAt;
   held=0;clean=false;
   return tap;
  }
  if(down&&held)clean=false;
  return false;
 }
};
}
