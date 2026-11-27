// Zonex 玻璃背景宿主：自持 Windows.UI.Composition 合成层，用 HostBackdropBrush 采样窗口
// 背后的实时画面，高斯模糊后由「圆角几何裁剪」切出任意半径（DWM 窗口圆角写死 8，做不到）。
// 与 Pane 同一套做法：模糊在自家合成树里，才谈得上自定义圆角 + 实时背景。
#include <windows.h>
#include <unknwn.h>
#include <dispatcherqueue.h>
#include <d2d1effects.h>
#include <d2d1.h>
#include <windows.graphics.effects.interop.h>
#include <windows.ui.composition.h>
#include <windows.ui.composition.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>

#include <cmath>
#include <cstdio>
#include <cwchar>
#include <unordered_map>
#include <utility>

using namespace ABI::Windows::Graphics::Effects;
using namespace winrt::Windows::Foundation::Numerics;
namespace WUC = winrt::Windows::UI::Composition;
namespace WGE = winrt::Windows::Graphics::Effects;
namespace WUI = winrt::Windows::UI;
namespace ABI_WUC = ABI::Windows::UI::Composition;

// ---------------------------------------------------------------- 高斯模糊效果
// CompositionEffectFactory 只吃 IGraphicsEffect；系统没有公开的「模糊」WinRT 类，
// 所以自己实现 IGraphicsEffectD2D1Interop 描述 CLSID_D2D1GaussianBlur。
struct BlurEffect : winrt::implements<BlurEffect,
    WGE::IGraphicsEffect, WGE::IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    WGE::IGraphicsEffectSource source{ nullptr };
    winrt::hstring name{ L"ZonexBlur" };
    float amount = 30.0f;

    explicit BlurEffect(WGE::IGraphicsEffectSource const& s) : source(s) {}

    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& value) { name = value; }

    HRESULT __stdcall GetEffectId(GUID* id) noexcept override { *id = CLSID_D2D1GaussianBlur; return S_OK; }

    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR n, UINT* index,
        GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override
    {
        *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
        if (wcscmp(n, L"BlurAmount") == 0) { *index = 0; return S_OK; }
        if (wcscmp(n, L"Optimization") == 0) { *index = 1; return S_OK; }
        if (wcscmp(n, L"BorderMode") == 0) { *index = 2; return S_OK; }
        return E_INVALIDARG;
    }

    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override { *count = 3; return S_OK; }

    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override
    {
        if (index >= 3u) return E_INVALIDARG;
        try
        {
            auto property = index == 0
                ? winrt::Windows::Foundation::PropertyValue::CreateSingle(amount)
                : winrt::Windows::Foundation::PropertyValue::CreateUInt32(
                    index == 1 ? D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED : D2D1_BORDER_MODE_HARD);
            *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(property));
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSource(UINT index, IGraphicsEffectSource** value) noexcept override
    {
        if (index != 0) return E_INVALIDARG;
        winrt::copy_to_abi(source, *reinterpret_cast<void**>(value));
        return S_OK;
    }

    HRESULT __stdcall GetSourceCount(UINT* count) noexcept override { *count = 1; return S_OK; }
};

// ---------------------------------------------------------------- 裁剪效果
// CLSID_D2D1Crop：把输入（= 宿主窗口背后的实时画面）先裁到「卡片矩形」，再做高斯模糊。
//
// 为什么必须裁：宿主窗口为了放投影，比卡片四周各外扩 marginDip（48）。相邻分区之间只隔
// ~10px，于是宿主的窗口矩形**必然盖住上下邻居的卡片**。高斯模糊的采样半径（32 DIP ≈ 3σ
// 近百像素）远超 margin，邻居卡片的半透明底色/文字就会被吸进本分区的背景 ——
// 表现就是「某个分区背景莫名其妙偏亮、像糊了别人的内容」，且随 Z 序变化时有时无。
// BorderMode=HARD：裁切线外复制边缘像素，模糊后边缘不会发暗/发透明。
struct CropEffect : winrt::implements<CropEffect,
    WGE::IGraphicsEffect, WGE::IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    WGE::IGraphicsEffectSource source{ nullptr };
    winrt::hstring name{ L"ZonexCrop" };
    // D2D1_VECTOR_4F：left, top, right, bottom（DIP，相对输入画面左上角）
    float rect[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
    // 序列化写法（自检用）：0=SingleArray(4)+BorderMode 1=CreateRect(X,Y,W,H) 2=只有 Rect 一个属性
    int propMode = 0;

    explicit CropEffect(WGE::IGraphicsEffectSource const& s) : source(s) {}

    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& value) { name = value; }

    HRESULT __stdcall GetEffectId(GUID* id) noexcept override { *id = CLSID_D2D1Crop; return S_OK; }

    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR n, UINT* index,
        GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override
    {
        // RECT 属性必须声明成 RECT_TO_VECTOR4，DIRECT 会被工厂判成类型不符（E_INVALIDARG）
        *mapping = (propMode >= 3) ? GRAPHICS_EFFECT_PROPERTY_MAPPING_RECT_TO_VECTOR4
                                   : GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
        if (wcscmp(n, L"Rect") == 0) { *index = 0; return S_OK; }
        if (wcscmp(n, L"BorderMode") == 0) { *index = 1; return S_OK; }
        return E_INVALIDARG;
    }

    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override
    {
        *count = (propMode == 2) ? 1u : 2u;
        return S_OK;
    }

    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override
    {
        UINT cnt = (propMode == 2) ? 1u : 2u;
        if (index >= cnt) return E_INVALIDARG;
        if (propMode == 5)   // 自检用：两个属性都当 UInt32 传
        {
            auto p = winrt::Windows::Foundation::PropertyValue::CreateUInt32(0);
            *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
            return S_OK;
        }
        try
        {
            if (index == 0)
            {
                ABI::Windows::Foundation::IPropertyValue* pv = nullptr;
                if (propMode == 1)
                {
                    // CreateRect 的语义是 X,Y,Width,Height
                    auto p = winrt::Windows::Foundation::PropertyValue::CreateRect(
                        winrt::Windows::Foundation::Rect{ rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1] });
                    pv = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
                }
                else if (propMode == 4)
                {
                    // RECT_TO_VECTOR4：按 X, Y, Width, Height 传
                    float v[4] = { rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1] };
                    auto p = winrt::Windows::Foundation::PropertyValue::CreateSingleArray(
                        winrt::array_view<float const>(v, v + 4));
                    pv = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
                }
                else
                {
                    auto p = winrt::Windows::Foundation::PropertyValue::CreateSingleArray(
                        winrt::array_view<float const>(rect, rect + 4));
                    pv = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
                }
                *value = pv;
            }
            else
            {
                auto property = winrt::Windows::Foundation::PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD);
                *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(property));
            }
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSource(UINT index, IGraphicsEffectSource** value) noexcept override
    {
        if (index != 0) return E_INVALIDARG;
        winrt::copy_to_abi(source, *reinterpret_cast<void**>(value));
        return S_OK;
    }

    HRESULT __stdcall GetSourceCount(UINT* count) noexcept override { *count = 1; return S_OK; }
};

// ---------------------------------------------------------------- 自检用的通用效果节点
// 只为了二分定位：是「Crop 这个节点不被支持」，还是「vector4 属性序列化不对」。
struct ProbeEffect : winrt::implements<ProbeEffect,
    WGE::IGraphicsEffect, WGE::IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    WGE::IGraphicsEffectSource source{ nullptr };
    winrt::hstring name{ L"ZonexProbe" };
    GUID id{};
    bool hasInput = true;
    bool hasColor = false;      // true = 声明一个 VECTOR4 属性（COLOR 用 SingleArray(4)）
    // true = 声明一个 Rect 属性，且映射用 RECT_TO_VECTOR4 + PropertyValue::CreateRect。
    // ⭐ 这个组合（CreateRect 值 + RECT_TO_VECTOR4 映射）在 17:30 的探针里**从没试过**：
    // 之前试的是 CreateRect+DIRECT、SingleArray+RECT_TO_VECTOR4 两种错配，全都 E_INVALIDARG。
    bool useRect = false;
    float color[4] = { 1.0f, 1.0f, 1.0f, 1.0f };

    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& v) { name = v; }

    HRESULT __stdcall GetEffectId(GUID* out) noexcept override { *out = id; return S_OK; }

    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR n, UINT* index,
        GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override
    {
        if (useRect && wcscmp(n, L"Rect") == 0)
        {
            *index = 0;
            *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING_RECT_TO_VECTOR4;
            return S_OK;
        }
        *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
        if (hasColor && wcscmp(n, L"Color") == 0) { *index = 0; return S_OK; }
        return E_INVALIDARG;
    }

    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override
    {
        *count = (hasColor || useRect) ? 1u : 0u;
        return S_OK;
    }

    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override
    {
        if ((!hasColor && !useRect) || index != 0) return E_INVALIDARG;
        try
        {
            if (useRect)
            {
                // Crop 的 Rect 在 D2D 侧是 RECT：CreateRect 的语义是 X, Y, Width, Height
                auto p = winrt::Windows::Foundation::PropertyValue::CreateRect(
                    winrt::Windows::Foundation::Rect{ color[0], color[1], color[2], color[3] });
                *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
                return S_OK;
            }
            auto pv = winrt::Windows::Foundation::PropertyValue::CreateSingleArray(
                winrt::array_view<float const>(color, color + 4));
            *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(pv));
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSource(UINT index, IGraphicsEffectSource** value) noexcept override
    {
        if (index != 0 || !source) return E_INVALIDARG;
        winrt::copy_to_abi(source, *reinterpret_cast<void**>(value));
        return S_OK;
    }

    HRESULT __stdcall GetSourceCount(UINT* count) noexcept override
    {
        *count = (hasInput && source) ? 1u : 0u;
        return S_OK;
    }
};

// ---------------------------------------------------------------- 饱和度效果（方案⑥）
// CLSID_D2D1Saturation：单输入 + 单 float 属性。用来验证「非模糊的内置效果节点」到底能不能
// 进 CreateEffectFactory —— 之前只试过 Crop/ColorMatrix/Flood（都失败），Saturation 没试过。
struct SaturationEffect : winrt::implements<SaturationEffect,
    WGE::IGraphicsEffect, WGE::IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    WGE::IGraphicsEffectSource source{ nullptr };
    winrt::hstring name{ L"ZonexSat" };
    float amount = 1.0f;

    explicit SaturationEffect(WGE::IGraphicsEffectSource const& s) : source(s) {}

    winrt::hstring Name() const { return name; }
    void Name(winrt::hstring const& v) { name = v; }

    HRESULT __stdcall GetEffectId(GUID* id) noexcept override { *id = CLSID_D2D1Saturation; return S_OK; }

    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR n, UINT* index,
        GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override
    {
        *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
        if (wcscmp(n, L"Saturation") == 0) { *index = 0; return S_OK; }
        return E_INVALIDARG;
    }

    HRESULT __stdcall GetPropertyCount(UINT* count) noexcept override { *count = 1; return S_OK; }

    HRESULT __stdcall GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept override
    {
        if (index != 0) return E_INVALIDARG;
        try
        {
            auto p = winrt::Windows::Foundation::PropertyValue::CreateSingle(amount);
            *value = reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(winrt::detach_abi(p));
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSource(UINT index, IGraphicsEffectSource** value) noexcept override
    {
        if (index != 0) return E_INVALIDARG;
        winrt::copy_to_abi(source, *reinterpret_cast<void**>(value));
        return S_OK;
    }

    HRESULT __stdcall GetSourceCount(UINT* count) noexcept override { *count = 1; return S_OK; }
};

// ---------------------------------------------------------------- 线程 DispatcherQueue
// Compositor 的构造函数要求当前线程已有 DispatcherQueue；纯 Win32 线程没有，
// 用官方 API 就地挂一个（DQTYPE_THREAD_CURRENT，依赖本线程的消息泵）。
static thread_local winrt::com_ptr<ABI::Windows::System::IDispatcherQueueController> g_dispatcher;

static void EnsureDispatcher()
{
    if (g_dispatcher) return;
    DispatcherQueueOptions options{ sizeof(DispatcherQueueOptions), DQTYPE_THREAD_CURRENT, DQTAT_COM_NONE };
    winrt::com_ptr<ABI::Windows::System::IDispatcherQueueController> controller;
    winrt::check_hresult(CreateDispatcherQueueController(options, controller.put()));
    g_dispatcher = controller;
}

static WUI::Color Argb(uint32_t v)
{
    return WUI::Color{ static_cast<uint8_t>((v >> 24) & 0xFF), static_cast<uint8_t>((v >> 16) & 0xFF),
                       static_cast<uint8_t>((v >> 8) & 0xFF), static_cast<uint8_t>(v & 0xFF) };
}

// ---------------------------------------------------------------- 宿主对象
struct GlassHost
{
    WUC::Compositor compositor{ nullptr };
    WUC::CompositionTarget target{ nullptr };
    WUC::ContainerVisual root{ nullptr };
    WUC::SpriteVisual shadowBig{ nullptr };
    WUC::SpriteVisual shadowSmall{ nullptr };
    // 投影效果对象本身也留着：ZonexGlassSetShadow 要在不重建合成树的前提下改参数
    WUC::DropShadow shadowBigFx{ nullptr };
    WUC::DropShadow shadowSmallFx{ nullptr };
    WUC::SpriteVisual material{ nullptr };
    // 材质 visual 的圆角裁剪几何：**铺满整个宿主窗口**（含 margin）、用 Offset 把圆角挪到面板位置。
    // 这样 visual 的本地坐标和窗口坐标是同一套，Crop 的矩形不会有坐标语义歧义。
    WUC::CompositionRoundedRectangleGeometry materialClip{ nullptr };
    // 材质用的 effect 链（Backdrop → [Crop 到卡片矩形] → GaussianBlur）。尺寸变化要重建 ——
    // Crop 的矩形是 effect 图里的常量，改尺寸只能换一张图。
    WUC::CompositionEffectFactory materialFactory{ nullptr };
    WUC::CompositionEffectBrush materialBrush{ nullptr };
    // ⭐ 采样源只创建一次、全程复用。
    // 折叠/展开动画里尺寸逐帧变化 → 每帧都要重建 effect 图（Crop 矩形是图里的常量）；
    // 如果每次重建都 CreateHostBackdropBrush()，等于每帧换一个全新采样源，
    // DWM 得从零开始采 → 动画期间背景随机闪一下（用户看到的就是「展开/折叠时模糊闪烁」）。
    // 复用同一个 brush，重建只是换一张效果图，采样源不变 → 不闪。
    WUC::CompositionBackdropBrush backdrop{ nullptr };
    // 材质 effect 图需要按新矩形重建但还没成功（上次 CreateEffectFactory 抛了）。
    // 重建失败不能只吞掉：Crop 会停在旧矩形，采样区域比卡片大 → 背景吸进卡片外的亮壁纸，
    // 表现就是「折叠完某一条背景莫名发亮」，而且不会自愈。
    bool materialDirty = false;
    float materialBlur = 0.0f;
    // 内容可见性总开关（ZonexGlassSetContentVisible）：宿主窗口可以**保持可见**而不画内容，
    // 这样 DWM 不会丢掉 HostBackdropBrush 的采样；显隐联动恢复时打开内容即带磨砂。
    bool contentVisible = true;
    // ⭐⭐ 运行期**绝不**降级成「不裁剪」的模糊链。
    // 无裁链的采样域是整个宿主窗口（卡片 + 四周 margin），比卡片大一圈 → 把卡片外的亮壁纸
    // 平均进背景，表现就是「一键折叠后某几条背景明显发亮，过一会儿又自己好了」。
    // 只有连续多次都建不出 Crop 链（= 本机 D2D 真不支持该节点）才永久降级，保证玻璃还在。
    // ⚠️ Crop 节点在本机合成器上**不可用**（`ZonexGlassRunEffectProbe` 实测：Crop 任何写法、
    // 以及 blur(blur(backdrop)) 这类两节点图，CreateEffectFactory 全部 E_INVALIDARG ——
    // 这台机器上的 CompositionEffectFactory 只接受「单个效果节点 + Backdrop 直连」）。
    // 所以「先模糊再按卡片矩形裁」这条路根本实现不了：采样域只能由**宿主窗口矩形**决定。
    // 结论：宿主窗口必须精确等于卡片矩形（margin=0），投影只能另开一层窗口画。
    bool cropSupported = false;
    int cropFails = 0;
    // ⭐⭐ 裁剪矩形优先走**运行时属性**（`ZonexCrop.Rect`）而不是每次重建效果图：
    // 折叠/展开动画里尺寸逐帧变，每帧 CreateEffectFactory 一次 → 动画期连续失败（实测 3 次连挂就永久降级，
    // 背景发亮）。改成建一次图、之后只 InsertVector4 改矩形，动画期间完全不碰工厂。
    bool cropViaProperty = false;
    HRESULT lastMaterialErr = S_OK;
    int materialModeLogged = 0;
    WUC::SpriteVisual tint{ nullptr };
    WUC::SpriteVisual glow{ nullptr };
    // 透传层（仅 blur<=0 的分区路径）：盖在两个影子 visual 之上、面板矩形内的圆角裁剪
    // HostBackdropBrush —— 让卡片矩形内部显示宿主背后的实时原画面，压掉剪影糊进卡片的内部黑影
    //（半透明卡片会把影子 visual 在其矩形内部渲染的近全黑剪影采进自己的 AcrylicBlur）。
    WUC::SpriteVisual passthrough{ nullptr };
    WUC::CompositionRoundedRectangleGeometry clip{ nullptr };
    WUC::CompositionRoundedRectangleGeometry maskGeo{ nullptr };
    WUC::ShapeVisual maskVisual{ nullptr };
    WUC::CompositionVisualSurface maskSurface{ nullptr };
    float radius = 20.0f;
    float margin = 0.0f;   // 窗口比面板大出来的部分（给阴影留位置），DIP
    float width = 0.0f;
    float height = 0.0f;

    // 主影（大而柔）的三个参数，可由 ZonexGlassSetShadow 在运行时改；
    // 接触影（小而实）按固定比例跟着走，保证「浮起 + 贴地」两层关系不会被调散。
    // 默认贴合本项目的观感：几乎不下移（offsetY = 2），靠大模糊向四周柔和外扩。
    float shadowOpacity = 0.30f;
    float shadowBlur = 48.0f;
    float shadowOffsetY = 2.0f;
};

static constexpr float kContactOpacityRatio = 1.0f / 3.0f;   // 0.10 / 0.30
static constexpr float kContactBlurRatio = 1.0f / 6.0f;      // 8 / 48
static constexpr float kContactOffsetRatio = 1.0f / 9.0f;    // 2 / 18

// 把裁剪矩形按当前卡片尺寸写进 brush 的运行时属性（不重建效果图）。
// D2D1_CROP_PROP_RECT 是 VECTOR4(left, top, right, bottom)，能走 Properties 就不必每帧建图。
// 成功返回 true；属性不可写（抛异常）返回 false，调用方退回去重建图。
static bool UpdateCropRect(GlassHost* h)
{
    if (!h || !h->cropViaProperty || !h->materialBrush) return false;
    try
    {
        h->materialBrush.Properties().InsertVector4(L"ZonexCrop.Rect",
            float4{ h->margin, h->margin, h->margin + h->width, h->margin + h->height });
        return true;
    }
    catch (...)
    {
        h->cropViaProperty = false;
        return false;
    }
}

// 重建材质 effect 图：HostBackdropBrush →（Crop 到卡片矩形）→ GaussianBlur。
// 有 margin 时必须先裁：宿主窗口比卡片大一圈，不裁会把相邻分区卡片的内容模糊进背景。
// 返回 true = 已经换成新图；false = 失败（旧图保持不动，materialDirty 置位等下次重试）。
static bool RebuildMaterial(GlassHost* h)
{
    if (!h || h->materialBlur <= 0.0f) return false;
    if (h->width <= 0.0f || h->height <= 0.0f) return false;   // 尺寸还没同步，等 ApplySize 再来

    auto parameter = WUC::CompositionEffectSourceParameter(L"Backdrop");
    WGE::IGraphicsEffectSource source{ nullptr };
    winrt::copy_from_abi(source, winrt::get_abi(parameter));

    // 采样源复用（不重建），只在真没有时才补一个 —— 见 GlassHost::backdrop 的注释
    if (!h->backdrop) h->backdrop = h->compositor.CreateHostBackdropBrush();

    const bool useCrop = h->margin > 0.5f && h->cropSupported;

    // 只有建成功才写回 GlassHost 的字段，失败时旧 brush 原封不动留在画面上
    auto build = [&](bool crop) -> WUC::CompositionEffectBrush
    {
        WGE::IGraphicsEffectSource input = source;
        if (crop)
        {
            // 材质 visual 铺满整个宿主窗口，本地坐标 ≡ 窗口坐标 → 裁剪矩形直接按面板在窗口里的位置算，
            // 不依赖「backdrop 采样用 visual 坐标还是窗口坐标」这个语义假设。
            auto cropFx = winrt::make_self<CropEffect>(source);
            cropFx->rect[0] = h->margin;
            cropFx->rect[1] = h->margin;
            cropFx->rect[2] = h->margin + h->width;
            cropFx->rect[3] = h->margin + h->height;
            input = cropFx.as<WGE::IGraphicsEffectSource>();
        }

        auto effect = winrt::make_self<BlurEffect>(input);
        effect->amount = h->materialBlur;

        auto factory = h->compositor.CreateEffectFactory(effect.as<WGE::IGraphicsEffect>());
        auto brush = factory.CreateBrush();
        brush.SetSourceParameter(L"Backdrop", h->backdrop);
        h->materialFactory = factory;   // 成功才更新
        return brush;
    };

    try
    {
        auto brush = build(useCrop);
        h->materialBrush = brush;
        if (h->material) h->material.Brush(h->materialBrush);
        h->materialDirty = false;
        h->cropFails = 0;

        // 建完立刻试一次「属性改矩形」：能改的话，之后所有尺寸变化都不用再建图了
        if (useCrop)
        {
            try
            {
                brush.Properties().InsertVector4(L"ZonexCrop.Rect",
                    float4{ h->margin, h->margin, h->margin + h->width, h->margin + h->height });
                h->cropViaProperty = true;
            }
            catch (...) { h->cropViaProperty = false; }
        }
        else h->cropViaProperty = false;
        return true;
    }
    catch (winrt::hresult_error const& e)
    {
        h->lastMaterialErr = e.code();
        // ⚠️ 绝不降级成「无裁剪」链：无裁链采样域 = 整窗（卡片 + 四周 margin），对 48px 折叠条
        // 面积大 3 倍 → 卡片外亮壁纸被平均进背景（「某几条莫名发亮」）。保留旧图 + 置脏重试，
        // 旧裁剪只是尺寸差一帧，肉眼看不出来。
        ++h->cropFails;
        h->materialDirty = true;
        return false;
    }
    catch (...)
    {
        h->lastMaterialErr = E_FAIL;
        ++h->cropFails;
        h->materialDirty = true;
        return false;
    }
}

static void ApplySize(GlassHost* h, float w, float ht)
{
    if (!h || w <= 0 || ht <= 0) return;
    const bool sizeChanged = fabsf(w - h->width) > 0.01f || fabsf(ht - h->height) > 0.01f;
    h->width = w;
    h->height = ht;
    const float m = h->margin;
    const float2 full{ w + m * 2, ht + m * 2 };
    const float2 panel{ w, ht };
    const float3 origin{ m, m, 0.0f };

    h->root.Size(full);
    h->clip.Size(panel);
    // 材质 visual 铺满整窗（含 margin），圆角裁剪几何内缩到面板位置 —— 见 materialClip 的注释
    if (h->material) { h->material.Size(full); h->material.Offset({ 0.0f, 0.0f, 0.0f }); }
    if (h->materialClip) { h->materialClip.Size(panel); h->materialClip.Offset({ m, m }); }
    if (h->tint) { h->tint.Size(panel); h->tint.Offset(origin); }
    if (h->glow) { h->glow.Size(panel); h->glow.Offset(origin); }
    if (h->passthrough) { h->passthrough.Size(panel); h->passthrough.Offset(origin); }
    if (h->shadowBig) { h->shadowBig.Size(panel); h->shadowBig.Offset(origin); }
    if (h->shadowSmall) { h->shadowSmall.Size(panel); h->shadowSmall.Offset(origin); }
    if (h->maskGeo) h->maskGeo.Size(panel);
    if (h->maskVisual) h->maskVisual.Size(panel);
    if (h->maskSurface) h->maskSurface.SourceSize(panel);

    // 卡片矩形变了 → 裁剪矩形跟着变。
    // ① 能走属性（cropViaProperty）就只改矩形，**完全不建效果图**：动画期间零工厂创建，
    //    既不会闪，也不会撞上 CreateEffectFactory 偶发失败。
    // ② 属性不可写才重建图；重建失败保留旧图 + 置脏重试（绝不退化成「无裁剪」的亮背景）。
    if (h->material && h->materialBlur > 0.0f)
    {
        if (h->materialDirty) RebuildMaterial(h);
        else if (sizeChanged && !UpdateCropRect(h)) RebuildMaterial(h);
    }
}
static void ApplyRadius(GlassHost* h, float radius)
{
    const float2 r{ radius, radius };
    if (h->clip) h->clip.CornerRadius(r);
    if (h->materialClip) h->materialClip.CornerRadius(r);
    if (h->maskGeo) h->maskGeo.CornerRadius(r);
}

// 把 host 上存的投影参数写进两个 DropShadow。附着时和运行时改参数走同一条路径，
// 免得「默认值」和「用户调过之后」出现两套渲染结果。
static void ApplyShadow(GlassHost* h)
{
    if (!h) return;
    if (h->shadowBigFx)
    {
        h->shadowBigFx.BlurRadius(fmaxf(0.0f, h->shadowBlur));
        h->shadowBigFx.Offset({ 0.0f, h->shadowOffsetY, 0.0f });
        h->shadowBigFx.Opacity(h->shadowOpacity);
    }
    if (h->shadowSmallFx)
    {
        h->shadowSmallFx.BlurRadius(fmaxf(1.0f, h->shadowBlur * kContactBlurRatio));
        h->shadowSmallFx.Offset({ 0.0f, h->shadowOffsetY * kContactOffsetRatio, 0.0f });
        h->shadowSmallFx.Opacity(h->shadowOpacity * kContactOpacityRatio);
    }
}

// 内容总开关：磨砂 / 透传 / 投影一起开合。窗口本身保持可见 —— 那样 DWM 才不会丢掉
// HostBackdropBrush 的采样，恢复时打开内容就是带磨砂的第一帧。
static void ApplyContentVisible(GlassHost* h)
{
    if (!h) return;
    if (h->material) h->material.IsVisible(h->contentVisible);
    if (h->passthrough) h->passthrough.IsVisible(h->contentVisible);
    if (h->shadowBig) h->shadowBig.IsVisible(h->contentVisible);
    if (h->shadowSmall) h->shadowSmall.IsVisible(h->contentVisible);
}

// ⭐ 自检：Crop 效果链从第一次构建起就 E_INVALIDARG（在 15:18 版实测），
// 于是所有分区其实一直在走「无裁剪」链（背景吸进卡片外的亮壁纸）。
// 这里把几种写法逐个试一遍，结果写到 %TEMP%\zonex_effect_probe.txt，一次把正确写法找出来。
static void RunEffectProbeVariants()
{
    wchar_t tmp[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH, tmp)) return;
    wcscat_s(tmp, L"zonex_effect_probe.txt");
    FILE* f = nullptr;
    _wfopen_s(&f, tmp, L"w");
    if (!f) return;

    try
    {
        EnsureDispatcher();
        WUC::Compositor c;
        auto backdrop = c.CreateHostBackdropBrush();

        auto param = WUC::CompositionEffectSourceParameter(L"Backdrop");
        WGE::IGraphicsEffectSource src{ nullptr };
        winrt::copy_from_abi(src, winrt::get_abi(param));

        auto run = [&](const char* label, WGE::IGraphicsEffect const& graph)
        {
            HRESULT hr = S_OK;
            const char* stage = "factory";
            try
            {
                auto factory = c.CreateEffectFactory(graph);
                stage = "brush";
                auto brush = factory.CreateBrush();
                stage = "source";
                brush.SetSourceParameter(L"Backdrop", backdrop);
                stage = "ok";
            }
            catch (winrt::hresult_error const& e) { hr = e.code(); }
            catch (...) { hr = E_FAIL; }
            fprintf(f, "%-38s hr=0x%08X stage=%s\n", label, static_cast<unsigned>(hr), stage);
            fflush(f);
        };

        // 0) 基线：只有模糊（已知可用，所有分区实际在跑这条）
        {
            auto blur = winrt::make_self<BlurEffect>(src);
            blur->amount = 32.0f;
            run("blur(only)", blur.as<WGE::IGraphicsEffect>());
        }

        // 1) crop(SingleArray4 + BorderMode) → blur   ← 现行写法
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(singlearray+border)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // 2) blur → crop（顺序反过来）
        {
            auto blur = winrt::make_self<BlurEffect>(src);
            blur->amount = 32.0f;
            auto crop = winrt::make_self<CropEffect>(blur.as<WGE::IGraphicsEffectSource>());
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            // 顶层必须是 IGraphicsEffect：再套一层 blur 无意义，直接拿 crop 当图
            run("blur->crop(only)", crop.as<WGE::IGraphicsEffect>());
        }

        // 3) crop 用 CreateRect（X,Y,W,H）→ blur
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            crop->propMode = 1;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(createrect)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // 4) crop 只声明 Rect 一个属性（不声明 BorderMode）→ blur
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            crop->propMode = 2;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(rect-only)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // 5) 完全不带属性的 Crop（看是不是「声明了属性」才挂）
        {
            auto e = winrt::make_self<ProbeEffect>();
            e->id = CLSID_D2D1Crop;
            e->source = src;
            run("crop(no-prop)", e.as<WGE::IGraphicsEffect>());
        }

        // 6) 不带属性的 ColorMatrix：证明「非模糊的普通效果」能不能用
        {
            auto e = winrt::make_self<ProbeEffect>();
            e->id = CLSID_D2D1ColorMatrix;
            e->source = src;
            run("colormatrix(no-prop)", e.as<WGE::IGraphicsEffect>());
        }

        // 7) Flood + VECTOR4 属性：单独验证「vector4 属性序列化」这条路
        {
            auto e = winrt::make_self<ProbeEffect>();
            e->id = CLSID_D2D1Flood;
            e->hasInput = false;
            e->hasColor = true;
            run("flood(vector4-prop)", e.as<WGE::IGraphicsEffect>());
        }

        // 8) Flood 不带属性：对照 7
        {
            auto e = winrt::make_self<ProbeEffect>();
            e->id = CLSID_D2D1Flood;
            e->hasInput = false;
            run("flood(no-prop)", e.as<WGE::IGraphicsEffect>());
        }

        // 9) crop 声明 RECT_TO_VECTOR4 映射（l,t,r,b 传法）
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            crop->propMode = 3;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(rect->vec4 ltrb)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // 10) crop 声明 RECT_TO_VECTOR4 映射（x,y,w,h 传法）
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            crop->propMode = 4;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(rect->vec4 xywh)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // 11) 两个已知可用的 blur 串起来：验证「图里有两个节点」本身能不能过
        {
            auto b1 = winrt::make_self<BlurEffect>(src);
            b1->amount = 16.0f;
            auto b2 = winrt::make_self<BlurEffect>(b1.as<WGE::IGraphicsEffectSource>());
            b2->amount = 16.0f;
            run("blur(blur(backdrop))", b2.as<WGE::IGraphicsEffect>());
        }

        // 12) crop 声明两个属性但都用 UInt32（故意类型不符）：看工厂是否只查数量不查类型
        {
            auto crop = winrt::make_self<CropEffect>(src);
            crop->rect[0] = 48; crop->rect[1] = 48; crop->rect[2] = 610; crop->rect[3] = 96;
            crop->propMode = 5;
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            run("crop(uint32 props)->blur", blur.as<WGE::IGraphicsEffect>());
        }
    }
    catch (...) { fprintf(f, "probe crashed\n"); }

    fclose(f);
}

// ---------------------------------------------------------------- 方案⑥ 探针（第二轮）
// 第一轮（RunEffectProbeVariants）的结论是「只有 blur(only) 能过，其它全 E_INVALIDARG」，
// 但那一轮有两个变量没分离：
//   ① **节点重名**：两个 BlurEffect 的 Name() 都返回 "ZonexBlur"，WUC 用节点名做图里的键，
//      重名可能直接 E_INVALIDARG（这能解释 blur(blur(backdrop)) 为什么也失败）。
//   ② **Rect 属性的正确组合**：只试过 CreateRect+DIRECT 和 SingleArray+RECT_TO_VECTOR4 两种错配，
//      正确写法应该是 **CreateRect 值 + RECT_TO_VECTOR4 映射**，从来没试过。
// 这一轮把两个变量分开验证，顺带补一个 Saturation 单节点（之前完全没试过）。
// 结果写 %TEMP%\zonex_effect_probe2.txt。
static void RunEffectProbeVariants2()
{
    wchar_t tmp[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH, tmp)) return;
    wcscat_s(tmp, L"zonex_effect_probe2.txt");
    FILE* f = nullptr;
    _wfopen_s(&f, tmp, L"w");
    if (!f) return;

    try
    {
        EnsureDispatcher();
        WUC::Compositor c;
        auto backdrop = c.CreateHostBackdropBrush();

        auto param = WUC::CompositionEffectSourceParameter(L"Backdrop");
        WGE::IGraphicsEffectSource src{ nullptr };
        winrt::copy_from_abi(src, winrt::get_abi(param));

        auto run = [&](const char* label, WGE::IGraphicsEffect const& graph)
        {
            HRESULT hr = S_OK;
            const char* stage = "factory";
            try
            {
                auto factory = c.CreateEffectFactory(graph);
                stage = "brush";
                auto brush = factory.CreateBrush();
                stage = "ok";
            }
            catch (winrt::hresult_error const& e) { hr = e.code(); }
            catch (...) { hr = E_FAIL; }
            fprintf(f, "%-40s hr=0x%08X stage=%s\n", label, static_cast<unsigned>(hr), stage);
            fflush(f);
        };

        // A) 两个 blur，**名字不同** —— 只改名字这一个变量
        {
            auto b1 = winrt::make_self<BlurEffect>(src);
            b1->amount = 16.0f;
            b1->Name(L"b1");
            auto b2 = winrt::make_self<BlurEffect>(b1.as<WGE::IGraphicsEffectSource>());
            b2->amount = 16.0f;
            b2->Name(L"b2");
            run("A blur(b1)->blur(b2) unique names", b2.as<WGE::IGraphicsEffect>());
        }

        // B) 饱和度单节点
        {
            auto sat = winrt::make_self<SaturationEffect>(src);
            sat->amount = 1.6f;
            run("B saturation(only)", sat.as<WGE::IGraphicsEffect>());
        }

        // C) 饱和度 → 模糊（两节点，名字唯一）
        {
            auto sat = winrt::make_self<SaturationEffect>(src);
            sat->amount = 1.6f;
            sat->Name(L"sat1");
            auto blur = winrt::make_self<BlurEffect>(sat.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            blur->Name(L"blur1");
            run("C saturation->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // D) Crop 单节点：CreateRect 值 + RECT_TO_VECTOR4 映射（正确组合，从没试过）
        {
            auto e = winrt::make_self<ProbeEffect>();
            e->id = CLSID_D2D1Crop;
            e->source = src;
            e->useRect = true;
            e->color[0] = 48; e->color[1] = 48;   // X, Y
            e->color[2] = 562; e->color[3] = 48;  // Width, Height
            e->Name(L"crop1");
            run("D crop(only) CreateRect+RectToVec4", e.as<WGE::IGraphicsEffect>());
        }

        // E) Crop(CreateRect+RectToVec4) → blur
        {
            auto crop = winrt::make_self<ProbeEffect>();
            crop->id = CLSID_D2D1Crop;
            crop->source = src;
            crop->useRect = true;
            crop->color[0] = 48; crop->color[1] = 48;
            crop->color[2] = 562; crop->color[3] = 48;
            crop->Name(L"crop1");
            auto blur = winrt::make_self<BlurEffect>(crop.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            blur->Name(L"blur1");
            run("E crop(CreateRect)->blur", blur.as<WGE::IGraphicsEffect>());
        }

        // F) 方案⑥的最终形态：crop → saturation → blur（三节点，全唯一名）
        {
            auto crop = winrt::make_self<ProbeEffect>();
            crop->id = CLSID_D2D1Crop;
            crop->source = src;
            crop->useRect = true;
            crop->color[0] = 48; crop->color[1] = 48;
            crop->color[2] = 562; crop->color[3] = 48;
            crop->Name(L"crop1");
            auto sat = winrt::make_self<SaturationEffect>(crop.as<WGE::IGraphicsEffectSource>());
            sat->amount = 1.6f;
            sat->Name(L"sat1");
            auto blur = winrt::make_self<BlurEffect>(sat.as<WGE::IGraphicsEffectSource>());
            blur->amount = 32.0f;
            blur->Name(L"blur1");
            run("F crop->saturation->blur", blur.as<WGE::IGraphicsEffect>());
        }
    }
    catch (...) { fprintf(f, "probe2 crashed\n"); }

    fclose(f);
}

static void ProbeEffectVariantsOnce()
{
    static bool done = false;
    if (done) return;
    done = true;
    RunEffectProbeVariants();
}

// 方案⑥ 探针入口（只在显式调用时跑，不挂到产品路径）
static void RunEffectProbe2Once()
{
    RunEffectProbeVariants2();
}
// ---------------------------------------------------------------- 方案④ 多面板可行性探针
// 只读诊断，**产品路径完全不调用**。回答三个平台前提：
//   P1 全屏 HWND 能否开 DWMWA_USE_HOSTBACKDROPBRUSH 并建 DesktopWindowTarget（小窗做对照组）
//   P2 一个 HostBackdropBrush / 由它生成的 effect brush 能否被多个 SpriteVisual 共享
//   P3 每个 SpriteVisual 能否挂独立的 GeometricClip，且 Offset/Size 能在运行时改
//      （= 拖动/折叠只改 visual 属性、不再 SetWindowPos，这是方案④的核心收益）
// mode=0 不显示窗口（零干扰，默认）；mode=1 额外短暂显示全屏窗并抓几个像素点。
// 结果写 %TEMP%\zonex_multipanel_probe.txt，供 Python ctypes 读取。
using ProbeDwmSetAttrFn = HRESULT(WINAPI*)(HWND, DWORD, LPCVOID, DWORD);

static ProbeDwmSetAttrFn ProbeDwmSetAttr()
{
    static ProbeDwmSetAttrFn fn = []() -> ProbeDwmSetAttrFn
    {
        auto h = LoadLibraryW(L"dwmapi.dll");
        return h ? reinterpret_cast<ProbeDwmSetAttrFn>(GetProcAddress(h, "DwmSetWindowAttribute")) : nullptr;
    }();
    return fn;
}

static void ProbeLine(FILE* f, const char* tag, const char* label, HRESULT hr)
{
    fprintf(f, "%-6s %-38s hr=0x%08X  %s\n", tag, label,
        static_cast<unsigned>(hr), SUCCEEDED(hr) ? "OK" : "FAIL");
    fflush(f);
}

// 屏幕 DC 取几个像素点：只记录 RGB 数值，不生成图片、不做界面回归。
static void ProbeRecordPixels(FILE* f, const char* tag, const char* phase,
    int winX, int winY, int winW, int winH)
{
    using GetDCFn = HDC(WINAPI*)(HWND);
    using ReleaseDCFn = int(WINAPI*)(HWND, HDC);
    using GetPixelFn = COLORREF(WINAPI*)(HDC, int, int);
    static GetDCFn pGetDC = nullptr;
    static ReleaseDCFn pReleaseDC = nullptr;
    static GetPixelFn pGetPixel = nullptr;
    static bool loaded = false;
    if (!loaded)
    {
        loaded = true;
        auto u32 = LoadLibraryW(L"user32.dll");
        auto gdi = LoadLibraryW(L"gdi32.dll");
        if (u32)
        {
            pGetDC = reinterpret_cast<GetDCFn>(GetProcAddress(u32, "GetDC"));
            pReleaseDC = reinterpret_cast<ReleaseDCFn>(GetProcAddress(u32, "ReleaseDC"));
        }
        if (gdi) pGetPixel = reinterpret_cast<GetPixelFn>(GetProcAddress(gdi, "GetPixel"));
    }
    if (!pGetDC || !pGetPixel || !pReleaseDC)
    {
        fprintf(f, "%-6s %-38s unavailable\n", tag, "pixel sample");
        return;
    }

    struct Sample { const char* name; int x; int y; };
    Sample pts[] = {
        { "red-marker", winX + winW - 90,  winY + winH - 90  },  // 纯红对照块中心
        { "panel-0",    winX + 260,        winY + 195        },  // 运行时改过 Offset/Size 的面板
        { "panel-1",    winX + 470,        winY + 110        },  // 直接共享 backdrop 的面板
        { "gap",        winX + 310,        winY + 110        },  // 面板之间（不该有材质）
        { "empty",      winX + 20,         winY + winH - 200 },  // 窗口内无 visual 区
    };

    HDC dc = pGetDC(nullptr);
    if (!dc) return;
    for (auto const& p : pts)
    {
        COLORREF c = pGetPixel(dc, p.x, p.y);
        if (c == CLR_INVALID)
            fprintf(f, "%-6s PIXEL %-9s %-10s invalid\n", tag, phase, p.name);
        else
            fprintf(f, "%-6s PIXEL %-9s %-10s rgb=(%3u,%3u,%3u)\n", tag, phase, p.name,
                static_cast<unsigned>(GetRValue(c)),
                static_cast<unsigned>(GetGValue(c)),
                static_cast<unsigned>(GetBValue(c)));
    }
    pReleaseDC(nullptr, dc);
    fflush(f);
}

struct ProbeOutcome
{
    int panels = 0;
    bool targetOk = false;
    bool sharedBrushOk = false;
    bool runtimeMoveOk = false;
    bool pixelBaseline = false;
};

// 在指定 HWND 上搭一棵「唯一样本源 + N 个独立裁剪面板」的合成树。
// 函数返回后 Compositor / Target / root 会被析构，所以像素采样必须留在函数内部完成。
static ProbeOutcome BuildMultiPanelTree(FILE* f, const char* tag, HWND hwnd,
    int winX, int winY, int winW, int winH, int panelCount, bool showWindow)
{
    ProbeOutcome out;
    if (!hwnd) return out;
    if (showWindow) ProbeRecordPixels(f, tag, "baseline", winX, winY, winW, winH);
    if (showWindow)
    {
        // layered 窗口不显式给 alpha 时默认全透明；产品走 NOREDIRECTIONBITMAP 不需要这步，
        // 但探针要确认「窗口真的上屏」就得显式设成不透明。
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);   // NOACTIVATE：绝不抢应用焦点
        Sleep(100);
        fprintf(f, "%-6s %-38s visible=%d\n", tag, "IsWindowVisible",
            IsWindowVisible(hwnd) ? 1 : 0);
        fflush(f);
    }

    try
    {
        WUC::Compositor c;
        auto interop = c.as<ABI_WUC::Desktop::ICompositorDesktopInterop>();
        winrt::com_ptr<IUnknown> raw;
        HRESULT hr = interop->CreateDesktopWindowTarget(hwnd, FALSE,
            reinterpret_cast<ABI_WUC::Desktop::IDesktopWindowTarget**>(raw.put()));
        ProbeLine(f, tag, "CreateDesktopWindowTarget", hr);
        if (FAILED(hr)) return out;
        out.targetOk = true;

        WUC::CompositionTarget target{ nullptr };
        hr = raw->QueryInterface(winrt::guid_of<WUC::CompositionTarget>(), winrt::put_abi(target));
        ProbeLine(f, tag, "QueryInterface(CompositionTarget)", hr);
        if (FAILED(hr)) return out;

        auto root = c.CreateContainerVisual();
        root.Size({ static_cast<float>(winW), static_cast<float>(winH) });
        target.Root(root);
        ProbeLine(f, tag, "ContainerVisual + Target.Root", S_OK);

        // ⭐ 全体面板共用的唯一样本源
        auto backdrop = c.CreateHostBackdropBrush();
        ProbeLine(f, tag, "CreateHostBackdropBrush (shared)", S_OK);

        // ⭐ 同一个 effect brush 给所有面板（磨砂路径）；单节点 + Backdrop 直连是本机唯一可用写法
        auto param = WUC::CompositionEffectSourceParameter(L"Backdrop");
        WGE::IGraphicsEffectSource src{ nullptr };
        winrt::copy_from_abi(src, winrt::get_abi(param));
        auto blur = winrt::make_self<BlurEffect>(src);
        blur->amount = 32.0f;
        WUC::CompositionEffectBrush sharedBrush{ nullptr };
        HRESULT fx = S_OK;
        try
        {
            auto factory = c.CreateEffectFactory(blur.as<WGE::IGraphicsEffect>());
            sharedBrush = factory.CreateBrush();
            sharedBrush.SetSourceParameter(L"Backdrop", backdrop);
        }
        catch (winrt::hresult_error const& e) { fx = e.code(); }
        catch (...) { fx = E_FAIL; }
        ProbeLine(f, tag, "shared effect brush (single blur)", fx);
        out.sharedBrushOk = SUCCEEDED(fx);

        WUC::SpriteVisual first{ nullptr };
        WUC::CompositionRoundedRectangleGeometry firstGeo{ nullptr };
        for (int i = 0; i < panelCount; ++i)
        {
            try
            {
                auto v = c.CreateSpriteVisual();
                float w = 260.0f, h = 140.0f;
                float x = 40.0f + static_cast<float>(i % 4) * 300.0f;
                float y = 40.0f + static_cast<float>(i / 4) * 180.0f;
                v.Offset({ x, y, 0.0f });
                v.Size({ w, h });
                // 一半走共享 effect brush，一半直接共享 backdrop（分区 blur<=0 路径）
                if (i % 2 == 0 && out.sharedBrushOk) v.Brush(sharedBrush);
                else v.Brush(backdrop);
                auto geo = c.CreateRoundedRectangleGeometry();
                geo.Size({ w, h });
                geo.CornerRadius({ 20.0f, 20.0f });
                v.Clip(c.CreateGeometricClip(geo));   // ⭐ 每个 visual 独立裁剪
                root.Children().InsertAtTop(v);
                if (i == 0) { first = v; firstGeo = geo; }
                ++out.panels;
            }
            catch (winrt::hresult_error const& e)
            {
                ProbeLine(f, tag, "panel[i] failed", e.code());
                break;
            }
            catch (...) { ProbeLine(f, tag, "panel[i] failed", E_FAIL); break; }
        }
        fprintf(f, "%-6s %-38s %d/%d\n", tag, "panels (shared brush + own clip)",
            out.panels, panelCount);
        fflush(f);

        // 位置跟随：只改 visual / geometry 属性，完全不碰 HWND
        if (out.panels > 0 && first)
        {
            HRESULT mv = S_OK;
            try
            {
                first.Offset({ 120.0f, 120.0f, 0.0f });
                first.Size({ 280.0f, 150.0f });
                if (firstGeo) firstGeo.Size({ 280.0f, 150.0f });
            }
            catch (winrt::hresult_error const& e) { mv = e.code(); }
            catch (...) { mv = E_FAIL; }
            ProbeLine(f, tag, "runtime Offset/Size update", mv);
            out.runtimeMoveOk = SUCCEEDED(mv);
        }

        // 红色对照块：确认合成树确实上屏（排除 layered 窗口全透明导致的假阳性）
        if (showWindow)
        {
            HRESULT mk = S_OK;
            try
            {
                auto marker = c.CreateSpriteVisual();
                marker.Offset({ static_cast<float>(winW - 140), static_cast<float>(winH - 140), 0.0f });
                marker.Size({ 100.0f, 100.0f });
                marker.Brush(c.CreateColorBrush(WUI::Colors::Red()));
                root.Children().InsertAtTop(marker);
            }
            catch (winrt::hresult_error const& e) { mk = e.code(); }
            catch (...) { mk = E_FAIL; }
            ProbeLine(f, tag, "red marker visual", mk);
        }

        fprintf(f, "%-6s %-38s %u\n", tag, "root.Children.Count",
            static_cast<unsigned>(root.Children().Count()));
        fflush(f);

        if (showWindow)
        {
            Sleep(250);                                  // 等 DWM 出一帧
            ProbeRecordPixels(f, tag, "shown", winX, winY, winW, winH);
            ShowWindow(hwnd, SW_HIDE);
            out.pixelBaseline = true;
        }
    }
    catch (winrt::hresult_error const& e) { ProbeLine(f, tag, "EXCEPTION", e.code()); }
    catch (...) { ProbeLine(f, tag, "EXCEPTION", E_FAIL); }
    return out;
}

static HRESULT RunMultiPanelProbe(int mode)
{
    wchar_t tmp[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH, tmp)) return E_FAIL;
    wcscat_s(tmp, L"zonex_multipanel_probe.txt");
    FILE* f = nullptr;
    _wfopen_s(&f, tmp, L"w");
    if (!f) return E_FAIL;

    const bool showWindow = mode != 0;
    fprintf(f, "Zonex scheme-4 probe: 13 fences -> 1 fullscreen visual layer\n");
    fprintf(f, "Diagnostic only. No product code path is touched.\n");
    fprintf(f, "mode=%d (%s)\n\n", mode, showWindow ? "show window + pixel sample" : "hidden, API only");

    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    fprintf(f, "virtual desktop: x=%d y=%d w=%d h=%d\n\n", vx, vy, vw, vh);

    // ctypes 宿主进程没有 COM 套间；已初始化时 init_apartment 会抛，忽略即可
    try { winrt::init_apartment(winrt::apartment_type::single_threaded); }
    catch (...) { fprintf(f, "base   init_apartment (already initialized, ignored)\n"); }
    try { EnsureDispatcher(); ProbeLine(f, "base", "DispatcherQueueController", S_OK); }
    catch (winrt::hresult_error const& e)
    {
        ProbeLine(f, "base", "DispatcherQueueController", e.code());
        fclose(f);
        return e.code();
    }
    catch (...) { fclose(f); return E_FAIL; }

    static const wchar_t* kClass = L"ZonexMultiPanelProbe";
    static bool classReady = false;
    if (!classReady)
    {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = kClass;
        classReady = RegisterClassW(&wc) != 0;
    }
    auto makeWindow = [&](int x, int y, int w, int h) -> HWND
    {
        return CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE
                | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT,
            kClass, L"", WS_POPUP, x, y, w, h,
            nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    };

    HWND full = makeWindow(vx, vy, vw, vh);
    ProbeLine(f, "full", "CreateWindowEx (virtual desktop)",
        full ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
    HWND ctrlWin = makeWindow(vx + 50, vy + 50, 688, 464);
    ProbeLine(f, "small", "CreateWindowEx (688x464 control)",
        ctrlWin ? S_OK : HRESULT_FROM_WIN32(GetLastError()));

    auto setDwm = [&](const char* tag, HWND h)
    {
        if (!h) return;
        auto fn = ProbeDwmSetAttr();
        if (!fn) { ProbeLine(f, tag, "DwmSetWindowAttribute", E_NOTIMPL); return; }
        BOOL on = TRUE;
        ProbeLine(f, tag, "USE_HOSTBACKDROPBRUSH(17)", fn(h, 17, &on, sizeof(on)));
        int noRound = 1;   // DWMWCP_DONOTROUND
        ProbeLine(f, tag, "WINDOW_CORNER_PREFERENCE(33)", fn(h, 33, &noRound, sizeof(noRound)));
        DWORD noBorder = 0xFFFFFFFEu;
        ProbeLine(f, tag, "BORDER_COLOR(34)", fn(h, 34, &noBorder, sizeof(noBorder)));
        BOOL noAnim = TRUE;
        ProbeLine(f, tag, "TRANSITIONS_FORCEDISABLED(3)", fn(h, 3, &noAnim, sizeof(noAnim)));
    };
    setDwm("full", full);
    setDwm("small", ctrlWin);
    fputc('\n', f);

    ProbeOutcome a = BuildMultiPanelTree(f, "full", full, vx, vy, vw, vh, 13, showWindow);
    fputc('\n', f);
    ProbeOutcome b = BuildMultiPanelTree(f, "small", ctrlWin, vx + 50, vy + 50, 688, 464, 13, false);
    fputc('\n', f);

    auto verdict = [&](const char* name, bool ok) {
        fprintf(f, "VERDICT %-34s %s\n", name, ok ? "PASS" : "FAIL");
    };
    verdict("P1 fullscreen DesktopWindowTarget", a.targetOk);
    verdict("P1b small-window control", b.targetOk);
    verdict("P2 single shared backdrop+effect", a.sharedBrushOk && b.sharedBrushOk);
    verdict("P3 13 panels with own clip", a.panels == 13 && b.panels == 13);
    verdict("P4 runtime Offset/Size (no SWP)", a.runtimeMoveOk && b.runtimeMoveOk);
    fflush(f);

    if (ctrlWin) DestroyWindow(ctrlWin);
    if (full) DestroyWindow(full);
    fclose(f);
    return S_OK;
}

// ---------------------------------------------------------------- 统一桌面视觉层（方案④）
// 与 GlassHost 并存的另一套实例模型：一个全屏 HWND + 一个 Compositor + 一个共享
// CompositionBackdropBrush，N 个分区各占一个 Panel（自己的 Offset/Size/GeometricClip/投影 mask）。
// ⭐ 位置跟随只改 visual.Offset/Size/geometry.Offset，**绝不 SetWindowPos**：
//    DWM 不会重采 backdrop、不会发 WM_WINDOWPOSCHANGED，Z 序目标从「13 组交错」塌缩成 1 个。
// ⭐ 材质/透传 visual 恒铺满整窗、Offset=0，靠各自圆角几何 Offset(x,y) 裁出卡片矩形 ——
//    与单宿主里「material 铺满整窗 + materialClip 内缩」完全同一套坐标语义，
//    避开 HostBackdropBrush 采样坐标系的歧义。窗口背后只有壁纸，面板之间不会串采。
struct GlassPanel
{
    WUC::SpriteVisual passthrough{ nullptr };
    WUC::SpriteVisual material{ nullptr };
    WUC::CompositionRoundedRectangleGeometry clip{ nullptr };
    WUC::SpriteVisual shadowBig{ nullptr };
    WUC::SpriteVisual shadowSmall{ nullptr };
    WUC::DropShadow shadowBigFx{ nullptr };
    WUC::DropShadow shadowSmallFx{ nullptr };
    WUC::CompositionRoundedRectangleGeometry maskGeo{ nullptr };
    WUC::ShapeVisual maskVisual{ nullptr };
    WUC::CompositionVisualSurface maskSurface{ nullptr };
    float x = 0.f, y = 0.f, w = 0.f, ht = 0.f;
    bool visible = true;
};

struct GlassLayer
{
    WUC::Compositor compositor{ nullptr };
    WUC::CompositionTarget target{ nullptr };
    WUC::ContainerVisual root{ nullptr };
    WUC::CompositionBackdropBrush backdrop{ nullptr };
    WUC::CompositionEffectBrush blurBrush{ nullptr };
    std::unordered_map<int, GlassPanel> panels;
    int nextId = 1;
    float winW = 0.f, winH = 0.f;
    float radius = 20.f, blur = 0.f, margin = 0.f;
    // 方案⑥：材质链多出来的一个饱和度节点（1.0 = 不改变）。以前以为「合成器只接受单节点」，
    // 是错的 —— 真因是**节点重名**。探针 A 证明 blur(b1)->blur(b2) 换个唯一名就能过工厂。
    float saturation = 1.0f;
    float shadowOpacity = 0.30f, shadowBlur = 48.f, shadowOffsetY = 2.f;
};

static void ApplyLayerPanelRect(GlassLayer* l, GlassPanel& p)
{
    const float2 sz{ p.w, p.ht };
    const float3 at{ p.x, p.y, 0.0f };
    if (p.passthrough) { p.passthrough.Size({ l->winW, l->winH }); p.passthrough.Offset({ 0.0f, 0.0f, 0.0f }); }
    if (p.material) { p.material.Size({ l->winW, l->winH }); p.material.Offset({ 0.0f, 0.0f, 0.0f }); }
    if (p.clip) { p.clip.Size(sz); p.clip.Offset({ p.x, p.y }); }
    if (p.shadowBig) { p.shadowBig.Size(sz); p.shadowBig.Offset(at); }
    if (p.shadowSmall) { p.shadowSmall.Size(sz); p.shadowSmall.Offset(at); }
    if (p.maskGeo) p.maskGeo.Size(sz);
    if (p.maskVisual) p.maskVisual.Size(sz);
    if (p.maskSurface) p.maskSurface.SourceSize(sz);
}

static void ApplyLayerPanelVisibility(GlassLayer* l, GlassPanel& p)
{
    const bool on = p.visible && p.w > 0.f && p.ht > 0.f;
    if (p.passthrough) p.passthrough.IsVisible(on && l->blur <= 0.0f);
    if (p.material) p.material.IsVisible(on && l->blur > 0.0f && l->blurBrush != nullptr);
    const bool shadowOn = on && l->shadowOpacity > 0.001f;
    if (p.shadowBig) p.shadowBig.IsVisible(shadowOn);
    if (p.shadowSmall) p.shadowSmall.IsVisible(shadowOn);
}

static void ApplyLayerShadow(GlassLayer* l, GlassPanel& p)
{
    if (p.shadowBigFx)
    {
        p.shadowBigFx.BlurRadius(fmaxf(0.0f, l->shadowBlur));
        p.shadowBigFx.Offset({ 0.0f, l->shadowOffsetY, 0.0f });
        p.shadowBigFx.Opacity(l->shadowOpacity);
    }
    if (p.shadowSmallFx)
    {
        p.shadowSmallFx.BlurRadius(fmaxf(1.0f, l->shadowBlur * kContactBlurRatio));
        p.shadowSmallFx.Offset({ 0.0f, l->shadowOffsetY * kContactOffsetRatio, 0.0f });
        p.shadowSmallFx.Opacity(l->shadowOpacity * kContactOpacityRatio);
    }
}

static void ApplyLayerGlobal(GlassLayer* l)
{
    const float2 r{ l->radius, l->radius };
    for (auto& kv : l->panels)
    {
        auto& p = kv.second;
        if (p.clip) p.clip.CornerRadius(r);
        if (p.maskGeo) p.maskGeo.CornerRadius(r);
        ApplyLayerShadow(l, p);
        ApplyLayerPanelRect(l, p);
        ApplyLayerPanelVisibility(l, p);
    }
}

// 一个模糊 effect brush 给所有 panel 共用（探针 P2 已实证多 visual 可共享）。
//
// ⭐ 方案⑥：材质链现在是「饱和度 → 高斯模糊」两个节点。
// 之前记忆里的结论「合成器只接受单节点 + Backdrop 直连」是**错的**：Blur→Blur 串联失败
// 的真实原因是两个节点**同名**（都叫 ZonexBlur），WUC 用节点名做图里的键。
// 唯一命名后两节点/三节点图都能建（smoke/effect_probe2.py 实证 A/C 两组 ok）。
// 唯一仍然不支持的是 CLSID_D2D1Crop（D/E/F 全 E_INVALIDARG）——
// 所以「先裁再模糊」依旧做不了，采样域仍由窗口矩形决定。
//
// 回退：两节点图万一在某台机器上建不出来，退回单 blur，保证玻璃还在（宁可少一道调色）。
static bool BuildLayerBlurBrush(GlassLayer* l, bool withSaturation)
{
    try
    {
        auto param = WUC::CompositionEffectSourceParameter(L"Backdrop");
        WGE::IGraphicsEffectSource src{ nullptr };
        winrt::copy_from_abi(src, winrt::get_abi(param));

        const float amount = l->blur > 0.0f ? l->blur : 32.0f;
        WGE::IGraphicsEffectSource blurInput = src;
        if (withSaturation && fabsf(l->saturation - 1.0f) > 0.001f)
        {
            auto sat = winrt::make_self<SaturationEffect>(src);
            sat->amount = l->saturation;
            sat->Name(L"ZonexSat");
            blurInput = sat.as<WGE::IGraphicsEffectSource>();
        }

        auto blur = winrt::make_self<BlurEffect>(blurInput);
        blur->amount = amount;
        blur->Name(L"ZonexBlur");

        auto factory = l->compositor.CreateEffectFactory(blur.as<WGE::IGraphicsEffect>());
        auto brush = factory.CreateBrush();
        brush.SetSourceParameter(L"Backdrop", l->backdrop);
        l->blurBrush = brush;
        for (auto& kv : l->panels)
            if (kv.second.material) kv.second.material.Brush(brush);
        return true;
    }
    catch (...) { l->blurBrush = nullptr; return false; }
}

static bool BuildLayerBlurBrush(GlassLayer* l)
{
    if (BuildLayerBlurBrush(l, true)) return true;
    if (l->saturation != 1.0f) { l->saturation = 1.0f; return BuildLayerBlurBrush(l, false); }
    return false;
}

static int CreateLayerPanel(GlassLayer* l)
{
    if (!l || !l->root) return -1;
    try
    {
        GlassPanel p;
        auto& c = l->compositor;
        const float2 one{ 1.0f, 1.0f };

        // 插入顺序 = Z 序（后插在上）：shadow → passthrough → material，与单宿主保持一致
        p.maskGeo = c.CreateRoundedRectangleGeometry();
        p.maskGeo.CornerRadius({ l->radius, l->radius });
        p.maskGeo.Size(one);
        p.maskVisual = c.CreateShapeVisual();
        auto shape = c.CreateSpriteShape(p.maskGeo);
        shape.FillBrush(c.CreateColorBrush(WUI::Colors::White()));
        p.maskVisual.Shapes().Append(shape);
        p.maskSurface = c.CreateVisualSurface();
        p.maskSurface.SourceVisual(p.maskVisual);
        p.maskSurface.SourceSize(one);
        auto maskBrush = c.CreateSurfaceBrush(p.maskSurface);
        for (int i = 0; i < 2; ++i)
        {
            auto fx = c.CreateDropShadow();
            fx.Mask(maskBrush);
            fx.Color(WUI::Colors::Black());
            auto v = c.CreateSpriteVisual();
            v.Shadow(fx);
            l->root.Children().InsertAtTop(v);
            if (i == 0) { p.shadowBig = v; p.shadowBigFx = fx; }
            else { p.shadowSmall = v; p.shadowSmallFx = fx; }
        }

        p.clip = c.CreateRoundedRectangleGeometry();
        p.clip.CornerRadius({ l->radius, l->radius });
        p.clip.Size(one);

        p.passthrough = c.CreateSpriteVisual();
        p.passthrough.Brush(l->backdrop);
        p.passthrough.Clip(c.CreateGeometricClip(p.clip));
        l->root.Children().InsertAtTop(p.passthrough);

        p.material = c.CreateSpriteVisual();
        if (l->blurBrush) p.material.Brush(l->blurBrush);
        else p.material.Brush(l->backdrop);
        p.material.Clip(c.CreateGeometricClip(p.clip));
        l->root.Children().InsertAtTop(p.material);

        const int id = l->nextId++;
        l->panels.emplace(id, std::move(p));
        auto& stored = l->panels[id];
        ApplyLayerShadow(l, stored);
        ApplyLayerPanelRect(l, stored);
        ApplyLayerPanelVisibility(l, stored);
        return id;
    }
    catch (...) { return -1; }
}

extern "C"
{
    // 自检入口（可重复调用）：把几种裁剪写法逐个试一遍，结果写 %TEMP%\zonex_effect_probe.txt
    __declspec(dllexport) void __stdcall ZonexGlassRunEffectProbe()
    {
        RunEffectProbeVariants();
    }
    // 方案⑥ 探针（第二轮）：分离「节点重名」与「Rect 属性正确组合」两个变量，
    // 并补测 Saturation 节点。结果写 %TEMP%\zonex_effect_probe2.txt。
    __declspec(dllexport) void __stdcall ZonexGlassRunEffectProbe2()
    {
        RunEffectProbe2Once();
    }
    // 方案④可行性探针（产品路径不调用）：mode=0 不显示窗口、纯 API 验证；mode=1 额外抓像素。
    // 结果写 %TEMP%\zonex_multipanel_probe.txt。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassRunMultiPanelProbe(int mode)
    {
        return RunMultiPanelProbe(mode);
    }
    // radiusDip：面板圆角（DIP）；blurAmount：模糊半径（DIP）；marginDip：窗口比面板大出来的部分（放阴影）
    __declspec(dllexport) HRESULT __stdcall ZonexGlassAttach(HWND hwnd, float radiusDip, float blurAmount,
        float marginDip, void** out)
    {
        if (!hwnd || !out) return E_INVALIDARG;
        *out = nullptr;
        try
        {
            EnsureDispatcher();
            ProbeEffectVariantsOnce();   // 一次性自检：把几种裁剪写法逐个试出来（结果写 %TEMP%）

            auto* host = new GlassHost();
            host->radius = radiusDip;
            host->margin = marginDip;
            host->compositor = WUC::Compositor();

            auto interop = host->compositor.as<ABI_WUC::Desktop::ICompositorDesktopInterop>();
            winrt::com_ptr<IUnknown> raw;
            winrt::check_hresult(interop->CreateDesktopWindowTarget(hwnd, FALSE,
                reinterpret_cast<ABI_WUC::Desktop::IDesktopWindowTarget**>(raw.put())));
            winrt::check_hresult(raw->QueryInterface(winrt::guid_of<WUC::CompositionTarget>(),
                winrt::put_abi(host->target)));

            host->root = host->compositor.CreateContainerVisual();
            host->clip = host->compositor.CreateRoundedRectangleGeometry();
            host->clip.CornerRadius({ radiusDip, radiusDip });
            host->root.Size({ 1.0f, 1.0f });

            // 阴影：圆角矩形 mask → 合成投影，落在窗口留出的 margin 里
            if (marginDip > 0)
            {
                host->maskGeo = host->compositor.CreateRoundedRectangleGeometry();
                host->maskGeo.CornerRadius({ radiusDip, radiusDip });
                host->maskVisual = host->compositor.CreateShapeVisual();
                auto shape = host->compositor.CreateSpriteShape(host->maskGeo);
                shape.FillBrush(host->compositor.CreateColorBrush(WUI::Colors::White()));
                host->maskVisual.Shapes().Append(shape);
                host->maskSurface = host->compositor.CreateVisualSurface();
                host->maskSurface.SourceVisual(host->maskVisual);
                auto maskBrush = host->compositor.CreateSurfaceBrush(host->maskSurface);

                for (int i = 0; i < 2; i++)
                {
                    auto shadow = host->compositor.CreateDropShadow();
                    shadow.Mask(maskBrush);
                    shadow.Color(WUI::Colors::Black());
                    shadow.BlurRadius(i == 0 ? host->shadowBlur : (host->shadowBlur * kContactBlurRatio));
                    shadow.Offset({ 0.0f, i == 0 ? host->shadowOffsetY : (host->shadowOffsetY * kContactOffsetRatio), 0.0f });
                    shadow.Opacity(i == 0 ? host->shadowOpacity : (host->shadowOpacity * kContactOpacityRatio));
                    auto visual = host->compositor.CreateSpriteVisual();
                    visual.Shadow(shadow);
                    host->root.Children().InsertAtTop(visual);
                    if (i == 0) { host->shadowBig = visual; host->shadowBigFx = shadow; }
                    else { host->shadowSmall = visual; host->shadowSmallFx = shadow; }
                }
            }

            // 透传层：仅分区路径（blur<=0，磨砂由卡片自己的 AcrylicBlur 承担）。
            // 影子 visual 在本窗口的整个渲染里会给面板矩形**内部**也铺上近全黑的模糊剪影，
            // 半透明卡片的 AcrylicBlur 采到的「背后画面」就含这层黑 → 卡片发暗发灰。
            // 盖一个圆角裁剪的 HostBackdropBrush visual：卡片矩形内部显示宿主背后的实时
            // 原画面（= 没有本窗口时卡片本来该看到的东西），投影只在面板矩形外可见。
            // 注意：这里不用 MaskBrush 包 HostBackdropBrush —— backdrop 画刷只允许作为
            // effect 的 source 参数，塞进 MaskBrush.Source 会直接抛异常（实测阴影整个消失）。
            // 本 visual 自身没有 Shadow，用 Clip 切圆角不会误伤投影。
            if (blurAmount <= 0.0f)
            {
                host->passthrough = host->compositor.CreateSpriteVisual();
                host->passthrough.Brush(host->compositor.CreateHostBackdropBrush());
                host->passthrough.Clip(host->compositor.CreateGeometricClip(host->clip));
                host->root.Children().InsertAtTop(host->passthrough);
            }

            // 材质：HostBackdropBrush（窗口背后的实时画面）→ 高斯模糊 → 圆角几何裁剪。
            // blurAmount<=0 = **只要投影，不要磨砂**（桌面分区卡片走这条路：卡片自己的半透明底
            // 已经承担观感，多一层 HostBackdropBrush 只是白烧 GPU，十几个分区叠起来更明显）。
            if (blurAmount > 0.0f)
            {
                host->materialBlur = blurAmount;
                host->materialClip = host->compositor.CreateRoundedRectangleGeometry();
                host->materialClip.CornerRadius({ radiusDip, radiusDip });
                host->materialClip.Size({ 1.0f, 1.0f });
                host->materialClip.Offset({ marginDip, marginDip });
                host->material = host->compositor.CreateSpriteVisual();
                host->material.Size({ 1.0f, 1.0f });
                host->material.Clip(host->compositor.CreateGeometricClip(host->materialClip));
                host->root.Children().InsertAtTop(host->material);
                // 此时面板尺寸还没同步（C# 随后调 ZonexGlassResize）→ 先不建 effect 图，
                // 由 ApplySize 里的尺寸变化分支补建（裁剪矩形必须用真实矩形）。
            }

            host->target.Root(host->root);
            ApplyContentVisible(host);

            *out = host;
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    __declspec(dllexport) HRESULT __stdcall ZonexGlassResize(void* handle, float widthDip, float heightDip)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try { ApplySize(host, widthDip, heightDip); return S_OK; }
        catch (...) { return winrt::to_hresult(); }
    }

    // 布局定稿：折叠/展开动画走完时调一次。
    // C# 侧的尺寸同步有 0.5 DIP 去抖，最后一帧的微小变化会被吃掉；再加上动画期间偶发一次
    // CreateEffectFactory 失败，就会留下「Crop 停在旧矩形」的分区（背景一直按更大的区域采样 =
    // 卡片外的亮壁纸被糊进来，表现为折叠后某一条背景发亮）。这里用最终尺寸无条件重建一次兜住。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassCommitSize(void* handle)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host || host->width <= 0.0f || host->height <= 0.0f) return S_FALSE;
        try
        {
            ApplySize(host, host->width, host->height);   // 先把几何按当前尺寸对齐
            if (host->material && host->materialBlur > 0.0f) RebuildMaterial(host);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    // 诊断：0 = 正常（裁剪靠重建图） 1 = 待重试（重建失败，沿用旧图）
    //       2 = 无裁剪（margin=0 时的正常态；margin>0 时背景会偏亮）
    //       3 = 正常且裁剪靠运行时属性  4 = **根本没有材质**（毛玻璃没建出来，画面会没有模糊）
    __declspec(dllexport) int __stdcall ZonexGlassMaterialState(void* handle)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return -1;
        if (!host->material || !host->materialBrush) return 4;
        if (!host->cropSupported) return 2;
        if (host->materialDirty) return 1;
        return host->cropViaProperty ? 3 : 0;
    }

    // 诊断：上次重建材质失败的 HRESULT（0 = 没失败过）
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLastMaterialError(void* handle)
    {
        auto* host = static_cast<GlassHost*>(handle);
        return host ? host->lastMaterialErr : E_INVALIDARG;
    }

    __declspec(dllexport) HRESULT __stdcall ZonexGlassSetRadius(void* handle, float radiusDip)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try { host->radius = radiusDip; ApplyRadius(host, radiusDip); return S_OK; }
        catch (...) { return winrt::to_hresult(); }
    }

    // 染色/提亮：叠在模糊画面上的颜色（ARGB，A 为浓度）
    __declspec(dllexport) HRESULT __stdcall ZonexGlassSetTint(void* handle, uint32_t tintArgb, uint32_t glowArgb)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try
        {
            const auto tintColor = Argb(tintArgb);
            if (!host->tint && tintColor.A > 0)
            {
                host->tint = host->compositor.CreateSpriteVisual();
                host->tint.Clip(host->compositor.CreateGeometricClip(host->clip));
                host->tint.Brush(host->compositor.CreateColorBrush(tintColor));
                host->root.Children().InsertAtTop(host->tint);
                ApplySize(host, host->width, host->height);
            }
            else if (host->tint)
            {
                host->tint.Brush(host->compositor.CreateColorBrush(tintColor));
                host->tint.IsVisible(tintColor.A > 0);
            }

            if (glowArgb == 0) return S_OK;
            const auto glowColor = Argb(glowArgb);
            if (!host->glow)
            {
                host->glow = host->compositor.CreateSpriteVisual();
                host->glow.Clip(host->compositor.CreateGeometricClip(host->clip));
                host->glow.Brush(host->compositor.CreateColorBrush(glowColor));
                host->root.Children().InsertAtTop(host->glow);
                ApplySize(host, host->width, host->height);
            }
            else
            {
                host->glow.Brush(host->compositor.CreateColorBrush(glowColor));
                host->glow.IsVisible(glowColor.A > 0);
            }
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    // 投影参数：opacity 0–1（浓度）、blurDip 0–64（模糊半径）、offsetYDip 0–32（纵向偏移）。
    // 只改已有 DropShadow 的属性，不重建合成树 → 拖动滑条可以做到逐帧跟手。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassSetShadow(void* handle, float opacity, float blurDip,
        float offsetYDip)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try
        {
            host->shadowOpacity = fminf(1.0f, fmaxf(0.0f, opacity));
            host->shadowBlur = fminf(64.0f, fmaxf(0.0f, blurDip));
            host->shadowOffsetY = fminf(32.0f, fmaxf(0.0f, offsetYDip));
            ApplyShadow(host);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    // 强制重新采样「宿主背后的实时画面」。
    //
    // 为什么要这个：窗口被别的窗口完全盖住（最大化别的程序 / 切到全屏游戏）时，DWM 会停掉
    // HostBackdropBrush 的采样；重新露出来后它不保证重新采 —— 玻璃就冻结在「被盖住那一刻」
    // 的画面。表现是「某个分区背景莫名其妙变亮/变花，像糊了别的窗口的内容」，且只有个别分区
    // （每个窗口被降级的时机不同），再动一次窗口又好了。
    // 重建 effect 图 = 换一张新的 HostBackdropBrush，逼 DWM 丢掉旧采样。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassRefreshBackdrop(void* handle)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try
        {
            if (host->materialBlur <= 0.0f || !host->material) return S_FALSE;
            // 这个接口的语义就是「强制重采」，必须丢弃旧采样源；复用 brush 等于什么都没做
            host->backdrop = nullptr;
            RebuildMaterial(host);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    // 内容可见性总开关（窗口保持可见，只摘掉磨砂/透传/投影）。
    //
    // 为什么需要：组件被 cloak / 显隐联动收起时，以前直接 SW_HIDE 宿主窗口 → DWM 丢掉
    // HostBackdropBrush 采样 → 恢复时那一帧玻璃是空的（用户看到「模糊闪一下」）。
    // 现在窗口不隐藏，只关内容：DWM 继续采样，恢复时打开内容立即是磨砂。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassSetContentVisible(void* handle, int visible)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return E_INVALIDARG;
        try
        {
            host->contentVisible = (visible != 0);
            ApplyContentVisible(host);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    __declspec(dllexport) void __stdcall ZonexGlassDetach(void* handle)
    {
        auto* host = static_cast<GlassHost*>(handle);
        if (!host) return;
        try
        {
            if (host->target) host->target.Root(nullptr);
            host->material = nullptr;
            host->materialClip = nullptr;
            host->materialBrush = nullptr;
            host->materialFactory = nullptr;
            host->backdrop = nullptr;
            host->materialDirty = false;
            host->tint = nullptr;
            host->glow = nullptr;
            host->passthrough = nullptr;
            host->shadowBig = nullptr;
            host->shadowSmall = nullptr;
            host->shadowBigFx = nullptr;
            host->shadowSmallFx = nullptr;
            host->maskSurface = nullptr;
            host->maskVisual = nullptr;
            host->root = nullptr;
            host->target = nullptr;
            host->compositor = nullptr;
        }
        catch (...) {}
        delete host;
    }

    // ------------------------------------------------ 统一桌面视觉层（方案④，产品路径）
    // 由 C# 的 DesktopGlassLayer 单例持有：一个全屏 HWND、一个 layer handle、N 个 panel id。
    // 所有改动都只写合成属性，绝不碰 HWND 的位置/Z 序。
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerCreate(HWND hwnd, void** out)
    {
        if (!hwnd || !out) return E_INVALIDARG;
        *out = nullptr;
        try
        {
            EnsureDispatcher();
            auto* l = new GlassLayer();
            l->compositor = WUC::Compositor();
            auto interop = l->compositor.as<ABI_WUC::Desktop::ICompositorDesktopInterop>();
            winrt::com_ptr<IUnknown> raw;
            HRESULT hr = interop->CreateDesktopWindowTarget(hwnd, FALSE,
                reinterpret_cast<ABI_WUC::Desktop::IDesktopWindowTarget**>(raw.put()));
            if (FAILED(hr)) { delete l; return hr; }
            raw->QueryInterface(winrt::guid_of<WUC::CompositionTarget>(), winrt::put_abi(l->target));
            l->root = l->compositor.CreateContainerVisual();
            l->target.Root(l->root);
            l->backdrop = l->compositor.CreateHostBackdropBrush();
            BuildLayerBlurBrush(l);
            *out = l;
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerSetSize(void* handle, float wDip, float hDip)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l || wDip <= 0 || hDip <= 0) return E_INVALIDARG;
        try
        {
            l->winW = wDip; l->winH = hDip;
            l->root.Size({ wDip, hDip });
            for (auto& kv : l->panels) ApplyLayerPanelRect(l, kv.second);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    /// <summary>全局外观参数（所有分区一致）：改一次应用到全部 panel。</summary>
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerSetGlobal(void* handle,
        float radiusDip, float blurDip, float marginDip,
        float shadowOpacity, float shadowBlurDip, float shadowOffsetYDip)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        try
        {
            l->radius = fmaxf(0.0f, radiusDip);
            l->blur = fmaxf(0.0f, blurDip);
            l->margin = fmaxf(0.0f, marginDip);
            l->shadowOpacity = fminf(1.0f, fmaxf(0.0f, shadowOpacity));
            l->shadowBlur = fminf(64.0f, fmaxf(0.0f, shadowBlurDip));
            l->shadowOffsetY = fminf(32.0f, fmaxf(0.0f, shadowOffsetYDip));
            ApplyLayerGlobal(l);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    /// <summary>方案⑥：设置材质链的饱和度（1.0 = 不改变）。会重建共享 effect brush。</summary>
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerSetSaturation(void* handle, float saturation)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        try
        {
            const float v = fminf(3.0f, fmaxf(0.0f, saturation));
            if (fabsf(v - l->saturation) < 0.001f) return S_OK;
            l->saturation = v;
            BuildLayerBlurBrush(l);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    /// <summary>新分区注册一个 panel，返回 id（失败 -1）。</summary>
    __declspec(dllexport) int __stdcall ZonexGlassLayerAddPanel(void* handle)
    {
        return CreateLayerPanel(static_cast<GlassLayer*>(handle));
    }

    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerRemovePanel(void* handle, int id)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        auto it = l->panels.find(id);
        if (it == l->panels.end()) return S_FALSE;
        try
        {
            auto& p = it->second;
            auto children = l->root.Children();
            if (p.material) children.Remove(p.material);
            if (p.passthrough) children.Remove(p.passthrough);
            if (p.shadowSmall) children.Remove(p.shadowSmall);
            if (p.shadowBig) children.Remove(p.shadowBig);
            l->panels.erase(it);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    /// <summary>位置跟随：只改 visual 属性，不 SetWindowPos。坐标是相对全屏窗口左上角的 DIP。</summary>
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerSetPanelRect(void* handle, int id,
        float xDip, float yDip, float wDip, float hDip)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        auto it = l->panels.find(id);
        if (it == l->panels.end()) return E_INVALIDARG;
        auto& p = it->second;
        // 几何去重：拖动时同一矩形会被多个事件反复送进来，重复写只会白烧合成器
        if (fabsf(p.x - xDip) < 0.1f && fabsf(p.y - yDip) < 0.1f
            && fabsf(p.w - wDip) < 0.1f && fabsf(p.ht - hDip) < 0.1f)
            return S_OK;
        try
        {
            p.x = xDip; p.y = yDip; p.w = wDip; p.ht = hDip;
            ApplyLayerPanelRect(l, p);
            ApplyLayerPanelVisibility(l, p);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerSetPanelVisible(void* handle, int id, int visible)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        auto it = l->panels.find(id);
        if (it == l->panels.end()) return E_INVALIDARG;
        try
        {
            const bool on = visible != 0;
            if (it->second.visible == on) return S_OK;
            it->second.visible = on;
            ApplyLayerPanelVisibility(l, it->second);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    /// <summary>强制重采：换掉共享采样源并重建共享模糊 brush，所有 panel 同步换笔刷。</summary>
    __declspec(dllexport) HRESULT __stdcall ZonexGlassLayerRefreshBackdrop(void* handle)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return E_INVALIDARG;
        try
        {
            l->backdrop = l->compositor.CreateHostBackdropBrush();
            for (auto& kv : l->panels)
                if (kv.second.passthrough) kv.second.passthrough.Brush(l->backdrop);
            BuildLayerBlurBrush(l);
            for (auto& kv : l->panels) ApplyLayerPanelVisibility(l, kv.second);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    __declspec(dllexport) void __stdcall ZonexGlassLayerDestroy(void* handle)
    {
        auto* l = static_cast<GlassLayer*>(handle);
        if (!l) return;
        try
        {
            l->root.Children().RemoveAll();
            l->panels.clear();
            if (l->target) l->target.Root(nullptr);
            l->blurBrush = nullptr;
            l->backdrop = nullptr;
            l->root = nullptr;
            l->target = nullptr;
            l->compositor = nullptr;
        }
        catch (...) {}
        delete l;
    }
}
