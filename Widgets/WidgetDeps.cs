// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Runtime.InteropServices;
using MiHomeHub.Services;

namespace MiHomeHub.Widgets;

/// <summary>缩放语义：Reflow = 变大显示更多内容（数据卡默认）；Scale = 整体等比放大。</summary>
public enum WidgetResizeMode
{
    Reflow,
    Scale
}

/// <summary>组件窗需要让 WidgetResize 读到的最小契约。</summary>
public interface IWidgetWindow
{
    WidgetResizeMode ResizeMode { get; }
}

/// <summary>MiHome 暂时不做全局 UI 缩放，按 1.0 处理；Zonex 的换算接口原样保留。</summary>
internal static class UiScale
{
    public const double Factor = 1.0;

    public const bool Active = false;
}

/// <summary>数据卡固定 20px 圆角（M2 原生玻璃宿主）。</summary>
internal static class GlassStyleService
{
    public const double WidgetCornerRadius = 20;
}

/// <summary>对齐辅助线在 MiHome 数据卡上不启用。</summary>
internal static class WidgetAlignGuide
{
    public static void End() { }
}

/// <summary>
/// Zonex 玻璃宿主带过来的日志入口。MiHome 没有 AppLog，统一落到 DesktopLog。
/// </summary>
internal static class AppLog
{
    public static void Info(string tag, string message)
        => DesktopLog.Write($"[{tag}] {message}");

    public static void Warn(string tag, string message)
        => DesktopLog.Write($"[{tag}] WARN {message}");

    public static void Error(string tag, string message)
        => DesktopLog.Write($"[{tag}] ERROR {message}");

    public static void Error(string tag, string message, Exception ex)
        => DesktopLog.Write($"[{tag}] ERROR {message} {ex.Message}");
}

/// <summary>
/// Zonex 的「统一全屏玻璃层」在这里不启用：MiHome 只有独立玻璃宿主这一条路径。
/// 保留同名 API 只是为了让直接搬过来的 <see cref="GlassBackdropHost"/> 能编译；
/// <see cref="Enabled"/> 恒 false，所以所有统一层分支都不会走到。
/// </summary>
internal sealed class DesktopGlassLayer
{
    internal static bool Enabled;

    internal static DesktopGlassLayer Instance { get; } = new();

    internal bool Ready => false;

    internal Func<nint>? ZAnchorSource { get; set; }

    internal bool TryCreate() => false;

    internal int Register() => -1;

    internal void Unregister(int id) { }

    internal bool SyncPanel(int id, nint owner, int x, int y, int width, int height, int radiusDip,
        float blur, float margin, float shadowOpacity, float shadowBlur, float shadowOffsetY,
        bool applyGlobal = true) => false;

    internal void HidePanel(int id) { }

    internal void MovePanel(int id, int x, int y, int width, int height) { }

    internal void PrimePanelForShow(int id, nint owner, int x, int y, int width, int height) { }

    internal void NotifyOwnerRestored(int id) { }

    internal void Reassert() { }

    internal void RefreshBackdropIfLayerHidden(string reason) { }

    internal void ReattachOwner() { }

    internal void EnsureZOrder(bool force = false, bool fromRefresh = false) { }

    internal void RequestZWatch() { }

    internal nint AboveLayerInsertAfter() => nint.Zero;

    internal bool ShellFloodRecent(int ms = 5000) => false;

    internal bool IsCardBelowLayer(nint card) => false;

    internal string DebugState() => "disabled";
}

/// <summary>
/// Zonex 桌面嵌入服务的裁剪桩：数据卡窗自己的 WndProc 负责 Z 序守卫，
/// 玻璃宿主不再重复挂 owner 过程（hookOwner=false / cardWndProc=false）。
/// </summary>
internal static class DesktopEmbedService
{
    internal static bool IsTextEditing(nint hwnd) => false;

    internal static bool ZOrderChangeAllowed => true;

    internal static IDisposable AllowZOrderChange() => Noop.Instance;

    internal static void SuppressZOrderChange(nint lParam) { }

    private sealed class Noop : IDisposable
    {
        internal static readonly Noop Instance = new();

        public void Dispose() { }
    }
}
