#pragma once
#include <array>
#include <cstdint>
#include <list>
namespace pane {
// 数值对应设计契约 PanelKey；0 留给「没有通知」。不依赖 Windows，便于序列测试。
enum PanelAction { PanelNone, PanelUp, PanelDown, PanelEnter, PanelShiftEnter, PanelQuick, PanelPin, PanelSend, PanelEscape, PanelType, PanelLeft, PanelRight, PanelDigit, PanelCopy, PanelRetry, PanelWords };
// 数值对应设计契约 PanelKeyMode；0/1 兼容原来的 send 参数。
enum class PanelMode { Clipboard, ClipboardWithSend, Super };
struct PanelModifiers { bool ctrl=false,alt=false,win=false,shift=false; unsigned altSides=0; };
struct PanelEvent { unsigned vk=0,scan=0; bool down=false,extended=false,own=false; };
struct PanelDecision { bool swallow=false,mask=false; PanelAction action=PanelNone; unsigned number=0; };
struct PanelQueued { PanelEvent event; bool mask=false; };
struct PanelKeys {
 static constexpr unsigned ReplayTimeoutMs=500;
 bool active=false,typing=false,wordsEnabled=false;
 PanelMode mode=PanelMode::Clipboard;
 uint64_t typingAt=0;
 std::array<bool,256> swallowed{},maskRelease{};
 // 每个排队结点固定小尺寸，不扩容复制大缓冲；释放在消息循环重放后进行。
 std::list<PanelQueued> queued;
 size_t count=0;
 void enqueue(PanelQueued q){queued.push_back(q);count++;}
 void begin(bool send,const std::array<bool,256>& held) {
  begin(send?PanelMode::ClipboardWithSend:PanelMode::Clipboard,held);
 }
 void begin(PanelMode selected,const std::array<bool,256>& held) {
  active=true;mode=selected;
  for(unsigned k:{0xA4u,0xA5u,0x5Bu,0x5Cu})maskRelease[k]=maskRelease[k]||held[k];
 }
 void end(){active=false;}
 bool pending() const {
  if(count)return true;
  for(unsigned k=0;k<256;k++)if(swallowed[k]||maskRelease[k])return true;
  return false;
 }
 bool expired(uint64_t now) const {return typing&&now-typingAt>=ReplayTimeoutMs;}
 void markAlt(PanelModifiers m){if(m.altSides&1)maskRelease[0xA4]=true;if(m.altSides&2)maskRelease[0xA5]=true;}
 static bool character(unsigned k){
  return (k>=0x41&&k<=0x5A)||(k>=0x30&&k<=0x39)||k==0x20||
   (k>=0x60&&k<=0x6F)||(k>=0xBA&&k<=0xC0)||(k>=0xDB&&k<=0xDF)||k==0xE2;
 }
 PanelDecision feed(PanelEvent e,PanelModifiers m,uint64_t now) {
  PanelDecision r{};if(e.own||e.vk>=256)return r;
  if(!e.down&&maskRelease[e.vk]){r.mask=true;maskRelease[e.vk]=false;}
  // 导航键已经吞掉的按下不重放孤立的松开；重复仍交给规则处理。
  if(!e.down&&swallowed[e.vk]){swallowed[e.vk]=false;r.swallow=true;return r;}
  if(e.down&&swallowed[e.vk]&&(!active||typing)){r.swallow=true;return r;}
  if(typing){enqueue({e,r.mask});r.swallow=true;if(e.down&&m.alt)markAlt(m);return r;}
  if(!active||!e.down)return r;
  if(mode==PanelMode::Super){
   if(!m.ctrl&&!m.alt&&!m.win&&!m.shift){
    if(e.vk==0x25)r.action=PanelLeft;
    else if(e.vk==0x27)r.action=PanelRight;
    else if(e.vk>=0x31&&e.vk<=0x36){r.action=PanelDigit;r.number=e.vk-0x30;}
    else if(e.vk==0x0D)r.action=PanelEnter;
    else if(e.vk==9)r.action=PanelSend;
    else if(e.vk==0x1B)r.action=PanelEscape;
   }else if(m.ctrl&&!m.alt&&!m.win&&!m.shift){
    if(e.vk==0x43)r.action=PanelCopy;
    else if(e.vk==0x52)r.action=PanelRetry;
   }
  }else if(!m.ctrl&&!m.alt&&!m.win){
   if(!m.shift&&e.vk==0x26)r.action=PanelUp;
   else if(!m.shift&&e.vk==0x28)r.action=PanelDown;
   else if(e.vk==0x0D)r.action=m.shift?PanelShiftEnter:PanelEnter;
   else if(!m.shift&&e.vk==9&&mode==PanelMode::ClipboardWithSend)r.action=PanelSend;
   else if(!m.shift&&e.vk==0x1B)r.action=PanelEscape;
   else if(character(e.vk))r.action=PanelType;
  }else if(m.alt&&!m.ctrl&&!m.win&&e.vk>=0x31&&e.vk<=0x39){r.action=PanelQuick;r.number=e.vk-0x30;}
  else if(m.ctrl&&!m.alt&&!m.win&&!m.shift&&e.vk==0x50)r.action=PanelPin;
  else if(wordsEnabled&&m.ctrl&&!m.alt&&!m.win&&!m.shift&&e.vk==0x42)r.action=PanelWords;
  if(r.action!=PanelNone){
   r.swallow=true;
   if(r.action==PanelType){enqueue({e,false});typing=true;typingAt=now;}
   else swallowed[e.vk]=true;
  }
  if(swallowed[e.vk])r.swallow=true;
  if(r.swallow&&e.down&&m.alt)markAlt(m);
  return r;
 }
 void replayed(){queued.clear();count=0;typing=false;active=false;}
};
}
