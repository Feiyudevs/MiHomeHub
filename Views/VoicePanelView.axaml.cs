// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 小爱语音指令浮层：由标题栏胶囊唤出。
/// 浮层可见性由动画驱动（打开淡入轻落 / 关闭收起后隐藏），开合动画随本控件走。
/// </summary>
public partial class VoicePanelView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public VoicePanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 若初始就是打开状态则直接落位（无动画）
        if (Vm is { IsVoiceOpen: true })
        {
            VoicePanel.IsVisible = true;
            VoicePanel.Opacity = 1;
        }
    }

    private void OnVoiceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // 回车直接发送，不插入换行
        Vm?.SendVoiceCommand.Execute(null);
    }

    /// <summary>
    /// 左沿对齐唤出按钮：由 MainWindow 传入按钮左沿在浮层坐标系中的位置（已夹在窗口内）。
    /// </summary>
    public void AlignToButton(double left)
    {
        VoicePanel.Margin = new Thickness(left, 2, 0, 0);
    }

    /// <summary>卡片本体宽度（宿主 UserControl 是整窗宽，夹取必须用卡片宽）。</summary>
    public double CardWidth => VoicePanel.Bounds.Width > 0 ? VoicePanel.Bounds.Width : 344;

    /// <summary>
    /// 浮层开合：打开时淡入并从标题栏下方轻落，关闭时反向收起后再隐藏。
    /// </summary>
    public void Animate(bool open) => OverlayReveal.Run(VoicePanel, open, () => Vm?.IsVoiceOpen == true);
}
