// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia.Controls;

namespace MiHomeHub.Views;

/// <summary>
/// 贴桌面卡片窗的公共契约：设备分区窗与数据卡窗都实现这一份，
/// <see cref="Services.DesktopHost"/> 的注册表、显隐、多屏落位与外观广播因此不必区分窗型。
/// </summary>
public interface IDesktopWindow
{
    nint Handle { get; }

    bool IsExiled { get; }

    /// <summary>底下的 Avalonia 窗，宿主统一订阅 Closed。</summary>
    Window Window { get; }

    double Width { get; set; }

    double Height { get; set; }

    double RenderScaling { get; }

    string CurrentScreenSignature();

    void ReapplyPlacement();

    void ReapplyAppearance();

    void ApplyChrome();

    void BringToFrontTemporarily();

    void BeginRename();

    void Close();
}
