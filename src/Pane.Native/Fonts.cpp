#include <windows.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <string>
#include <new>
using Microsoft::WRL::ComPtr;

static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
static std::wstring Name(IDWriteLocalizedStrings* names, const wchar_t* locale, const std::wstring& fallback = L"") {
    UINT32 index = 0, length = 0; BOOL exists = FALSE;
    Check(names->FindLocaleName(locale, &index, &exists));
    if (!exists) { if (!fallback.empty()) return fallback; index = 0; }
    Check(names->GetStringLength(index, &length));
    std::wstring text(length + 1, L'\0');
    Check(names->GetString(index, text.data(), length + 1)); text.resize(length); return text;
}
static bool Covers(IDWriteFont* font, const wchar_t* text) {
    for (; *text; ++text) { BOOL exists = FALSE; Check(font->HasCharacter(*text, &exists)); if (!exists) return false; }
    return true;
}
// 只把字族元数据交给托管层；所有 DirectWrite 对象在本次枚举结束时释放。
extern "C" __declspec(dllexport) HRESULT __cdecl PaneFontFamilies(wchar_t** result) noexcept {
    if (!result) return E_POINTER; *result = nullptr;
    try {
        ComPtr<IDWriteFactory3> factory;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory3), reinterpret_cast<IUnknown**>(factory.GetAddressOf())));
        ComPtr<IDWriteFontCollection1> collection;
        Check(factory->GetSystemFontCollection(FALSE, &collection, TRUE));
        std::wstring text;
        for (UINT32 i = 0; i < collection->GetFontFamilyCount(); ++i) {
            ComPtr<IDWriteFontFamily> family; Check(collection->GetFontFamily(i, &family));
            ComPtr<IDWriteFont> font; Check(family->GetFirstMatchingFont(DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, &font));
            if (font->IsSymbolFont()) continue;
            bool chinese = Covers(font.Get(), L"的一是在不了有和人这");
            // 按实际文字覆盖排除符号字体，同时保留中文专用字族。
            if (!chinese && !Covers(font.Get(), L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")) continue;
            ComPtr<IDWriteLocalizedStrings> names; Check(family->GetFamilyNames(&names));
            auto name = Name(names.Get(), L"en-us");
            text += name + L"\t" + Name(names.Get(), L"zh-cn", name) + (chinese ? L"\t1\n" : L"\t0\n");
        }
        auto bytes = (text.size() + 1) * sizeof(wchar_t);
        *result = static_cast<wchar_t*>(CoTaskMemAlloc(bytes)); if (!*result) return E_OUTOFMEMORY;
        memcpy(*result, text.c_str(), bytes); return S_OK;
    } catch (HRESULT hr) { return hr; } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
