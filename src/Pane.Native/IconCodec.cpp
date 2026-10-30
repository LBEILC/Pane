#define NOMINMAX
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <vector>
#include <string>
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
namespace { struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED); ~Apartment(){if(SUCCEEDED(hr))CoUninitialize();} }; }
namespace {
#pragma pack(push,2)
struct GroupEntry {BYTE width,height,colors,reserved;WORD planes,bits;DWORD bytes;WORD id;};
#pragma pack(pop)
struct GroupChoice {int wanted,ordinal=0;std::wstring name;WORD id=0;bool numeric=false;};
BOOL CALLBACK ChooseGroup(HMODULE,LPCWSTR,LPWSTR name,LONG_PTR data){
 auto& c=*reinterpret_cast<GroupChoice*>(data);bool match=c.wanted<0?(IS_INTRESOURCE(name)&&LOWORD(name)==-c.wanted):c.ordinal==c.wanted;c.ordinal++;
 if(!match)return TRUE;c.numeric=IS_INTRESOURCE(name);if(c.numeric)c.id=LOWORD(name);else c.name=name;return FALSE;
}
}
// 只映射资源数据，不加载或执行目标程序代码。输出保留资源的真实像素尺寸。
API int PaneLauncherResourceIcon(const wchar_t* path,int index,const wchar_t* output,int* width,int* height){
 Apartment apartment;ComPtr<IWICImagingFactory> factory;HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));if(FAILED(hr))return 0;
 ComPtr<IWICBitmapSource> source;std::wstring file(path);HICON icon=nullptr;
 {
  ComPtr<IWICBitmapDecoder> decoder;if(SUCCEEDED(factory->CreateDecoderFromFilename(path,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder))){UINT count=0,best=0;decoder->GetFrameCount(&count);
   for(UINT i=0;i<count;i++){ComPtr<IWICBitmapFrameDecode> frame;UINT w=0,h=0;if(SUCCEEDED(decoder->GetFrame(i,&frame))&&SUCCEEDED(frame->GetSize(&w,&h))&&w*h>best){best=w*h;source=frame;}}
  }
 }if(!source&&file.size()>3&&file[1]==L':'){
  HMODULE module=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_AS_DATAFILE|LOAD_LIBRARY_AS_IMAGE_RESOURCE);
  if(module){GroupChoice choice{index};EnumResourceNamesW(module,MAKEINTRESOURCEW(14),ChooseGroup,reinterpret_cast<LONG_PTR>(&choice));
   if(choice.numeric||!choice.name.empty()){auto group=FindResourceW(module,choice.numeric?MAKEINTRESOURCEW(choice.id):choice.name.c_str(),MAKEINTRESOURCEW(14));DWORD size=group?SizeofResource(module,group):0;
    auto bytes=group?static_cast<const BYTE*>(LockResource(LoadResource(module,group))):nullptr;
    if(bytes&&size>=6){WORD count=0;memcpy(&count,bytes+4,2);if(count<=(size-6)/sizeof(GroupEntry)){auto entries=reinterpret_cast<const GroupEntry*>(bytes+6);const GroupEntry* best=nullptr;unsigned area=0;
     for(WORD i=0;i<count;i++){unsigned n=(entries[i].width?entries[i].width:256u)*(entries[i].height?entries[i].height:256u);if(n>area||(n==area&&best&&entries[i].bits>best->bits)){area=n;best=&entries[i];}}
     if(best){auto resource=FindResourceW(module,MAKEINTRESOURCEW(best->id),MAKEINTRESOURCEW(3));if(resource){auto data=static_cast<BYTE*>(LockResource(LoadResource(module,resource)));icon=CreateIconFromResourceEx(data,SizeofResource(module,resource),TRUE,0x30000,best->width?best->width:256,best->height?best->height:256,LR_DEFAULTCOLOR);}}
    }}
   }FreeLibrary(module);
  }
  if(icon){ComPtr<IWICBitmap> bitmap;if(SUCCEEDED(factory->CreateBitmapFromHICON(icon,&bitmap)))source=bitmap;DestroyIcon(icon);}
 }
 if(!source)return 0;UINT w=0,h=0;source->GetSize(&w,&h);*width=static_cast<int>(w);*height=static_cast<int>(h);
 ComPtr<IWICStream> stream;ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;
 hr=factory->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(output,GENERIC_WRITE);
 if(SUCCEEDED(hr))hr=factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder);if(SUCCEEDED(hr))hr=encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache);
 if(SUCCEEDED(hr))hr=encoder->CreateNewFrame(&frame,nullptr);if(SUCCEEDED(hr))hr=frame->Initialize(nullptr);if(SUCCEEDED(hr))hr=frame->WriteSource(source.Get(),nullptr);
 if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=encoder->Commit();return SUCCEEDED(hr)?1:0;
}
// 统一解码为居中的 256px RGBA；只在索引辅助进程调用。
API int PaneIconDecode(const wchar_t* path,BYTE* pixels){
 Apartment apartment;ComPtr<IWICImagingFactory> factory;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICBitmapScaler> scaler;ComPtr<IWICFormatConverter> convert;
 HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));
 if(SUCCEEDED(hr))hr=factory->CreateDecoderFromFilename(path,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder);
 if(SUCCEEDED(hr))hr=decoder->GetFrame(0,&frame);
 UINT w=0,h=0;if(SUCCEEDED(hr))hr=frame->GetSize(&w,&h);if(!w||!h)return 0;
 double k=256.0/std::max(w,h);UINT width=std::max(1u,static_cast<UINT>(w*k+.5)),height=std::max(1u,static_cast<UINT>(h*k+.5));
 if(SUCCEEDED(hr))hr=factory->CreateBitmapScaler(&scaler);
 if(SUCCEEDED(hr))hr=scaler->Initialize(frame.Get(),width,height,std::max(w,h)<64?WICBitmapInterpolationModeHighQualityCubic:WICBitmapInterpolationModeFant);
 if(SUCCEEDED(hr))hr=factory->CreateFormatConverter(&convert);
 if(SUCCEEDED(hr))hr=convert->Initialize(scaler.Get(),GUID_WICPixelFormat32bppRGBA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom);
 ZeroMemory(pixels,256*256*4);UINT offset=((256-height)/2*256+(256-width)/2)*4;
 if(SUCCEEDED(hr))hr=convert->CopyPixels(nullptr,256*4,256*256*4-offset,pixels+offset);
 return SUCCEEDED(hr)?1:0;
}
API int PaneIconEncode(const wchar_t* path,const BYTE* pixels,int size){
 Apartment apartment;ComPtr<IWICImagingFactory> factory;ComPtr<IWICStream> stream;ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IWICBitmap> bitmap;
 HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));
 if(SUCCEEDED(hr))hr=factory->CreateStream(&stream);if(SUCCEEDED(hr))hr=stream->InitializeFromFilename(path,GENERIC_WRITE);
 if(SUCCEEDED(hr))hr=factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder);if(SUCCEEDED(hr))hr=encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache);
 if(SUCCEEDED(hr))hr=encoder->CreateNewFrame(&frame,nullptr);if(SUCCEEDED(hr))hr=frame->Initialize(nullptr);
 if(SUCCEEDED(hr))hr=factory->CreateBitmapFromMemory(size,size,GUID_WICPixelFormat32bppRGBA,size*4,size*size*4,const_cast<BYTE*>(pixels),&bitmap);
 if(SUCCEEDED(hr))hr=frame->WriteSource(bitmap.Get(),nullptr);
 if(SUCCEEDED(hr))hr=frame->Commit();if(SUCCEEDED(hr))hr=encoder->Commit();return SUCCEEDED(hr)?1:0;
}
