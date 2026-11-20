// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MiHome.Views;

/// <summary>
/// 快捷键触发时的桌面 toast：无边框、置顶、不抢焦点，贴在主屏工作区右下角。
/// 只负责「长什么样 + 摆在哪」，显隐节奏与复用由 <see cref="Services.HotkeyToast"/> 控制。
/// </summary>
public partial class HotkeyToastWindow : Window
{
    /// <summary>离屏幕右下角的留白（px）。</summary>
    private const int ScreenMargin = 18;

    public HotkeyToastWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PositionBottomRight();
    }

    /// <summary>写入两行文案：第一行是快捷键名，第二行是这次执行结果。</summary>
    public void SetContent(string head, string body)
    {
        HeadText.Text = head;
        BodyText.Text = body;
    }

    /// <summary>按主屏工作区定位到右下角（座标是物理像素，宽高要乘缩放）。</summary>
    public void PositionBottomRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1d : screen.Scaling;

        var width = (int)Math.Round((Bounds.Width > 0 ? Bounds.Width : Width) * scaling);
        var height = (int)Math.Round((Bounds.Height > 0 ? Bounds.Height : 120) * scaling);

        Position = new PixelPoint(
            area.X + area.Width - width - ScreenMargin,
            area.Y + area.Height - height - ScreenMargin);
    }
}
