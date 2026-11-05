// 替换剪贴板读取边界，直接运行产品解码与编码实现；夹具不进入系统剪贴板。
#define NOMINMAX
#include <windows.h>
static UINT fixtureFormat;
static HGLOBAL fixtureMemory;
static UINT WINAPI FixtureRegister(LPCWSTR){return 0xc001;}
static BOOL WINAPI FixtureAvailable(UINT format){return format==fixtureFormat;}
static HANDLE WINAPI FixtureGet(UINT format){return format==fixtureFormat?fixtureMemory:nullptr;}
#define RegisterClipboardFormatW FixtureRegister
#define IsClipboardFormatAvailable FixtureAvailable
#define GetClipboardData FixtureGet
#include "../../src/Pane.Native/Clipboard.cpp"
#undef RegisterClipboardFormatW
#undef IsClipboardFormatAvailable
#undef GetClipboardData
#include <iostream>
#include <chrono>
#include <string>
#include <fstream>
static int checks=0;
static void Check(bool ok,const char* name){++checks;if(!ok){std::cerr<<"FAIL "<<name<<"\n";exit(1);}}
static void Require(HRESULT hr){if(FAILED(hr)){std::cerr<<"HRESULT="<<std::hex<<hr<<"\n";exit(1);}}
static std::vector<BYTE> Dib(bool v5,bool rgb24,const std::vector<BYTE>& pixels){
 const size_t headerSize=v5?sizeof(BITMAPV5HEADER):sizeof(BITMAPINFOHEADER);
 std::vector<BYTE> data(headerSize+8);auto h=reinterpret_cast<BITMAPINFOHEADER*>(data.data());
 h->biSize=static_cast<DWORD>(headerSize);h->biWidth=2;h->biHeight=-1;h->biPlanes=1;h->biBitCount=rgb24?24:32;h->biCompression=v5?BI_BITFIELDS:BI_RGB;h->biSizeImage=8;
 if(v5){auto v=reinterpret_cast<BITMAPV5HEADER*>(h);v->bV5RedMask=0xff0000;v->bV5GreenMask=0xff00;v->bV5BlueMask=0xff;v->bV5AlphaMask=0xff000000;v->bV5CSType=LCS_sRGB;}
 if(rgb24){memcpy(data.data()+headerSize,pixels.data(),3);memcpy(data.data()+headerSize+3,pixels.data()+4,3);}else memcpy(data.data()+headerSize,pixels.data(),8);
 return data;
}
static std::vector<BYTE> StreamBytes(IStream* stream){
 STATSTG stats{};Require(stream->Stat(&stats,STATFLAG_NONAME));std::vector<BYTE> bytes(static_cast<size_t>(stats.cbSize.QuadPart));LARGE_INTEGER zero{};Require(stream->Seek(zero,STREAM_SEEK_SET,nullptr));Require(stream->Read(bytes.data(),static_cast<ULONG>(bytes.size()),nullptr));return bytes;
}
static std::vector<BYTE> Png(IWICImagingFactory* f,const std::vector<BYTE>& pixels){
 ComPtr<IWICBitmap> bitmap;Require(f->CreateBitmapFromMemory(2,1,GUID_WICPixelFormat32bppBGRA,8,8,const_cast<BYTE*>(pixels.data()),&bitmap));ComPtr<IStream> stream;Require(CreateStreamOnHGlobal(nullptr,TRUE,&stream));Require(Encode(f,bitmap.Get(),stream.Get()));return StreamBytes(stream.Get());
}
static std::vector<BYTE> Pixels(IWICImagingFactory* f,std::vector<BYTE>& bytes){
 ComPtr<IWICStream> stream;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> convert;
 Require(f->CreateStream(&stream));Require(stream->InitializeFromMemory(bytes.data(),static_cast<DWORD>(bytes.size())));Require(f->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnLoad,&decoder));Require(decoder->GetFrame(0,&frame));Require(f->CreateFormatConverter(&convert));Require(convert->Initialize(frame.Get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));std::vector<BYTE> pixels(8);Require(convert->CopyPixels(nullptr,8,8,pixels.data()));return pixels;
}
static std::vector<BYTE> Capture(UINT format,const std::vector<BYTE>& data){
 fixtureFormat=format;fixtureMemory=Memory(data.data(),data.size());void* output=nullptr;int length=0;
 int ok=PaneClipImageAlloc(&output,&length);GlobalFree(fixtureMemory);fixtureMemory=nullptr;Check(ok==1&&output&&length>0,"image alloc");
 std::vector<BYTE> result(static_cast<BYTE*>(output),static_cast<BYTE*>(output)+length);CoTaskMemFree(output);return result;
}
static void SaveFixture(IWICImagingFactory* f,std::vector<BYTE> data,bool bmp,const std::vector<BYTE>& expected){
 // 缩略图沿用现有 WIC 缩放行为（半透明颜色存在预乘舍入），基准使用期望像素。
 // 文件入口必须接收路径：仅把内存合成数据写入测试输出目录。
 if(bmp){BITMAPFILEHEADER h{};h.bfType=0x4d42;h.bfSize=static_cast<DWORD>(sizeof(h)+data.size());h.bfOffBits=sizeof(h)+reinterpret_cast<BITMAPINFOHEADER*>(data.data())->biSize;std::vector<BYTE> bytes(h.bfSize);memcpy(bytes.data(),&h,sizeof(h));memcpy(bytes.data()+sizeof(h),data.data(),data.size());data=std::move(bytes);}
 {std::ofstream file("fixture-image",std::ios::binary);file.write(reinterpret_cast<const char*>(data.data()),static_cast<std::streamsize>(data.size()));}
 ComPtr<IWICBitmap> bitmap;ComPtr<IWICBitmapScaler> scaler;ComPtr<IStream> reference;
 ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;
 Require(f->CreateBitmapFromMemory(2,1,GUID_WICPixelFormat32bppBGRA,8,8,const_cast<BYTE*>(expected.data()),&bitmap));
 Require(f->CreateDecoderFromFilename(L"fixture-image",nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder));Require(decoder->GetFrame(0,&frame));
 Require(f->CreateBitmapScaler(&scaler));Require(scaler->Initialize(expected[3]==0?static_cast<IWICBitmapSource*>(frame.Get()):bitmap.Get(),2,1,WICBitmapInterpolationModeFant));
 Require(CreateStreamOnHGlobal(nullptr,TRUE,&reference));Require(Encode(f,scaler.Get(),reference.Get()));auto referenceBytes=StreamBytes(reference.Get());auto thumbnail=Pixels(f,referenceBytes);
 for(bool full:{true,false}){
  int w=0,h=0;Check(PaneClipSaveImage(L"fixture-image",full?L"fixture-full.png":nullptr,L"fixture-thumb.png",&w,&h)==1,"save image entry");Check(w==2&&h==1,"saved dimensions");
  for(const char* path:{"fixture-full.png","fixture-thumb.png"}){bool isFull=std::string(path)=="fixture-full.png";if(!full&&isFull)continue;std::ifstream file(path,std::ios::binary);std::vector<BYTE> bytes((std::istreambuf_iterator<char>(file)),{});auto actual=Pixels(f,bytes);auto target=isFull?expected:thumbnail;if(actual!=target){std::cerr<<path<<" actual:";for(auto v:actual)std::cerr<<" "<<static_cast<int>(v);std::cerr<<" expected:";for(auto v:target)std::cerr<<" "<<static_cast<int>(v);std::cerr<<"\n";}Check(actual==target,"saved full/thumbnail pixels");}
 }
 frame.Reset();decoder.Reset();scaler.Reset();DeleteFileW(L"fixture-image");DeleteFileW(L"fixture-full.png");DeleteFileW(L"fixture-thumb.png");
}
static void Benchmark(){
 const size_t size=3840ull*2160*4;std::vector<BYTE> pixels(size,75);std::vector<double> times;
 for(int mode=0;mode<3;mode++){
  times.clear();
  for(int iteration=0;iteration<105;iteration++){
   for(size_t i=3;i<size;i+=4)pixels[i]=0;
   if(mode==1)pixels[size-1]=128;if(mode==2)pixels[3]=255;
   auto start=std::chrono::steady_clock::now();bool repaired=RepairEmptyAlpha(pixels.data(),pixels.size());auto end=std::chrono::steady_clock::now();
   if(repaired!=(mode==0)){std::cerr<<"benchmark result invalid\n";exit(1);}
   if(iteration>=5)times.push_back(std::chrono::duration<double,std::milli>(end-start).count());
  }
  std::sort(times.begin(),times.end());std::cout<<"4K "<<(mode==0?"scan+repair":mode==1?"scan-last-nonzero":"scan-first-nonzero")<<" medianMs="<<times[50]<<" p95Ms="<<times[94]<<" samples=100\n";
 }
}
int main(int argc,char** argv){
 Apartment apartment;auto f=Factory();if(!f)return 1;bool baseline=argc>1&&std::string(argv[1])=="--baseline";
 const std::vector<BYTE> zero={25,75,150,0,200,100,50,0},opaque={25,75,150,255,200,100,50,255},mixed={25,75,150,0,200,100,50,128};
 for(bool v5:{false,true}){auto png=Capture(v5?CF_DIBV5:CF_DIB,Dib(v5,false,zero));auto actual=Pixels(f.Get(),png);std::cout<<(v5?"DIBV5":"DIB")<<" alpha="<<static_cast<int>(actual[3])<<","<<static_cast<int>(actual[7])<<"\n";if(!baseline)Check(actual==opaque,"zero alpha becomes opaque, colors preserved");}
 if(baseline){std::cout<<"baseline;systemClipboard=0\n";return 0;}
 for(bool pngInput:{false,true}){auto png=Capture(pngInput?0xc001:CF_DIBV5,pngInput?Png(f.Get(),mixed):Dib(true,false,mixed));Check(Pixels(f.Get(),png)==mixed,"mixed alpha and colors preserved");}
 auto png=Capture(0xc001,Png(f.Get(),zero));Check(Pixels(f.Get(),png)==opaque,"transparent PNG becomes opaque");
 png=Capture(CF_DIB,Dib(false,true,opaque));Check(Pixels(f.Get(),png)==opaque,"24 bit DIB unchanged");
 SaveFixture(f.Get(),Dib(false,false,zero),true,opaque);
 SaveFixture(f.Get(),Dib(true,false,zero),true,opaque);
 SaveFixture(f.Get(),Dib(true,false,mixed),true,mixed);
 SaveFixture(f.Get(),Png(f.Get(),zero),false,opaque);
 SaveFixture(f.Get(),Png(f.Get(),mixed),false,mixed);
 Benchmark();
 std::cout<<"PASS image assertions="<<checks<<";systemClipboard=0\n";
}
