#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <thread>
#include <atomic>
#include <future>
#include <vector>
#include <algorithm>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace {
using Callback=void(__cdecl*)(unsigned);
struct Listener {std::thread thread;HWND window=nullptr;Callback callback=nullptr;bool removed=false;std::atomic<int> count{0};};
LRESULT CALLBACK Proc(HWND w,UINT m,WPARAM a,LPARAM b){
 auto p=reinterpret_cast<Listener*>(GetWindowLongPtrW(w,GWLP_USERDATA));
 if(m==WM_NCCREATE){p=static_cast<Listener*>(reinterpret_cast<CREATESTRUCTW*>(b)->lpCreateParams);SetWindowLongPtrW(w,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(p));}
 if(m==WM_CLIPBOARDUPDATE&&p){p->count++;p->callback(GetClipboardSequenceNumber());return 0;}
 if(m==WM_CLOSE){if(p)p->removed=RemoveClipboardFormatListener(w)!=0;DestroyWindow(w);PostQuitMessage(0);return 0;}
 return DefWindowProcW(w,m,a,b);
}
struct Apartment {HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);~Apartment(){if(SUCCEEDED(hr))CoUninitialize();}};
ComPtr<IWICImagingFactory> Factory(){ComPtr<IWICImagingFactory> f;CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&f));return f;}
HRESULT Encode(IWICImagingFactory* f,IWICBitmapSource* source,IStream* stream){
 ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;HRESULT hr=f->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder);
 if(SUCCEEDED(hr))hr=encoder->Initialize(stream,WICBitmapEncoderNoCache);if(SUCCEEDED(hr))hr=encoder->CreateNewFrame(&frame,nullptr);
 if(SUCCEEDED(hr))hr=frame->Initialize(nullptr);if(SUCCEEDED(hr))hr=frame->WriteSource(source,nullptr);if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=encoder->Commit();return hr;
}
HRESULT Save(IWICImagingFactory* f,IWICBitmapSource* source,const wchar_t* path){ComPtr<IWICStream> stream;HRESULT hr=f->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(path,GENERIC_WRITE);return SUCCEEDED(hr)?Encode(f,source,stream.Get()):hr;}
HGLOBAL Memory(const void* data,size_t size){auto h=GlobalAlloc(GMEM_MOVEABLE,size);if(!h)return nullptr;auto p=GlobalLock(h);if(!p){GlobalFree(h);return nullptr;}memcpy(p,data,size);GlobalUnlock(h);return h;}
bool Put(UINT format,HGLOBAL& memory){if(!SetClipboardData(format,memory))return false;memory=nullptr;return true;}
}
API void* PaneClipListen(Callback callback){
 auto p=new Listener();p->callback=callback;std::promise<bool> ready;auto future=ready.get_future();
 p->thread=std::thread([p,&ready]{Apartment apartment;WNDCLASSW wc{};wc.lpfnWndProc=Proc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"PaneClipboard014";RegisterClassW(&wc);
 p->window=CreateWindowW(wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,p);
 bool ok=p->window&&AddClipboardFormatListener(p->window);ready.set_value(ok);
 if(ok){MSG msg;while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}}else if(p->window)DestroyWindow(p->window);
 UnregisterClassW(wc.lpszClassName,wc.hInstance);
 });
 if(!future.get()){p->thread.join();delete p;return nullptr;}return p;
}
API int PaneClipStop(void* state,int* count){auto p=static_cast<Listener*>(state);PostMessageW(p->window,WM_CLOSE,0,0);p->thread.join();*count=p->count;bool ok=p->removed;delete p;return ok;}
// 调用方持有 OpenClipboard；不改变剪贴板。DIB 用 BMP 文件头交 WIC 解码。
API int PaneClipImageAlloc(void** output,int* length){
 *output=nullptr;*length=0;auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapSource> source;ComPtr<IWICStream> input;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;
 UINT format=RegisterClipboardFormatW(L"PNG");if(!IsClipboardFormatAvailable(format))format=IsClipboardFormatAvailable(CF_DIBV5)?CF_DIBV5:CF_DIB;
 auto handle=GetClipboardData(format);if(!handle)return 0;size_t size=GlobalSize(handle);auto p=static_cast<BYTE*>(GlobalLock(handle));if(!p||size>MAXDWORD-sizeof(BITMAPFILEHEADER)){if(p)GlobalUnlock(handle);return 0;}
 std::vector<BYTE> bytes;
 if(format==CF_DIB||format==CF_DIBV5){
  if(size<sizeof(BITMAPINFOHEADER)){GlobalUnlock(handle);return 0;}auto header=reinterpret_cast<BITMAPINFOHEADER*>(p);
  if(header->biSize<sizeof(BITMAPINFOHEADER)||header->biSize>size){GlobalUnlock(handle);return 0;}
  DWORD colors=header->biClrUsed?header->biClrUsed:(header->biBitCount<=8?1u<<header->biBitCount:0);
  unsigned long long offset=sizeof(BITMAPFILEHEADER)+header->biSize+static_cast<unsigned long long>(colors)*4;
  if(header->biSize==40&&(header->biCompression==BI_BITFIELDS||header->biCompression==6))offset+=header->biCompression==6?16:12;
  if(offset>size+sizeof(BITMAPFILEHEADER)){GlobalUnlock(handle);return 0;}
  BITMAPFILEHEADER file{};file.bfType=0x4d42;file.bfSize=static_cast<DWORD>(size+sizeof(file));file.bfOffBits=static_cast<DWORD>(offset);
  bytes.resize(file.bfSize);memcpy(bytes.data(),&file,sizeof(file));memcpy(bytes.data()+sizeof(file),p,size);
 }else bytes.assign(p,p+size);GlobalUnlock(handle);
 HRESULT hr=f->CreateStream(&input);if(SUCCEEDED(hr))hr=input->InitializeFromMemory(bytes.data(),static_cast<DWORD>(bytes.size()));
 if(SUCCEEDED(hr))hr=f->CreateDecoderFromStream(input.Get(),nullptr,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
 ComPtr<IStream> encoded;if(SUCCEEDED(hr))hr=CreateStreamOnHGlobal(nullptr,TRUE,&encoded);if(SUCCEEDED(hr))hr=Encode(f.Get(),frame.Get(),encoded.Get());
 STATSTG stats{};if(SUCCEEDED(hr))hr=encoded->Stat(&stats,STATFLAG_NONAME);if(FAILED(hr)||stats.cbSize.QuadPart>INT_MAX)return 0;
 *length=static_cast<int>(stats.cbSize.QuadPart);*output=CoTaskMemAlloc(*length);if(!*output)return 0;LARGE_INTEGER zero{};encoded->Seek(zero,STREAM_SEEK_SET,nullptr);ULONG read=0;hr=encoded->Read(*output,*length,&read);
 if(FAILED(hr)||read!=static_cast<ULONG>(*length)){CoTaskMemFree(*output);*output=nullptr;return 0;}return 1;
}
API int PaneClipSaveImage(const wchar_t* input,const wchar_t* full,const wchar_t* thumb,int* width,int* height){
 Apartment apartment;auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICBitmapScaler> scale;
 HRESULT hr=f->CreateDecoderFromFilename(input,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
 UINT w=0,h=0;if(SUCCEEDED(hr))hr=frame->GetSize(&w,&h);if(!w||!h||w>INT_MAX||h>INT_MAX)return 0;*width=static_cast<int>(w);*height=static_cast<int>(h);
 if(SUCCEEDED(hr)&&full)hr=Save(f.Get(),frame.Get(),full);if(SUCCEEDED(hr))hr=f->CreateBitmapScaler(&scale);double k=std::min(1.0,640.0/std::max(w,h));
 if(SUCCEEDED(hr))hr=scale->Initialize(frame.Get(),std::max(1u,static_cast<UINT>(w*k)),std::max(1u,static_cast<UINT>(h*k)),WICBitmapInterpolationModeFant);
 if(SUCCEEDED(hr))hr=Save(f.Get(),scale.Get(),thumb);return SUCCEEDED(hr);
}
API int PaneClipWrite(const wchar_t* text,const wchar_t* image,const wchar_t* files,int fileChars){
 Apartment apartment;HGLOBAL data=nullptr,png=nullptr;UINT format=CF_UNICODETEXT;
 if(image){
  auto f=Factory();if(!f)return 0;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> convert;
  HRESULT hr=f->CreateDecoderFromFilename(image,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder);if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
  UINT w=0,h=0;if(SUCCEEDED(hr))hr=frame->GetSize(&w,&h);if(FAILED(hr)||!w||!h||static_cast<unsigned long long>(w)*h*4>MAXDWORD-sizeof(BITMAPV5HEADER))return 0;
  if(SUCCEEDED(hr))hr=f->CreateFormatConverter(&convert);if(SUCCEEDED(hr))hr=convert->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom);
  std::vector<BYTE> bytes(sizeof(BITMAPV5HEADER)+static_cast<size_t>(w)*h*4);auto header=reinterpret_cast<BITMAPV5HEADER*>(bytes.data());header->bV5Size=sizeof(*header);header->bV5Width=static_cast<LONG>(w);header->bV5Height=-static_cast<LONG>(h);header->bV5Planes=1;header->bV5BitCount=32;header->bV5Compression=BI_BITFIELDS;header->bV5RedMask=0xff0000;header->bV5GreenMask=0xff00;header->bV5BlueMask=0xff;header->bV5AlphaMask=0xff000000;header->bV5CSType=LCS_sRGB;
  if(SUCCEEDED(hr))hr=convert->CopyPixels(nullptr,w*4,w*h*4,bytes.data()+sizeof(*header));if(FAILED(hr))return 0;data=Memory(bytes.data(),bytes.size());format=CF_DIBV5;
  ComPtr<IStream> stream;if(SUCCEEDED(CreateStreamOnHGlobal(nullptr,TRUE,&stream))&&SUCCEEDED(Encode(f.Get(),frame.Get(),stream.Get()))){HGLOBAL mem=nullptr;GetHGlobalFromStream(stream.Get(),&mem);auto ptr=GlobalLock(mem);if(ptr){png=Memory(ptr,GlobalSize(mem));GlobalUnlock(mem);}}
 }else if(files&&fileChars>1){format=CF_HDROP;std::vector<BYTE> bytes(sizeof(DROPFILES)+static_cast<size_t>(fileChars)*2);auto drop=reinterpret_cast<DROPFILES*>(bytes.data());drop->pFiles=sizeof(DROPFILES);drop->fWide=TRUE;memcpy(bytes.data()+sizeof(DROPFILES),files,static_cast<size_t>(fileChars)*2);data=Memory(bytes.data(),bytes.size());}
 else if(text)data=Memory(text,(wcslen(text)+1)*2);if(!data){if(png)GlobalFree(png);return 0;}
 DWORD one=1;HGLOBAL marker=Memory(&one,sizeof(one));HWND owner=CreateWindowW(L"STATIC",L"",0,0,0,0,0,HWND_MESSAGE,nullptr,GetModuleHandleW(nullptr),nullptr);
 bool opened=false;if(owner&&marker)for(int i=0;i<5&&!opened;i++){opened=OpenClipboard(owner)!=0;if(!opened)Sleep(15);}
 bool ok=opened&&EmptyClipboard()&&Put(RegisterClipboardFormatW(L"Pane.OwnClipboard"),marker)&&Put(format,data);
 if(ok&&png)ok=Put(RegisterClipboardFormatW(L"PNG"),png);if(opened)CloseClipboard();if(owner)DestroyWindow(owner);if(marker)GlobalFree(marker);if(data)GlobalFree(data);if(png)GlobalFree(png);return ok;
}
API int PaneClipSaveDialog(const wchar_t* source,const wchar_t* name){
 Apartment apartment;ComPtr<IFileSaveDialog> dialog;HRESULT hr=CoCreateInstance(CLSID_FileSaveDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog));
 COMDLG_FILTERSPEC filter{L"PNG",L"*.png"};if(SUCCEEDED(hr))hr=dialog->SetFileTypes(1,&filter);if(SUCCEEDED(hr))hr=dialog->SetDefaultExtension(L"png");if(SUCCEEDED(hr))hr=dialog->SetFileName(name);
 if(SUCCEEDED(hr))hr=dialog->Show(nullptr);if(hr==HRESULT_FROM_WIN32(ERROR_CANCELLED))return 2;
 ComPtr<IShellItem> result;if(SUCCEEDED(hr))hr=dialog->GetResult(&result);PWSTR path=nullptr;if(SUCCEEDED(hr))hr=result->GetDisplayName(SIGDN_FILESYSPATH,&path);
 if(SUCCEEDED(hr)&&!CopyFileW(source,path,FALSE))hr=HRESULT_FROM_WIN32(GetLastError());CoTaskMemFree(path);return SUCCEEDED(hr)?1:0;
}
API int PaneClipSourceIcon(const wchar_t* path,const wchar_t* output){
 Apartment apartment;auto f=Factory();if(!f)return 0;ComPtr<IShellItemImageFactory> item;
 HRESULT hr=SHCreateItemFromParsingName(path,nullptr,IID_PPV_ARGS(&item));HBITMAP handle=nullptr;
 if(SUCCEEDED(hr))hr=item->GetImage({32,32},SIIGBF_ICONONLY,&handle);ComPtr<IWICBitmap> bitmap;
 if(SUCCEEDED(hr))hr=f->CreateBitmapFromHBITMAP(handle,nullptr,WICBitmapUsePremultipliedAlpha,&bitmap);
 if(SUCCEEDED(hr))hr=Save(f.Get(),bitmap.Get(),output);if(handle)DeleteObject(handle);return SUCCEEDED(hr);
}
