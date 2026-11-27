// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MiHomeHub.Services;

namespace MiHomeHub.Widgets;

/// <summary>
/// 贴桌面卡片的统一窗体外观：20px 大圆角 + 原生磨砂 + 投影（<see cref="GlassBackdropHost"/>），
/// 原生玻璃不可用时自动退回 8px DWM 圆角 + 窗口级亚克力。
///
/// 数据卡与设备分区共用这一份，窗口本身只负责落位、拖动和 Z 序守卫，
/// 圆角半径 / 模糊参数 / 投影浓度 / 回退策略都不要再在各窗口里另写一套。
/// </summary>
internal sealed class DesktopGlassFrame : IDisposable
{
    /// <summary>原生玻璃路径的大圆角（DIP）。DWM 自身最多 8，这里靠合成层切出 20。</summary>
    internal const int RadiusDip = 20;

    /// <summary>没有原生玻璃时的回退圆角：DWM 能做的最大值。</summary>
    private const int FallbackRadiusDip = 8;

    /// <summary>毛玻璃开启时的模糊半径（DIP）：和设置面板那套一致。</summary>
    private const float GlassBlurDip = 32f;

    private readonly Window _window;
    private readonly Border _card;
    private readonly Func<nint> _handle;
    private readonly string _tag;
    private readonly Action? _ownerRestored;

    private GlassBackdropHost? _host;
    private bool _failed;
    private bool? _loggedGlass;
    private bool _closed;
    /// <summary>拖动 / 拉伸降载中：磨砂与投影已摘掉，宿主窗口留在原地不再跟随。</summary>
    private bool _dragLite;

    /// <param name="window">卡片窗本身。</param>
    /// <param name="card">承载底色的 Border：圆角由这里统一写，保证和宿主裁切一致。</param>
    /// <param name="handle">当前窗口句柄（可能尚未创建，所以是委托）。</param>
    /// <param name="tag">日志前缀，例如 data-card / desktop-zone。</param>
    /// <param name="ownerRestored">窗口从最小化 / 显示桌面恢复后的回调（在 UI 线程）。</param>
    internal DesktopGlassFrame(Window window, Border card, Func<nint> handle, string tag,
        Action? ownerRestored = null)
    {
        _window = window;
        _card = card;
        _handle = handle;
        _tag = tag;
        _ownerRestored = ownerRestored;
    }

    /// <summary>原生玻璃是否真的可用：DLL 在、宿主也建起来了；否则整卡退回 8px + 亚克力。</summary>
    internal bool UsesNativeGlass => !_failed && _host != null && GlassBackdropHost.Available;

    /// <summary>建一次玻璃宿主（失败会记住，不再重试）。必须在打圆角之前调用。</summary>
    internal bool EnsureHost()
    {
        if (_host != null) return true;
        if (_failed || !GlassBackdropHost.Available)
        {
            _failed = true;
            return false;
        }

        try
        {
            _host = new GlassBackdropHost(48, GlassBlurDip, hookOwner: false, desktopLayer: false,
                cardWndProc: false)
            {
                // 卡片只借宿主的模糊与投影，不参与分区统一层的全局参数维护
                ApplyGlobalAppearance = false,
                OwnerRestored = () => Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) return;
                    Sync();
                    _ownerRestored?.Invoke();
                }),
            };
            _host.SetBlur(DesktopHost.Look.Glass ? GlassBlurDip : 0f);
            _host.SetShadow(0.30f, 48f, 18f);
            return true;
        }
        catch (Exception ex)
        {
            _host = null;
            _failed = true;
            DesktopLog.Write($"[{_tag}] 玻璃宿主创建失败，回退窗口级亚克力：{ex.Message}");
            return false;
        }
    }

    /// <summary>重打圆角 / 主题与材质：外观设置改动、尺寸变化、系统重合成后都会调用。</summary>
    internal void ApplyChrome()
    {
        if (_dragLite) return;   // 降载期间不动材质，松手后由 EndDragLite 统一补
        var h = _handle();
        if (h == nint.Zero) return;

        var look = DesktopHost.Look;
        _window.RequestedThemeVariant = DesktopPalette.IsLight(look) ? ThemeVariant.Light : ThemeVariant.Dark;
        // 底色 / 描边由 XAML 绑定到各窗口的 CardBrush / BorderBrush：
        // 外观一变 VM 立刻换画刷，卡面不用等这一次材质重打（那一步有去抖）。

        DesktopEmbed.EnsureNoActivate(h);
        DesktopEmbed.StripNoRedirection(h);
        DesktopEmbed.KillSystemFrame(h);
        DesktopEmbed.SuppressSystemShadow(h);

        if (UsesNativeGlass)
        {
            // 磨砂跟着「桌面组件外观」的毛玻璃开关：关掉时只留圆角与投影（blur=0），
            // 底色由 DesktopPalette 抬到近实心的一档来压住文字；重新打开会按 32 重建材质。
            _host!.SetBlur(look.Glass ? GlassBlurDip : 0f);
            if (_loggedGlass != look.Glass)
            {
                _loggedGlass = look.Glass;
                DesktopLog.Write($"[{_tag}] chrome glass={look.Glass} " +
                                 $"density={DesktopPalette.CardDensity(look):0.###}");
            }

            _card.CornerRadius = new CornerRadius(RadiusDip);
            DesktopEmbed.ClearAcrylic(h);
            DesktopEmbed.RoundCorners(h, 0);
        }
        else
        {
            // DLL 不可用 / 宿主没建起来：退回 8px + 窗口级亚克力，仍然是一张贴桌面卡片
            _card.CornerRadius = new CornerRadius(FallbackRadiusDip);
            if (look.Glass)
                DesktopEmbed.ApplyAcrylic(h, DesktopPalette.TintColor(look), DesktopPalette.TintOpacity(look));
            else
                DesktopEmbed.ClearAcrylic(h);
            if (!DesktopEmbed.RoundByDwm(h))
                DesktopEmbed.RoundCorners(h, FallbackRadiusDip);
            else
                DesktopEmbed.RoundCorners(h, 0);
        }

        Sync();
    }

    /// <summary>把卡片矩形同步给玻璃宿主：宿主自己会做几何去重，拖动里每帧调用也不会重采背景。</summary>
    internal bool Sync()
    {
        if (_closed || _host is null || _dragLite) return false;
        var h = _handle();
        if (h == nint.Zero) return false;

        if (!DesktopNative.IsWindowVisible(h))
        {
            _host.Hide();
            return false;
        }
        if (DesktopNative.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            _host.SuppressContent();
            return false;
        }

        if (!DesktopNative.GetWindowRect(h, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
        var scale = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        return _host.Sync(h, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            scale, RadiusDip, 0u);
    }

    /// <summary>卡窗还 cloak 着时先把宿主摆好、采好背景，再把内容压住，避免首帧闪动。</summary>
    internal bool SeatBehind()
    {
        if (_closed || _host is null) return false;
        var h = _handle();
        if (h == nint.Zero) return false;
        var scale = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        return _host.SeatBehind(h, scale, RadiusDip);
    }

    /// <summary>解除 cloak 后把磨砂打开，和卡片内容同一拍上屏。</summary>
    internal void PrimeForShow()
    {
        if (_closed || _host is null) return;
        var h = _handle();
        if (h == nint.Zero) return;
        _host.PrimeForShow(h);
    }

    /// <summary>宿主翻到卡内容上面时重新钉回正下方。</summary>
    internal void RepinBehindOwner()
    {
        if (_closed) return;
        _host?.RepinBehindOwner();
    }

    /// <summary>窗口自己的 WndProc 收到 WM_WINDOWPOSCHANGING 时转发，拖动时宿主才能同帧跟随。</summary>
    internal void FollowOwnerMove(nint lParam)
    {
        if (_closed || _dragLite) return;
        try { _host?.FollowOwnerMove(lParam); } catch { }
    }

    /// <summary>
    /// 开始拖动 / 拉伸：Win10 上把磨砂与投影先摘掉、宿主窗口原地不动，只让卡窗自己跟手走。
    ///
    /// 为什么必须这么做：宿主每跟手一帧就是一次 SetWindowPos —— DWM 会丢掉 HostBackdropBrush
    /// 的旧采样、重新采背景，并把整条「高斯模糊 + 两层投影」的效果图重跑一遍。Win10 的合成器
    /// 这一拍很贵，鼠标一动就掉帧；Win11 能吃下，所以只在 Win10 走这条路（见 <see cref="DesktopEmbed.DragLite"/>）。
    /// 宿主内容用「保持窗口可见、只摘内容」的通道摘掉：窗口不隐藏，采样不会丢，松手补一次即可，
    /// 不会出现「先亮一帧、再闪进模糊」。
    /// </summary>
    /// <returns>true = 进入降载；调用方松手时必须配一次 <see cref="EndDragLite"/>。</returns>
    internal bool BeginDragLite()
    {
        if (_dragLite) return true;
        if (!DesktopEmbed.DragLite || _closed) return false;
        var h = _handle();
        if (h == nint.Zero) return false;

        _dragLite = true;
        try
        {
            if (UsesNativeGlass) _host!.SuppressContent();
            else DesktopEmbed.ClearAcrylic(h);
        }
        catch { }
        DesktopLog.Write($"[{_tag}] 拖动降载 on glass={UsesNativeGlass}");
        return true;
    }

    /// <summary>松手：把宿主摆到落点矩形、打开磨砂并补采一次背景（只在这一下付一次重采的代价）。</summary>
    internal void EndDragLite()
    {
        if (!_dragLite) return;
        _dragLite = false;

        var h = _handle();
        if (_closed || h == nint.Zero) return;

        try
        {
            if (UsesNativeGlass)
            {
                // 顺序：先按最终矩形摆放（这一次会重采），再打开内容，最后重申一次采样兜住
                // 「拖动期间窗口被别的程序盖过 → 采样冻结」的情况。
                Sync();
                _host!.EnsureContentVisible();
                _host.RefreshBackdropSampling("drag-end");
                RepinBehindOwner();
            }
            else
            {
                ApplyChrome();
            }
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"[{_tag}] 拖动降载恢复失败：{ex.Message}");
        }
        DesktopLog.Write($"[{_tag}] 拖动降载 off glass={UsesNativeGlass}");
    }

    /// <summary>原生合成层挂不上（驱动 / 旧系统 / DLL 导出失败）时，整卡退回 8px 窗口级亚克力。</summary>
    internal void FallbackToAcrylic(string reason)
    {
        if (_closed) return;
        var wasGlassy = _host != null;
        _failed = true;
        try { _host?.Dispose(); } catch { }
        _host = null;
        if (wasGlassy) DesktopLog.Write($"[{_tag}] 原生玻璃不可用，回退窗口级亚克力 reason={reason}");
        ApplyChrome();
    }

    public void Dispose()
    {
        _closed = true;
        try { _host?.Dispose(); } catch { }
        _host = null;
    }
}
