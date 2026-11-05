#pragma once
#include <cstdint>
namespace pane {
// 时间使用键盘事件的 DWORD 毫秒计数，差值允许系统计数回绕。
struct DoubleTap {
 static constexpr uint32_t HoldMilliseconds=300, GapMilliseconds=400;
 unsigned key=0,phase=0; uint32_t downAt=0,upAt=0; uint64_t mouseAt=0;
 bool pressed[256]{};
 void cancel(){key=phase=0;}
 unsigned feed(unsigned vk,bool down,uint32_t time,bool own,uint64_t mouse,bool (*isPressed)(unsigned)=nullptr){
  // TODO(design): 自身输入或鼠标按下保守地中断整轮（包括两次点击的间隙），如需放宽再细化。
  if(own){cancel();return 0;}
  if(vk>=256){cancel();return 0;}
  bool repeat=pressed[vk];pressed[vk]=down;
  if(phase&&mouse!=mouseAt)cancel();
  if(down){
   if(phase&&vk!=key){cancel();return 0;}
   if(repeat)return 0;
   if(phase==2&&time-upAt<=GapMilliseconds){phase=3;downAt=time;return 0;}
   cancel();
   if(vk<0xA0||vk>0xA5)return 0;
   for(unsigned i=0;i<256;i++)if(i!=vk&&pressed[i]){
    // 安全桌面可能丢失松开事件；仅在残留标记阻碍新一轮时核对真实状态。
    if(isPressed&&!isPressed(i))pressed[i]=false;
    else return 0;
   }
   key=vk;phase=1;downAt=time;mouseAt=mouse;return 0;
  }
  if(vk!=key||!phase)return 0;
  if(time-downAt>HoldMilliseconds){cancel();return 0;}
  if(phase==1){phase=2;upAt=time;return 0;}
  if(phase==3){auto result=key;cancel();return result;}
  return 0;
 }
};
}
