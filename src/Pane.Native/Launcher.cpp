#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <appmodel.h>
#include <shobjidl.h>
#include <propkey.h>
#include <propsys.h>
#include <wincodec.h>
#include <powrprof.h>
#include <wrl/client.h>
#include <filesystem>
#include <vector>
#include <string>
#include <atomic>
#include "BoundedOpen.h"
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED); ~Apartment(){if(SUCCEEDED(hr)){CoFreeUnusedLibrariesEx(0,0);CoUninitialize();}} };
using AppCallback=void(__cdecl*)(const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,int);
std::wstring property(IShellItem2* item,REFPROPERTYKEY key){PWSTR p=nullptr;std::wstring value;if(SUCCEEDED(item->GetString(key,&p))&&p){value=p;CoTaskMemFree(p);}return value;}
std::wstring display(IShellItem* item,SIGDN kind){PWSTR p=nullptr;std::wstring value;if(SUCCEEDED(item->GetDisplayName(kind,&p))&&p){value=p;CoTaskMemFree(p);}return value;}
HWND endpoint(){HWND h=FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION",nullptr);return h?h:FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)",nullptr);}
struct Query {HWND target=nullptr;DWORD serial=0,limit=0,total=0;bool done=false,extended=false;std::vector<long long> sizes,dates;std::vector<std::wstring> names,folders;std::vector<int> kinds;};
bool text(COPYDATASTRUCT* c,DWORD offset,std::wstring& value){if(offset%2||offset>=c->cbData)return false;auto p=reinterpret_cast<const wchar_t*>(static_cast<const BYTE*>(c->lpData)+offset);size_t n=(c->cbData-offset)/2,i=0;while(i<n&&p[i])++i;if(i==n)return false;value.assign(p,i);return true;}
LRESULT CALLBACK replyProc(HWND h,UINT m,WPARAM w,LPARAM l){
 if(m==WM_NCCREATE)SetWindowLongPtrW(h,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams));
 auto q=reinterpret_cast<Query*>(GetWindowLongPtrW(h,GWLP_USERDATA));
 if(m==WM_COPYDATA&&q){auto c=reinterpret_cast<COPYDATASTRUCT*>(l);if(reinterpret_cast<HWND>(w)!=q->target||c->dwData!=q->serial||!c->lpData||c->cbData<(q->extended?20u:28u)||c->cbData>4*1024*1024)return FALSE;
  auto d=static_cast<const DWORD*>(c->lpData);
  if(q->extended){
   DWORD n=d[1];if(n>q->limit||n>(c->cbData-20)/8||(n>0&&d[3]!=0x53))return FALSE;
   auto bytes=static_cast<const BYTE*>(c->lpData);
   for(DWORD i=0;i<n;i++){
    DWORD offset=d[6+2*i];std::wstring name,folder;
    auto read=[&](std::wstring& value){if(offset>c->cbData-4)return false;DWORD length;memcpy(&length,bytes+offset,4);offset+=4;if(length>(c->cbData-offset)/2||2ull*(length+1)>c->cbData-offset)return false;value.assign(reinterpret_cast<const wchar_t*>(bytes+offset),length);offset+=2*(length+1);return true;};
    if(!read(name)||!read(folder)||offset>c->cbData-16)return FALSE;
    long long size,date;memcpy(&size,bytes+offset,8);memcpy(&date,bytes+offset+8,8);
    q->names.push_back(name);q->folders.push_back(folder);q->kinds.push_back((d[5+2*i]&1)?1:0);q->sizes.push_back(size);q->dates.push_back(date);
   }q->total=d[0];q->done=true;return TRUE;
  }
  DWORD n=d[5];if(n>q->limit||28+12*n>c->cbData)return FALSE;
  for(DWORD i=0;i<n;i++){std::wstring name,folder;if(!text(c,d[8+3*i],name)||!text(c,d[9+3*i],folder))return FALSE;q->names.push_back(name);q->folders.push_back(folder);q->kinds.push_back((d[7+3*i]&1)?1:0);}q->done=true;return TRUE;
 }return DefWindowProcW(h,m,w,l);
}
}
API int PaneLauncherEnumerate(AppCallback callback){
 Apartment apartment;ComPtr<IShellItem> apps;HRESULT hr=SHCreateItemFromParsingName(L"shell:AppsFolder",nullptr,IID_PPV_ARGS(&apps));if(FAILED(hr))return static_cast<int>(hr);
 ComPtr<IEnumShellItems> items;hr=apps->BindToHandler(nullptr,BHID_EnumItems,IID_PPV_ARGS(&items));if(FAILED(hr))return static_cast<int>(hr);
 ComPtr<IShellItem> item;
 while(items->Next(1,&item,nullptr)==S_OK){ComPtr<IShellItem2> props;item.As(&props);if(props){auto name=display(item.Get(),SIGDN_NORMALDISPLAY),parsing=display(item.Get(),SIGDN_DESKTOPABSOLUTEPARSING);auto id=property(props.Get(),PKEY_AppUserModel_ID),target=property(props.Get(),PKEY_Link_TargetParsingPath);callback(name.c_str(),id.c_str(),target.c_str(),L"",parsing.c_str(),0);}item.Reset();}
 for(auto known:{FOLDERID_Programs,FOLDERID_CommonPrograms}){PWSTR root=nullptr;if(FAILED(SHGetKnownFolderPath(known,0,nullptr,&root)))continue;std::error_code error;
  for(std::filesystem::recursive_directory_iterator it(root,std::filesystem::directory_options::skip_permission_denied,error),end;it!=end;it.increment(error)){
   if(error){error.clear();continue;}auto path=it->path();if(_wcsicmp(path.extension().c_str(),L".lnk")!=0)continue;
   ComPtr<IShellLinkW> link;ComPtr<IPersistFile> file;if(FAILED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link)))||FAILED(link.As(&file))||FAILED(file->Load(path.c_str(),STGM_READ)))continue;
   wchar_t target[32768]{},args[32768]{};link->GetPath(target,32768,nullptr,SLGP_RAWPATH);link->GetArguments(args,32768);ComPtr<IShellItem2> props;std::wstring id;if(SUCCEEDED(SHCreateItemFromParsingName(path.c_str(),nullptr,IID_PPV_ARGS(&props))))id=property(props.Get(),PKEY_AppUserModel_ID);
   auto name=props?display(props.Get(),SIGDN_NORMALDISPLAY):path.stem().wstring();
   callback(name.c_str(),id.c_str(),target,args,path.c_str(),1);
  }CoTaskMemFree(root);
 }return 0;
}
// 保留枚举的旧目标用于稳定 Id；规范化目标、参数和图标位置单独返回。
API int PaneLauncherResolve(const wchar_t* parsing,wchar_t* target,wchar_t* args,wchar_t* icon,int* iconIndex){
 ComPtr<IShellItem2> item;if(FAILED(SHCreateItemFromParsingName(parsing,nullptr,IID_PPV_ARGS(&item))))return 0;
 auto t=property(item.Get(),PKEY_Link_TargetParsingPath),a=property(item.Get(),PKEY_Link_Arguments);
 std::wstring shortcut=parsing;PROPERTYKEY key{};PROPVARIANT value{};
 if(SUCCEEDED(PSGetPropertyKeyFromName(L"System.AppUserModel.BestShortcut",&key))&&SUCCEEDED(item->GetProperty(key,&value))){
  if(value.vt==(VT_VECTOR|VT_UI1)&&value.caub.cElems>=2){ComPtr<IShellItem> best;if(SUCCEEDED(SHCreateItemFromIDList(reinterpret_cast<PCIDLIST_ABSOLUTE>(value.caub.pElems),IID_PPV_ARGS(&best))))shortcut=display(best.Get(),SIGDN_FILESYSPATH);}
 }PropVariantClear(&value);
 std::wstring location;int index=0;auto ext=std::filesystem::path(shortcut).extension().wstring();
 if(_wcsicmp(ext.c_str(),L".lnk")==0){
  ComPtr<IShellLinkW> link;ComPtr<IPersistFile> file;
  if(SUCCEEDED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link)))&&SUCCEEDED(link.As(&file))&&SUCCEEDED(file->Load(shortcut.c_str(),STGM_READ))){
   wchar_t buffer[32768]{};link->GetPath(buffer,32768,nullptr,SLGP_RAWPATH);if(buffer[0])t=buffer;
   else {PIDLIST_ABSOLUTE pidl=nullptr;if(SUCCEEDED(link->GetIDList(&pidl))&&pidl){ComPtr<IShellItem> dest;if(SUCCEEDED(SHCreateItemFromIDList(pidl,IID_PPV_ARGS(&dest))))t=display(dest.Get(),SIGDN_DESKTOPABSOLUTEPARSING);CoTaskMemFree(pidl);}}
   buffer[0]=0;link->GetArguments(buffer,32768);a=buffer;buffer[0]=0;link->GetIconLocation(buffer,32768,&index);location=buffer;
  }
 }else if(_wcsicmp(ext.c_str(),L".url")==0){wchar_t buffer[32768]{};GetPrivateProfileStringW(L"InternetShortcut",L"IconFile",L"",buffer,32768,shortcut.c_str());location=buffer;index=GetPrivateProfileIntW(L"InternetShortcut",L"IconIndex",0,shortcut.c_str());}
 if(location.empty()){
  PIDLIST_ABSOLUTE pidl=nullptr;if(SUCCEEDED(SHParseDisplayName(parsing,nullptr,&pidl,0,nullptr))){ComPtr<IShellFolder> parent;PCUITEMID_CHILD child=nullptr;
   if(SUCCEEDED(SHBindToParent(pidl,IID_PPV_ARGS(&parent),&child))){ComPtr<IExtractIconW> extract;if(SUCCEEDED(parent->GetUIObjectOf(nullptr,1,&child,IID_IExtractIconW,nullptr,&extract))){wchar_t buffer[32768]{};UINT flags=0;int resourceIndex=0;if(SUCCEEDED(extract->GetIconLocation(0,buffer,32768,&resourceIndex,&flags))&&!(flags&GIL_NOTFILENAME)){location=buffer;index=resourceIndex;}}}
   CoTaskMemFree(pidl);
  }
 }
 if(location.empty()){location=t;index=0;}
 wchar_t expanded[32768]{};ExpandEnvironmentStringsW(location.c_str(),expanded,32768);
 wcscpy_s(target,32768,t.c_str());wcscpy_s(args,32768,a.c_str());wcscpy_s(icon,32768,expanded);*iconIndex=index;return 1;
}
// 通过包完整名称取得带版本的目录，用于图标缓存失效。
API int PaneLauncherPackagePath(const wchar_t* id,wchar_t* output,int capacity){
 std::wstring family(id);auto bang=family.find(L'!');if(bang==std::wstring::npos)return 0;family.resize(bang);
 UINT32 count=0,length=0;LONG result=GetPackagesByPackageFamily(family.c_str(),&count,nullptr,&length,nullptr);
 if(result!=ERROR_INSUFFICIENT_BUFFER||!count)return 0;
 std::vector<PWSTR> names(count);std::vector<wchar_t> buffer(length);
 if(GetPackagesByPackageFamily(family.c_str(),&count,names.data(),&length,buffer.data())!=ERROR_SUCCESS)return 0;
 for(UINT32 i=0;i<count;i++){UINT32 size=static_cast<UINT32>(capacity);if(GetPackagePathByFullName(names[i],&size,output)==ERROR_SUCCESS)return 1;}return 0;
}
static int shellIcon(const wchar_t* parsing,const wchar_t* output,int size){
 Apartment apartment;
 ComPtr<IShellItemImageFactory> item;HRESULT found=SHCreateItemFromParsingName(parsing,nullptr,IID_PPV_ARGS(&item));if(FAILED(found))return static_cast<int>(found);
 HBITMAP bitmap=nullptr;found=item->GetImage({size,size},SIIGBF_ICONONLY,&bitmap);if(FAILED(found))return static_cast<int>(found);
 ComPtr<IWICImagingFactory> factory;ComPtr<IWICBitmap> image;ComPtr<IWICStream> stream;ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;
 HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));
 if(SUCCEEDED(hr))hr=factory->CreateBitmapFromHBITMAP(bitmap,nullptr,WICBitmapUsePremultipliedAlpha,&image);
 if(SUCCEEDED(hr))hr=factory->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(output,GENERIC_WRITE);
 if(SUCCEEDED(hr))hr=factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder);if(SUCCEEDED(hr))hr=encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache);
 if(SUCCEEDED(hr))hr=encoder->CreateNewFrame(&frame,nullptr);if(SUCCEEDED(hr))hr=frame->Initialize(nullptr);
 // 文件图标固定输出 64 px；Shell 可能只返回较小的原始图标，保留 alpha 后缩放。
 ComPtr<IWICBitmapScaler> scaled;
 if(SUCCEEDED(hr)&&size==64)hr=factory->CreateBitmapScaler(&scaled);
 if(SUCCEEDED(hr)&&scaled)hr=scaled->Initialize(image.Get(),64,64,WICBitmapInterpolationModeFant);
 if(SUCCEEDED(hr))hr=frame->WriteSource(scaled?static_cast<IWICBitmapSource*>(scaled.Get()):static_cast<IWICBitmapSource*>(image.Get()),nullptr);if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=encoder->Commit();DeleteObject(bitmap);return SUCCEEDED(hr)?1:static_cast<int>(hr);
}
API int PaneLauncherIcon(const wchar_t* parsing,const wchar_t* output){return shellIcon(parsing,output,256);}
API int PaneFileIcon(const wchar_t* path,const wchar_t* output){return shellIcon(path,output,64);}
API int PaneEverythingVersion(int* version){auto h=endpoint();if(!h)return 0;for(int i=0;i<4;i++){DWORD_PTR v=0;if(!SendMessageTimeoutW(h,WM_USER,static_cast<WPARAM>(i),0,SMTO_ABORTIFHUNG,30,&v))return -1;version[i]=static_cast<int>(v);}return version[0]>1||(version[0]==1&&version[1]>=4)?1:-1;}
using FileCallback=void(__cdecl*)(const wchar_t*,const wchar_t*,int);
API int PaneEverythingQuery(const wchar_t* query,int limit,FileCallback callback){
 auto start=GetTickCount64();Query q;q.target=endpoint();if(!q.target)return 0;static std::atomic<DWORD> serial=0;q.serial=++serial;q.limit=static_cast<DWORD>(limit);
 WNDCLASSW wc{};wc.lpfnWndProc=replyProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaneLauncherReply";RegisterClassW(&wc);
 HWND h=CreateWindowW(wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,&q);if(!h)return -1;
 size_t bytes=(wcslen(query)+1)*2;std::vector<BYTE> payload(20+bytes);DWORD header[5]={static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(h)),q.serial,0,0,q.limit};memcpy(payload.data(),header,20);memcpy(payload.data()+20,query,bytes);
 COPYDATASTRUCT c{2,static_cast<DWORD>(payload.size()),payload.data()};DWORD_PTR accepted=0;
 bool sent=SendMessageTimeoutW(q.target,WM_COPYDATA,reinterpret_cast<WPARAM>(h),reinterpret_cast<LPARAM>(&c),SMTO_ABORTIFHUNG,150,&accepted)!=0;
 while(sent&&accepted&&!q.done&&GetTickCount64()-start<150){MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}if(!q.done)MsgWaitForMultipleObjects(0,nullptr,FALSE,1,QS_ALLINPUT);}
 DestroyWindow(h);if(!q.done)return -1;for(size_t i=0;i<q.names.size();i++)callback(q.names[i].c_str(),q.folders[i].c_str(),q.kinds[i]);return static_cast<int>(q.names.size());
}
// QUERY2 字段与布局依据 Everything SDK 的 everything_ipc.h。
using FilePageCallback=void(__cdecl*)(const wchar_t*,const wchar_t*,int,long long,long long);
API int PaneEverythingPage(const wchar_t* query,int offset,int limit,int sort,FilePageCallback callback,int* total){
 Query q;q.extended=true;q.target=endpoint();if(!q.target)return 0;
 static std::atomic<DWORD> serial=100000;q.serial=++serial;q.limit=static_cast<DWORD>(limit);
 WNDCLASSW wc{};wc.lpfnWndProc=replyProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaneFilesReply";RegisterClassW(&wc);
 HWND h=CreateWindowW(wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,&q);if(!h)return -1;
 size_t bytes=(wcslen(query)+1)*2;std::vector<BYTE> payload(28+bytes);
 DWORD header[7]={static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(h)),q.serial,0,static_cast<DWORD>(offset),q.limit,0x53,static_cast<DWORD>(sort)};
 memcpy(payload.data(),header,28);memcpy(payload.data()+28,query,bytes);
 COPYDATASTRUCT c{18,static_cast<DWORD>(payload.size()),payload.data()};DWORD_PTR accepted=0;auto start=GetTickCount64();
 bool sent=SendMessageTimeoutW(q.target,WM_COPYDATA,reinterpret_cast<WPARAM>(h),reinterpret_cast<LPARAM>(&c),SMTO_ABORTIFHUNG,150,&accepted)!=0;
 while(sent&&accepted&&!q.done&&GetTickCount64()-start<150){MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}if(!q.done)MsgWaitForMultipleObjects(0,nullptr,FALSE,1,QS_ALLINPUT);}
 DestroyWindow(h);if(!q.done)return -1;*total=static_cast<int>((std::min)(q.total,static_cast<DWORD>(INT_MAX)));
 for(size_t i=0;i<q.names.size();i++)callback(q.names[i].c_str(),q.folders[i].c_str(),q.kinds[i],q.sizes[i],q.dates[i]);return 1;
}
namespace {
int launcherOpen(const wchar_t* target,int packaged){
 Apartment apartment;AllowSetForegroundWindow(ASFW_ANY);
 if(packaged){ComPtr<IApplicationActivationManager> manager;DWORD pid=0;if(FAILED(CoCreateInstance(CLSID_ApplicationActivationManager,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&manager))))return 0;CoAllowSetForegroundWindow(manager.Get(),nullptr);auto hr=manager->ActivateApplication(target,nullptr,AO_NONE,&pid);if(SUCCEEDED(hr)&&pid)AllowSetForegroundWindow(pid);return SUCCEEDED(hr);}
 // 文件属性查询也在受限工作线程内，网络路径不可达时不会占住调用者。
 DWORD attributes=GetFileAttributesW(target);
 if(attributes!=INVALID_FILE_ATTRIBUTES&&(attributes&FILE_ATTRIBUTE_DIRECTORY)){
  wchar_t windows[32768]{};UINT length=GetWindowsDirectoryW(windows,32768);
  if(!length||length>=32768)return 0;
  std::wstring explorer=std::wstring(windows)+L"\\explorer.exe",folder=target;
  const auto root=std::filesystem::path(folder).root_path().wstring();
  while(folder.size()>root.size()&&(folder.back()==L'\\'||folder.back()==L'/'))folder.pop_back();
  // Explorer 使用自己的命令行解析器，根目录保留原来的单个分隔符。
  std::wstring command=L"\""+explorer+L"\" \""+folder+L"\"";
  STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
  if(!CreateProcessW(explorer.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&process))return 0;
  AllowSetForegroundWindow(process.dwProcessId);CloseHandle(process.hThread);CloseHandle(process.hProcess);return 1;
 }
 SHELLEXECUTEINFOW info{sizeof(info)};info.fMask=SEE_MASK_NOCLOSEPROCESS|SEE_MASK_NOASYNC|SEE_MASK_FLAG_NO_UI;info.lpVerb=L"open";info.lpFile=target;info.nShow=SW_SHOWNORMAL;
 PIDLIST_ABSOLUTE item=nullptr;if(wcsncmp(target,L"::",2)==0&&SUCCEEDED(SHParseDisplayName(target,nullptr,&item,0,nullptr))){info.fMask|=SEE_MASK_IDLIST;info.lpIDList=item;info.lpFile=nullptr;}
 bool ok=ShellExecuteExW(&info)!=0;DWORD error=ok?ERROR_SUCCESS:GetLastError();if(info.hProcess){AllowSetForegroundWindow(GetProcessId(info.hProcess));CloseHandle(info.hProcess);}CoTaskMemFree(item);return !ok&&error==ERROR_CANCELLED?-ERROR_CANCELLED:static_cast<int>(ok);
}
}
API int PaneLauncherOpen(const wchar_t* target,int packaged){
 if(!target||!*target)return 0;
 // 保持工作槽存活到进程结束，超时任务仍可能引用它。
 static auto* operation=new BoundedOpen;
 // 模式 2 只由内置系统组件入口使用，允许用户在 UAC 中停留；普通文件仍受 5 秒上限保护。
 try {return operation->run([value=std::wstring(target),packaged]{return launcherOpen(value.c_str(),packaged==1);},packaged==2?INFINITE:5000);}
 catch(...) {return 0;}
}
API int PaneLauncherReveal(const wchar_t* path){Apartment apartment;PIDLIST_ABSOLUTE item=nullptr;if(FAILED(SHParseDisplayName(path,nullptr,&item,0,nullptr)))return 0;auto hr=SHOpenFolderAndSelectItems(item,0,nullptr,0);CoTaskMemFree(item);return SUCCEEDED(hr);}
extern "C" int PaneClipWrite(const wchar_t*,const wchar_t*,const wchar_t*,int);
API int PaneLauncherCopy(const wchar_t* value){return PaneClipWrite(value,nullptr,nullptr,0); }
API int PaneLauncherCommand(int command){
 if(command==0)return LockWorkStation();if(command==1)return SetSuspendState(FALSE,FALSE,FALSE);
 if(command==5)return ExitWindowsEx(EWX_LOGOFF,SHTDN_REASON_MAJOR_APPLICATION|SHTDN_REASON_FLAG_PLANNED);
 if(command==6)return SetSuspendState(TRUE,FALSE,FALSE);
 if(command==4)return SUCCEEDED(SHEmptyRecycleBinW(nullptr,nullptr,0));
 if(command!=2&&command!=3)return 0;
 HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_ADJUST_PRIVILEGES|TOKEN_QUERY,&token))return 0;
 TOKEN_PRIVILEGES privileges{},previous{};DWORD size=sizeof(previous);privileges.PrivilegeCount=1;LookupPrivilegeValueW(nullptr,L"SeShutdownPrivilege",&privileges.Privileges[0].Luid);privileges.Privileges[0].Attributes=SE_PRIVILEGE_ENABLED;
 SetLastError(0);bool enabled=AdjustTokenPrivileges(token,FALSE,&privileges,sizeof(previous),&previous,&size)&&GetLastError()==ERROR_SUCCESS;
 bool ok=enabled&&ExitWindowsEx(command==2?EWX_REBOOT:EWX_POWEROFF,SHTDN_REASON_MAJOR_APPLICATION|SHTDN_REASON_FLAG_PLANNED);if(enabled)AdjustTokenPrivileges(token,FALSE,&previous,0,nullptr,nullptr);CloseHandle(token);return ok;
}
