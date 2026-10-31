#pragma once
#include <cstdint>
#include <cmath>
#include <atomic>

namespace pane {
// 发送标记限制为 32 位，兼容旧版完整值及其截断值。
constexpr uintptr_t InputTag = 0x45303130UL;
constexpr bool ownInput(uintptr_t tag) { return tag==InputTag || tag==0x50414E45303130ULL; }
// 与发送线程共享；计数支持并发发送，标记负责异步到达的事件。
inline std::atomic<unsigned> simulatingInput=0;
inline std::atomic<bool> inputBypass=false;
struct SimulationScope {SimulationScope(){simulatingInput++;}~SimulationScope(){simulatingInput--;}};
enum Kind { Move, Down, Up, Wheel, Tick };
enum Signal { None, Begin, Point, Modifier, End, Replay, Super, Cancel };
struct Input { Kind kind; int button=0,x=0,y=0,wheel=0; uint64_t time=0; uintptr_t tag=0; bool injected=false; };
struct Decision { bool swallow=false; Signal signal=None; int value=0; int repeats=1; };
struct Capture {
 int enabled=1,superButton=4,timeout=0; unsigned hold=400; double dpi=96;
 int active=0,blockedUps=0,sx=0,sy=0,wheelRemainder=0; uint64_t started=0,lastMove=0;
 bool moved=false,modified=false,cancelled=false,panel=false,gestureEnabled=false;
 Decision feed(Input i,bool blocked=false) {
  if(ownInput(i.tag))return {};
  if(i.kind==Up&&(blockedUps&i.button)){blockedUps&=~i.button;return {true,None,0};}
  if(!active){
   if(i.kind!=Down||blocked||(!(enabled&i.button)&&i.button!=superButton))return {};
   active=i.button;sx=i.x;sy=i.y;wheelRemainder=0;started=lastMove=i.time;moved=modified=cancelled=panel=false;gestureEnabled=(enabled&i.button)!=0;
   return {true,Begin,active};
  }
  if(i.kind==Up&&i.button==active){
   int button=active;active=0;
   if(cancelled||panel)return {true,End,-1};
   if(button==superButton&&!moved&&!modified&&i.time-started>=hold)return {true,Super,button};
   if(!moved&&!modified)return {true,Replay,button};
   return {true,End,0};
  }
  if(i.kind==Tick){
   if(!cancelled&&!panel&&active==superButton&&!moved&&!modified&&i.time-started>=hold){panel=true;return {false,Super,active};}
   if(!cancelled&&!panel&&timeout>0&&i.time-lastMove>=static_cast<unsigned>(timeout)){cancelled=true;return {false,Cancel,0};}
   return {};
  }
  if(i.kind==Move){
   lastMove=i.time;
   // 未开启侧键手势时沿用 009：短按补发，长按请求面板，移动不进入手势识别。
   if(!gestureEnabled)return {};
   double dx=double(i.x)-sx,dy=double(i.y)-sy;
   if(dx*dx+dy*dy>=64*dpi*dpi/(96*96))moved=true;
   if(cancelled||panel)return {};
   return {false,Point,0};
  }
  if(i.kind==Wheel||(i.kind==Down&&i.button!=active)){
   int repeats=1;
   if(i.kind==Wheel){
    if(!i.wheel)return {true,None,0};
    modified=true;
    if(cancelled||panel)return {true,None,0};
    if((wheelRemainder>0&&i.wheel<0)||(wheelRemainder<0&&i.wheel>0))wheelRemainder=0;
    // 原生鼠标增量为有符号 16 位；每 120 单位生成一格，余数留在本次手势。
    wheelRemainder+=i.wheel;
    repeats=std::abs(wheelRemainder)/120;
    wheelRemainder%=120;
    if(!repeats)return {true,None,0};
   }
   int mod=i.kind==Wheel?(i.wheel>0?1:2):i.button==16?8:i.button==2?4:i.button==4?32:i.button==8?64:0;
   if(!mod)return {};
   if(i.kind==Down)blockedUps|=i.button;
   modified=true;
   return {true,(cancelled||panel)?None:Modifier,mod,repeats};
  }
  return {};
 }
};
}
