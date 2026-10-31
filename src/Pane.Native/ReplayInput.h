#pragma once
#include "Capture.h"
// 纯构造边界，便于验证坐标、成对事件和自身标记；不调用输入 API。
inline bool replayInputs(INPUT (&inputs)[2],int button,int x,int y,unsigned dpi,POINT cursor,RECT desktop){
 if(button!=1&&button!=2&&button!=4&&button!=8)return false;
 double dx=double(cursor.x)-x,dy=double(cursor.y)-y;
 if(dx*dx+dy*dy>=64.0*dpi*dpi/(96.0*96.0))return false;
 int width=desktop.right-desktop.left,height=desktop.bottom-desktop.top;
 if(width<=0||height<=0)return false;
 for(auto& i:inputs){i={};i.type=INPUT_MOUSE;i.mi.dwExtraInfo=pane::InputTag;i.mi.mouseData=button==4?1:button==8?2:0;
  i.mi.dx=static_cast<LONG>((static_cast<int64_t>(x-desktop.left)*65536+32768)/width);
  i.mi.dy=static_cast<LONG>((static_cast<int64_t>(y-desktop.top)*65536+32768)/height);
  i.mi.dwFlags=MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK;
 }
 inputs[0].mi.dwFlags|=button==1?MOUSEEVENTF_RIGHTDOWN:button==2?MOUSEEVENTF_MIDDLEDOWN:MOUSEEVENTF_XDOWN;
 inputs[1].mi.dwFlags|=button==1?MOUSEEVENTF_RIGHTUP:button==2?MOUSEEVENTF_MIDDLEUP:MOUSEEVENTF_XUP;
 return true;
}
