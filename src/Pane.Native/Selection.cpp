#define NOMINMAX
#include <windows.h>
#include <UIAutomation.h>
#include <exdisp.h>
#include <shldisp.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include <atomic>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
thread_local std::wstring diagnostic;
bool required(CLIPFORMAT format){
 if(format==CF_UNICODETEXT||format==CF_TEXT||format==CF_HDROP||format==CF_DIB||format==CF_DIBV5)return true;
 wchar_t name[256]{};GetClipboardFormatNameW(format,name,256);
 return !wcscmp(name,L"PNG")||!wcscmp(name,L"HTML Format")||!wcscmp(name,L"Rich Text Format");
}
void formatFailure(const FORMATETC& f,HRESULT hr){
 wchar_t name[256]{};GetClipboardFormatNameW(f.cfFormat,name,256);wchar_t code[32]{};swprintf_s(code,L"0x%08X",static_cast<unsigned>(hr));
 diagnostic+=L" format="+std::to_wstring(f.cfFormat)+L" name="+name+L" tymed="+std::to_wstring(f.tymed)+L" hr="+code+L" required="+(required(f.cfFormat)?L"true":L"false");
}
struct Apartment { HRESULT hr=OleInitialize(nullptr); ~Apartment(){if(SUCCEEDED(hr))OleUninitialize();} };
bool elevated(HANDLE process){HANDLE token{};TOKEN_ELEVATION e{};DWORD n{};if(!OpenProcessToken(process,TOKEN_QUERY,&token))return false;bool ok=GetTokenInformation(token,TokenElevation,&e,sizeof(e),&n)!=0;CloseHandle(token);return ok&&e.TokenIsElevated;}
HRESULT freeze(STGMEDIUM& source,STGMEDIUM& target){
 // 不保留源应用的释放代理；快照必须独立于其下一次复制。
 if(source.tymed==TYMED_ISTREAM){
  ComPtr<IStream> stream;HRESULT hr=CreateStreamOnHGlobal(nullptr,TRUE,&stream);if(FAILED(hr))return hr;
  LARGE_INTEGER zero{};ULARGE_INTEGER position{},size{};STATSTG stat{};
  if(FAILED(source.pstm->Stat(&stat,STATFLAG_NONAME))||FAILED(source.pstm->Seek(zero,STREAM_SEEK_CUR,&position))||FAILED(source.pstm->Seek(zero,STREAM_SEEK_SET,nullptr)))return E_FAIL;
  size=stat.cbSize;hr=source.pstm->CopyTo(stream.Get(),size,nullptr,nullptr);LARGE_INTEGER old{};old.QuadPart=static_cast<LONGLONG>(position.QuadPart);source.pstm->Seek(old,STREAM_SEEK_SET,nullptr);
  if(FAILED(hr))return hr;stream->Seek(zero,STREAM_SEEK_SET,nullptr);target.tymed=TYMED_ISTREAM;target.pstm=stream.Detach();return S_OK;
 }
 if(source.tymed==TYMED_ISTORAGE){ComPtr<ILockBytes> bytes;ComPtr<IStorage> storage;HRESULT hr=CreateILockBytesOnHGlobal(nullptr,TRUE,&bytes);if(FAILED(hr))return hr;hr=StgCreateDocfileOnILockBytes(bytes.Get(),STGM_CREATE|STGM_READWRITE|STGM_SHARE_EXCLUSIVE,0,&storage);if(FAILED(hr))return hr;hr=source.pstg->CopyTo(0,nullptr,nullptr,storage.Get());if(FAILED(hr))return hr;target.tymed=TYMED_ISTORAGE;target.pstg=storage.Detach();return S_OK;}
 auto independent=source;independent.pUnkForRelease=nullptr;return CopyStgMedium(&independent,&target);
}
struct SavedData : IDataObject {
 std::atomic<ULONG> refs{1};std::vector<FORMATETC> formats;std::vector<STGMEDIUM> media;
 ~SavedData(){for(auto& m:media)ReleaseStgMedium(&m);for(auto& f:formats)CoTaskMemFree(f.ptd);}
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p) override {if(id==IID_IUnknown||id==IID_IDataObject){*p=this;AddRef();return S_OK;}*p=nullptr;return E_NOINTERFACE;}
 ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;} ULONG STDMETHODCALLTYPE Release() override{auto n=--refs;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE GetData(FORMATETC* f,STGMEDIUM* m) override{for(size_t i=0;i<formats.size();i++)if(formats[i].cfFormat==f->cfFormat&&formats[i].dwAspect==f->dwAspect&&formats[i].lindex==f->lindex&&(media[i].tymed&f->tymed))return CopyStgMedium(&media[i],m);return DV_E_FORMATETC;}
 HRESULT STDMETHODCALLTYPE GetDataHere(FORMATETC*,STGMEDIUM*) override{return E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE QueryGetData(FORMATETC* f) override{for(auto& a:formats)if(a.cfFormat==f->cfFormat&&(a.tymed&f->tymed))return S_OK;return DV_E_FORMATETC;}
 HRESULT STDMETHODCALLTYPE GetCanonicalFormatEtc(FORMATETC*,FORMATETC* f) override{f->ptd=nullptr;return E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE SetData(FORMATETC*,STGMEDIUM*,BOOL) override{return E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE EnumFormatEtc(DWORD d,IEnumFORMATETC** e) override{return d==DATADIR_GET?SHCreateStdEnumFmtEtc(static_cast<UINT>(formats.size()),formats.data(),e):E_NOTIMPL;}
 HRESULT STDMETHODCALLTYPE DAdvise(FORMATETC*,DWORD,IAdviseSink*,DWORD*) override{return OLE_E_ADVISENOTSUPPORTED;}
 HRESULT STDMETHODCALLTYPE DUnadvise(DWORD) override{return OLE_E_ADVISENOTSUPPORTED;}
 HRESULT STDMETHODCALLTYPE EnumDAdvise(IEnumSTATDATA**) override{return OLE_E_ADVISENOTSUPPORTED;}
};
}
static int selectionDirectOnce(HWND target,HWND fixture,std::wstring& value){
 diagnostic=L"uiaPattern=none";
 Apartment apartment;DWORD pid{};GetWindowThreadProcessId(target,&pid);HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
 bool high=process&&elevated(process)&&!elevated(GetCurrentProcess());if(process)CloseHandle(process);if(high)return 3;
 if(!fixture&&GetForegroundWindow()!=target)return 4;
 bool supported=false;ComPtr<IUIAutomation> automation;
 if(SUCCEEDED(CoCreateInstance(CLSID_CUIAutomation,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation)))){
  ComPtr<IUIAutomationElement> focus;ComPtr<IUIAutomationTextPattern> pattern;ComPtr<IUIAutomationTextRangeArray> ranges;
  if(SUCCEEDED(fixture?automation->ElementFromHandle(fixture,&focus):automation->GetFocusedElement(&focus))&&focus){
   // 自建非激活窗口测试从窗口内定位文档，避免切换用户的前台焦点。
   if(fixture){ComPtr<IUIAutomationTextPattern> own;focus->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&own));if(!own){VARIANT yes{};yes.vt=VT_BOOL;yes.boolVal=VARIANT_TRUE;ComPtr<IUIAutomationCondition> condition;ComPtr<IUIAutomationElement> document;automation->CreatePropertyCondition(UIA_IsTextPatternAvailablePropertyId,yes,&condition);if(condition&&SUCCEEDED(focus->FindFirst(TreeScope_Descendants,condition.Get(),&document))&&document)focus=document;}}
   ComPtr<IUIAutomationTreeWalker> walker;automation->get_ControlViewWalker(&walker);
   for(int depth=0;focus&&depth<8;depth++){
    if(SUCCEEDED(focus->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&pattern)))&&pattern){diagnostic=L"uiaPattern=text depth="+std::to_wstring(depth);break;}
    UIA_HWND handle{};focus->get_CurrentNativeWindowHandle(&handle);if(reinterpret_cast<HWND>(handle)==target)break;
    if(!walker)break;ComPtr<IUIAutomationElement> parent;if(FAILED(walker->GetParentElement(focus.Get(),&parent)))break;focus=parent;
   }
  }
  if(pattern&&SUCCEEDED(pattern->GetSelection(&ranges))&&ranges){
   supported=true;int n{};ranges->get_Length(&n);for(int i=0;i<n;i++){ComPtr<IUIAutomationTextRange> range;BSTR text{};if(SUCCEEDED(ranges->GetElement(i,&range))&&SUCCEEDED(range->GetText(-1,&text))&&text){if(!value.empty())value+=L"\n";value+=text;SysFreeString(text);}}
  }
 }
 wchar_t cls[256]{};GetClassNameW(target,cls,256);
 if(value.empty()&&(!wcscmp(cls,L"CabinetWClass")||!wcscmp(cls,L"ExploreWClass"))){
  ComPtr<IShellWindows> windows;if(SUCCEEDED(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&windows)))){
   long n{};windows->get_Count(&n);for(long i=0;i<n;i++){VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> d;if(FAILED(windows->Item(index,&d)))continue;
    ComPtr<IWebBrowser2> browser;if(FAILED(d.As(&browser)))continue;SHANDLE_PTR h{};browser->get_HWND(&h);if(reinterpret_cast<HWND>(h)!=target)continue;
    ComPtr<IDispatch> document;ComPtr<IShellFolderViewDual> view;ComPtr<FolderItems> items;
    if(SUCCEEDED(browser->get_Document(&document))&&SUCCEEDED(document.As(&view))&&SUCCEEDED(view->SelectedItems(&items))){supported=true;long count{};items->get_Count(&count);for(long j=0;j<count;j++){VARIANT at{};at.vt=VT_I4;at.lVal=j;ComPtr<FolderItem> item;BSTR path{};if(SUCCEEDED(items->Item(at,&item))&&SUCCEEDED(item->get_Path(&path))&&path){if(!value.empty())value+=L"\n";value+=path;SysFreeString(path);}}}break;
   }
  }
 }
 if(!fixture&&GetForegroundWindow()!=target)return 4;
 diagnostic+=value.empty()?L" uiaSelection=empty":L" uiaSelection=present";
 if(value.empty())return supported?1:2;return 0;
}
static int selectionDirect(HWND target,HWND fixture,std::wstring& value){
 ULONGLONG start=GetTickCount64();int result=selectionDirectOnce(target,fixture,value);
 wchar_t cls[256]{};GetClassNameW(target,cls,256);
 if((result==1||result==2)&&(fixture||wcsstr(cls,L"Chrome_"))&&GetTickCount64()-start<220){
  std::wstring first=diagnostic;Sleep(25);result=selectionDirectOnce(target,fixture,value);diagnostic=L"first={"+first+L"} retry=1 "+diagnostic;
 }
 return result;
}
API const wchar_t* PaneSelectionDiagnostics(){return diagnostic.c_str();}
API int PaneSelectionDirectAlloc(HWND target,wchar_t** output){*output=nullptr;std::wstring value;int result=selectionDirect(target,nullptr,value);if(result)return result;auto memory=static_cast<wchar_t*>(CoTaskMemAlloc((value.size()+1)*sizeof(wchar_t)));if(!memory)return 2;memcpy(memory,value.c_str(),(value.size()+1)*sizeof(wchar_t));*output=memory;return 0;}
// 仅供自建窗口测试：禁止读取本进程以外的元素，生产入口仍要求前台和焦点。
API int PaneSelectionFixture(HWND target,HWND element,wchar_t* output,int capacity){DWORD a{},b{};GetWindowThreadProcessId(target,&a);GetWindowThreadProcessId(element,&b);if(a!=GetCurrentProcessId()||b!=a)return 2;std::wstring value;int result=selectionDirect(target,element,value);if(result==0)wcsncpy_s(output,capacity,value.c_str(),_TRUNCATE);return result;}
static void* snapshotFrom(IDataObject* source,bool checkAvailable){
 ComPtr<IEnumFORMATETC> enumeration;if(FAILED(source->EnumFormatEtc(DATADIR_GET,&enumeration))){diagnostic+=L" enumFailed=true";return nullptr;}
 auto saved=new SavedData();FORMATETC f{};ULONG n{};
 HRESULT hr{};while((hr=enumeration->Next(1,&f,&n))==S_OK){
  STGMEDIUM m{},copy{};HRESULT status=f.ptd?DV_E_DVTARGETDEVICE:source->GetData(&f,&m);
  if(SUCCEEDED(status))status=m.tymed==TYMED_FILE?DV_E_TYMED:freeze(m,copy);
  if(m.tymed)ReleaseStgMedium(&m);
  if(FAILED(status)){formatFailure(f,status);bool essential=required(f.cfFormat);CoTaskMemFree(f.ptd);if(copy.tymed)ReleaseStgMedium(&copy);if(essential){saved->Release();return nullptr;}continue;}
  f.tymed=copy.tymed;saved->formats.push_back(f);saved->media.push_back(copy);
 }
 if(hr!=S_FALSE){diagnostic+=L" enumIncomplete=true";saved->Release();return nullptr;}
 // 防止枚举提供者遗漏 Windows 实际声明存在的必需格式。
 const UINT essentials[]={CF_UNICODETEXT,CF_TEXT,CF_HDROP,CF_DIB,CF_DIBV5,RegisterClipboardFormatW(L"PNG"),RegisterClipboardFormatW(L"HTML Format"),RegisterClipboardFormatW(L"Rich Text Format")};
 for(auto format:essentials){if(!checkAvailable||!IsClipboardFormatAvailable(format))continue;bool found=false;for(auto& item:saved->formats)if(item.cfFormat==format)found=true;if(found)continue;
  FORMATETC needed{static_cast<CLIPFORMAT>(format),nullptr,DVASPECT_CONTENT,-1,TYMED_HGLOBAL};STGMEDIUM m{},copy{};HRESULT status=source->GetData(&needed,&m);if(SUCCEEDED(status))status=freeze(m,copy);if(m.tymed)ReleaseStgMedium(&m);
  if(FAILED(status)){formatFailure(needed,status);saved->Release();return nullptr;}needed.tymed=copy.tymed;saved->formats.push_back(needed);saved->media.push_back(copy);
 }
 diagnostic+=L" savedFormats="+std::to_wstring(saved->formats.size());
 return saved;
}
// 在同一个 STA 线程中物化快照。必需格式失败不得借用，其余失败格式记入诊断。
template<typename Getter> static HRESULT clipboardSource(Getter get,ComPtr<IDataObject>& source){
 HRESULT hr{};ULONGLONG start=GetTickCount64();unsigned attempts=0;
 do{attempts++;hr=get(source.GetAddressOf());if(SUCCEEDED(hr))break;source.Reset();if(GetTickCount64()-start>=90)break;Sleep(5);}while(true);
 diagnostic=L"snapshotAttempts="+std::to_wstring(attempts);
 return hr;
}
API void* PaneClipboardSnapshot(){
 diagnostic.clear();ComPtr<IDataObject> source;HRESULT hr=clipboardSource(OleGetClipboard,source);
 if(FAILED(hr)||!source){diagnostic+=L" oleGetHr="+std::to_wstring(hr);return nullptr;}
 return snapshotFrom(source.Get(),true);
}
API int PaneClipboardRestore(void* data){auto saved=static_cast<SavedData*>(data);HRESULT hr=E_FAIL;for(int i=0;i<20;i++){hr=OleSetClipboard(saved);if(SUCCEEDED(hr))break;Sleep(5);}if(SUCCEEDED(hr))hr=OleFlushClipboard();diagnostic=L"restoreHr="+std::to_wstring(hr);saved->Release();return SUCCEEDED(hr);}
API void PaneClipboardRelease(void* data){static_cast<SavedData*>(data)->Release();}
API int PaneClipboardTextAlloc(wchar_t** output){*output=nullptr;if(!OpenClipboard(nullptr))return 0;auto h=GetClipboardData(CF_UNICODETEXT);auto p=h?static_cast<wchar_t*>(GlobalLock(h)):nullptr;bool ok=false;if(p){size_t n=0,max=GlobalSize(h)/sizeof(wchar_t);while(n<max&&p[n])n++;auto memory=static_cast<wchar_t*>(CoTaskMemAlloc((n+1)*sizeof(wchar_t)));if(memory){memcpy(memory,p,n*sizeof(wchar_t));memory[n]=0;*output=memory;ok=true;}GlobalUnlock(h);}CloseClipboard();return ok;}
extern "C" int PaneClipWrite(const wchar_t*,const wchar_t*,const wchar_t*,int);
API int PaneClipboardWrite(const wchar_t* text){return PaneClipWrite(text,nullptr,nullptr,0); }
