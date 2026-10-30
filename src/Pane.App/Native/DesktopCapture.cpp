#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <winrt/base.h>
using Microsoft::WRL::ComPtr;

// 只从 DXGI 桌面纹理读取，不调用 GDI，不叠加单独的硬件光标。
extern "C" __declspec(dllexport) HRESULT __stdcall CaptureDesktop(
    int x, int y, int width, int height, BYTE* pixels, int length) noexcept {
    try {
        if (!pixels || width <= 0 || height <= 0 || (long long)width * height * 4 != length) return E_INVALIDARG;
        auto monitor = MonitorFromPoint(POINT{x,y}, MONITOR_DEFAULTTONULL);
        ComPtr<IDXGIFactory1> factory;
        winrt::check_hresult(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<IDXGIOutput> output;
        DXGI_OUTPUT_DESC desc{};
        bool found=false;
        for (UINT a=0; !found; ++a) {
            auto hr=factory->EnumAdapters1(a,adapter.ReleaseAndGetAddressOf());
            if(hr==DXGI_ERROR_NOT_FOUND) break;
            winrt::check_hresult(hr);
            for(UINT o=0;;++o) {
                hr=adapter->EnumOutputs(o,output.ReleaseAndGetAddressOf());
                if(hr==DXGI_ERROR_NOT_FOUND) break;
                winrt::check_hresult(hr);winrt::check_hresult(output->GetDesc(&desc));
                if(desc.AttachedToDesktop && desc.Monitor==monitor){found=true;break;}
            }
        }
        if(!found) return DXGI_ERROR_NOT_FOUND;
        const auto r=desc.DesktopCoordinates;
        if(x<r.left || y<r.top || (long long)x+width>r.right || (long long)y+height>r.bottom) return E_INVALIDARG;
        // 本验收夹具限定未旋转的单屏；旋转屏明确报错，避免交付错位图。
        if(desc.Rotation!=DXGI_MODE_ROTATION_IDENTITY) return DXGI_ERROR_UNSUPPORTED;
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        winrt::check_hresult(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        ComPtr<IDXGIOutput1> output1;winrt::check_hresult(output.As(&output1));
        ComPtr<IDXGIOutputDuplication> duplication;winrt::check_hresult(output1->DuplicateOutput(device.Get(),&duplication));
        DXGI_OUTDUPL_FRAME_INFO frame{};ComPtr<IDXGIResource> resource;
        winrt::check_hresult(duplication->AcquireNextFrame(3000,&frame,&resource));
        struct FrameGuard { IDXGIOutputDuplication* value; ~FrameGuard(){value->ReleaseFrame();} } guard{duplication.Get()};
        ComPtr<ID3D11Texture2D> source;winrt::check_hresult(resource.As(&source));
        D3D11_TEXTURE2D_DESC texture{};source->GetDesc(&texture);
        texture.Width=width;texture.Height=height;texture.Usage=D3D11_USAGE_STAGING;
        texture.BindFlags=0;texture.CPUAccessFlags=D3D11_CPU_ACCESS_READ;texture.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;winrt::check_hresult(device->CreateTexture2D(&texture,nullptr,&staging));
        D3D11_BOX box{(UINT)(x-r.left),(UINT)(y-r.top),0,(UINT)(x-r.left+width),(UINT)(y-r.top+height),1};
        context->CopySubresourceRegion(staging.Get(),0,0,0,0,source.Get(),0,&box);
        D3D11_MAPPED_SUBRESOURCE mapped{};winrt::check_hresult(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        for(int row=0;row<height;++row){
            auto dest=pixels+(size_t)row*width*4;
            memcpy(dest,(BYTE*)mapped.pData+(size_t)row*mapped.RowPitch,(size_t)width*4);
            for(int col=0;col<width;++col)dest[col*4+3]=255;
        }
        context->Unmap(staging.Get(),0);return S_OK;
    } catch(...) { return winrt::to_hresult(); }
}
