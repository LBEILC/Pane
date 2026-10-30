// 直接包含实现以替换 IDataObject；不打开或读取系统剪贴板。
#include "../../src/Pane.Native/Selection.cpp"
#include <iostream>
extern "C" int PaneClipWrite(const wchar_t*,const wchar_t*,const wchar_t*,int){return 0;}
struct FixtureData:SavedData {
 CLIPFORMAT bad{};
 HRESULT STDMETHODCALLTYPE GetData(FORMATETC* f,STGMEDIUM* m) override {
  if(f->cfFormat==bad)return DV_E_TYMED;
  m->tymed=TYMED_HGLOBAL;m->hGlobal=GlobalAlloc(GMEM_MOVEABLE,32);if(!m->hGlobal)return E_OUTOFMEMORY;
  auto memory=GlobalLock(m->hGlobal);memset(memory,0,32);memcpy(memory,L"fixture",16);GlobalUnlock(m->hGlobal);return S_OK;
 }
};
int main(){
 Apartment apartment;int checks=0;
 auto check=[&](bool ok){checks++;if(!ok){std::cerr<<"FAIL assertion="<<checks<<"\n";exit(1);}};
 auto optional=static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"Pane019OptionalFixture"));
 FixtureData source;source.bad=optional;source.formats={{CF_UNICODETEXT,nullptr,DVASPECT_CONTENT,-1,TYMED_HGLOBAL},{optional,nullptr,DVASPECT_CONTENT,-1,TYMED_FILE}};
 diagnostic.clear();auto saved=static_cast<SavedData*>(snapshotFrom(&source,false));check(saved&&saved->formats.size()==1);check(diagnostic.find(L"required=false")!=std::wstring::npos);
 FORMATETC text{CF_UNICODETEXT,nullptr,DVASPECT_CONTENT,-1,TYMED_HGLOBAL};STGMEDIUM result{};check(SUCCEEDED(saved->GetData(&text,&result)));check(!wcscmp(static_cast<wchar_t*>(GlobalLock(result.hGlobal)),L"fixture"));GlobalUnlock(result.hGlobal);ReleaseStgMedium(&result);saved->Release();
 const UINT essentials[]={CF_UNICODETEXT,CF_TEXT,CF_HDROP,CF_DIB,CF_DIBV5,RegisterClipboardFormatW(L"PNG"),RegisterClipboardFormatW(L"HTML Format"),RegisterClipboardFormatW(L"Rich Text Format")};
 for(auto format:essentials){source.bad=static_cast<CLIPFORMAT>(format);source.formats={{source.bad,nullptr,DVASPECT_CONTENT,-1,TYMED_HGLOBAL}};diagnostic.clear();check(snapshotFrom(&source,false)==nullptr);check(diagnostic.find(L"required=true")!=std::wstring::npos);}
 ComPtr<IDataObject> unavailable;int attempts=0;ULONGLONG start=GetTickCount64();HRESULT busy=clipboardSource([&](IDataObject**){attempts++;return CLIPBRD_E_CANT_OPEN;},unavailable);
 check(FAILED(busy)&&attempts>1&&!unavailable);check(GetTickCount64()-start<110);check(diagnostic.find(L"snapshotAttempts=")!=std::wstring::npos);
 std::cout<<"PASS snapshot assertions="<<checks<<"; systemClipboard=0; input=0\n";return 0;
}
