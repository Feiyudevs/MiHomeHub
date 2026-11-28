// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace MiHomeHub.Services;

/// <summary>
/// 窗口尺寸的「不许离谱」上限。
///
/// 背景（macOS）：窗口一旦被撑到远超屏幕的大小，AppKit 会把窗口帧裁到平台允许的上限，
/// 而 Avalonia 把裁回来的物理像素当成逻辑点读回，于是每往返一次尺寸就放大一轮
/// （实测 1668 → 22248 → 50668 → 61448…）。窗口越大，backing store 的分配
/// （宽 × 高 × 4 字节 × RenderScaling²）越夸张，最后直接 OutOfMemoryException
/// 把进程带走 —— 表现就是「点绿键进全屏就闪退」。
///
/// 所以所有「写窗口尺寸」的地方（拖边缩放、最大化 / 还原、读回历史布局）都要先过这里，
/// 并且历史布局里已经离谱的值要直接丢弃，不让它再进平台。
/// </summary>
public static class WindowSizeLimits
{
    /// <summary>
    /// 单边绝对上限（逻辑点）。够大到横跨两台显示器，又小到 backing store 分配得起
    /// （Retina 上 backing store 是 2 倍像素，再乘 4 字节）。
    /// </summary>
    public const double MaxExtent = 6000;

    /// <summary>尺寸是否在可接受范围内（有限、正数、不超过上限）。</summary>
    public static bool IsSane(double width, double height) =>
        IsSaneExtent(width) && IsSaneExtent(height);

    public static bool IsSaneExtent(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= MaxExtent;

    /// <summary>把宽度夹到 [min, MaxExtent]。</summary>
    public static double ClampWidth(double width, double min) =>
        Math.Clamp(width, Math.Max(min, 1), MaxExtent);

    /// <summary>把高度夹到 [min, MaxExtent]。</summary>
    public static double ClampHeight(double height, double min) =>
        Math.Clamp(height, Math.Max(min, 1), MaxExtent);
}
