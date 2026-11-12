// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区「添加设备 / 场景」弹窗宿主：自绘卡片（标题栏可拖动、点遮罩关闭），
/// 正文按房间分组、场景单独一组，每组是网格卡片；勾选即时生效，没有「确定」二次提交。
/// </summary>
public partial class DesktopPickerWindow : Window
{
    // 设计尺寸：窗体 956×736、卡片 840×620，四周各 58 是留给卡片投影的透明边
    private const double BaseWidth = 956;
    private const double BaseHeight = 736;
    private const double BaseCardWidth = 840;
    private const double BaseCardHeight = 620;
    private const double CardInset = 116;

    private readonly DesktopPickerVm _vm = new();
    private bool _clamping;

    public DesktopPickerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
        Opened += (_, _) =>
        {
            ClampToWorkArea();
            // 窗口是复用实例：上次关窗时滚到一半，再打开会停在中间，看着像「底部被切」。
            // 每次弹出都从顶部开始看。
            BodyScroll.Offset = new Vector(0, 0);
            FocusSearch();
        };
        PositionChanged += (_, _) => ClampToWorkArea();
    }

    /// <summary>
    /// 按屏幕工作区收敛窗体与卡片（小屏 / 高缩放下 956×736 会顶出工作区，
    /// 卡片底部连同滚动区最后几行一起被屏幕边缘吃掉）。每次弹出都按设计尺寸重算，
    /// 换屏或改分辨率后不会沿用上次收小的值。
    /// </summary>
    public void FitToWorkArea()
    {
        var (w, h) = PopupPlacement.FitToWorkArea(this, BaseWidth, BaseHeight);
        Width = w;
        Height = h;

        // 卡片同比收，但留出下限，别把标题栏和搜索条挤没
        PickerCard.Width = Math.Min(BaseCardWidth, Math.Max(560, w - CardInset));
        PickerCard.Height = Math.Min(BaseCardHeight, Math.Max(380, h - CardInset));
    }

    /// <summary>拖动标题栏时不要把窗体拖出工作区：一旦底部出屏，正文就再也滚不到了。</summary>
    private void ClampToWorkArea()
    {
        if (_clamping) return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        var scale = screen.Scaling > 0 ? screen.Scaling : RenderScaling;
        if (scale <= 0) scale = 1;

        var wa = screen.WorkingArea;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var x = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.Right - w));
        var y = Math.Clamp(Position.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - h));

        if (x == Position.X && y == Position.Y) return;

        _clamping = true;
        Position = new PixelPoint(x, y);
        _clamping = false;
    }

    public DesktopGroupVm? Target => _vm.Target;

    public void Bind(DesktopGroupVm vm)
    {
        _vm.Bind(vm);
        Title = $"添加设备 / 场景 · {vm.Name}";
    }

    /// <summary>打开就把光标放进搜索框：用户在长列表里几乎总是先搜。</summary>
    private void FocusSearch()
    {
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // 标题栏空白处按下才拖；右侧关闭按钮自己消费了事件
        BeginMoveDrag(e);
        e.Handled = true;
    }

    /// <summary>点分类胶囊：下面只留那一组（「全部房间」= 全部房间的设备），并把正文拉回顶部。</summary>
    private void OnRoomTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerTab tab }) return;
        _vm.SelectTab(tab);
        BodyScroll.Offset = new Vector(0, 0);
        e.Handled = true;
    }

    private void OnMaskPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
