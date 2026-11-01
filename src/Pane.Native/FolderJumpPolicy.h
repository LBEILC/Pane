#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include <algorithm>

namespace pane::folderjump {
struct Control { HWND window; HWND parent; std::wstring type; int id; bool visible=true; };
inline HWND Filename(const std::wstring& type, const std::vector<Control>& controls) {
    if(type!=L"#32770")return nullptr;
    bool shell=false,accept=false;
    for(const auto& c:controls){
        shell|=c.visible&&c.type==L"SHELLDLL_DefView";
        accept|=c.visible&&c.type==L"Button"&&c.id==IDOK;
    }
    if(!shell||!accept)return nullptr;
    for(const auto& c:controls)if(c.visible&&c.type==L"Edit"&&c.id==0x480)return c.window;
    for(const auto& c:controls)if(c.visible&&c.type==L"Edit")
        for(const auto& p:controls)if(p.visible&&p.window==c.parent&&p.type==L"ComboBox"&&p.id==0x47c)return c.window;
    // 另存为的文件类型框没有 Edit；地址栏使用自己的非零 ID。
    // 只接受 FloatNotifySink 下唯一的可见文件名候选，歧义时不接管。
    HWND candidate=nullptr;
    for(const auto& c:controls)if(c.visible&&c.type==L"Edit"&&c.id==1001)
        for(const auto& p:controls)if(p.visible&&p.window==c.parent&&p.type==L"ComboBox"&&p.id==0)
            for(const auto& sink:controls)if(sink.visible&&sink.window==p.parent&&sink.type==L"FloatNotifySink"){
                if(candidate)return nullptr;
                candidate=c.window;
            }
    return candidate;
}
// 注册与注销都在接收 WM_HOTKEY 的线程执行，失败的键不会被当作已注册。
struct Hotkeys {
    HWND dialog=nullptr; bool direct=false,menu=false;
    template<class Register,class Unregister> void Change(HWND next,Register reg,Unregister unreg){
        if(next==dialog)return;
        if(direct)unreg(1);if(menu)unreg(2);
        direct=menu=false;dialog=next;
        if(next){direct=reg(1);menu=reg(2);}
    }
};
struct Entry { HWND window=nullptr; std::wstring path,name; bool recent=false; unsigned long long order=0; };
inline std::wstring Key(std::wstring path){
    std::replace(path.begin(),path.end(),L'/',L'\\');
    while(path.size()>3&&path.back()==L'\\')path.pop_back();
    if(!path.empty())CharLowerBuffW(path.data(),static_cast<DWORD>(path.size()));
    return path;
}
template<class Exists> std::vector<Entry> Select(std::vector<Entry> entries,Exists exists,bool showRecent=true){
    std::stable_sort(entries.begin(),entries.end(),[](const Entry& a,const Entry& b){return a.recent!=b.recent?!a.recent:a.order>b.order;});
    std::vector<Entry> result;std::vector<std::wstring> seen;int recent=0;
    for(auto& e:entries){
        if(e.recent&&!showRecent)continue;
        // 仅接受绝对文件系统目录；虚拟 Shell 位置和文件都过滤。
        if(e.path.empty()||!((e.path.size()>2&&e.path[1]==L':'&&e.path[2]==L'\\')||e.path.rfind(L"\\\\",0)==0)||!exists(e.path))continue;
        auto key=Key(e.path);if(std::find(seen.begin(),seen.end(),key)!=seen.end())continue;
        if(e.recent&&recent>=8)continue;
        seen.push_back(key);if(e.recent)recent++;result.push_back(std::move(e));
    }
    return result;
}
}
