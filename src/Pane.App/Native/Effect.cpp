#include <unknwn.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <windows.graphics.effects.interop.h>
#include <d2d1effects.h>
using namespace ABI::Windows::Graphics::Effects;

// 只描述公开的 D2D 饱和度效果；对象寿命由 C++/WinRT 管理，不依赖 CLR COM。
struct Saturation : winrt::implements<Saturation,
    winrt::Windows::Graphics::Effects::IGraphicsEffect,
    winrt::Windows::Graphics::Effects::IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    winrt::Windows::Graphics::Effects::IGraphicsEffectSource source;
    winrt::hstring name{L"Saturation"};
    float amount=1.6f; bool blur=false;
    explicit Saturation(winrt::Windows::Graphics::Effects::IGraphicsEffectSource const& s) : source(s) {}
    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& value) { name = value; }
    HRESULT __stdcall GetEffectId(GUID* id) noexcept override { *id = (blur ? CLSID_D2D1GaussianBlur : CLSID_D2D1Saturation); return S_OK; }
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR n, UINT* index, GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override {
        *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
        if (!blur && wcscmp(n,L"Saturation")==0) {*index=0;return S_OK;}
        if (blur && wcscmp(n,L"BlurAmount")==0) {*index=0;return S_OK;}
        if (blur && wcscmp(n,L"Optimization")==0) {*index=1;return S_OK;}
        if (blur && wcscmp(n,L"BorderMode")==0) {*index=2;return S_OK;}
        return E_INVALIDARG;
    }
    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override { *count = blur ? 3 : 1; return S_OK; }
    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override {
        if (index >= (blur ? 3u : 1u)) return E_INVALIDARG;
        try {
            // 高斯模糊必须完整描述三个 D2D 属性；不能沿用饱和度的单属性描述。
            auto property=index==0 ? winrt::Windows::Foundation::PropertyValue::CreateSingle(amount)
                : winrt::Windows::Foundation::PropertyValue::CreateUInt32(index==1 ? D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED : D2D1_BORDER_MODE_HARD);
            *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(property));return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall GetSource(UINT index, ABI::Windows::Graphics::Effects::IGraphicsEffectSource** value) noexcept override {
        if (index != 0) return E_INVALIDARG;
        winrt::copy_to_abi(source, *reinterpret_cast<void**>(value)); return S_OK;
    }
    HRESULT __stdcall GetSourceCount(UINT* count) noexcept override { *count = 1; return S_OK; }
};

extern "C" __declspec(dllexport) HRESULT __stdcall CreateSaturation(void* source, float amount, int blur, void** result) noexcept {
    try {
        winrt::Windows::Graphics::Effects::IGraphicsEffectSource s{nullptr};
        winrt::copy_from_abi(s, source);
        auto impl = winrt::make_self<Saturation>(s); impl->amount=amount; impl->blur=blur!=0; impl->name=blur ? L"ExtraBlur" : L"Saturation";
        auto effect=impl.as<winrt::Windows::Graphics::Effects::IGraphicsEffect>();
        *result = winrt::detach_abi(effect); return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
