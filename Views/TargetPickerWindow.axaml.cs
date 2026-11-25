// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHomeHub.Models;
using MiHomeHub.Services;
using MiHomeHub.ViewModels;

namespace MiHomeHub.Views;

/// <summary>
/// 全局唯一的设备选择弹窗：卡片网格（标题栏可拖动、点遮罩关闭），正文按房间 / 场景 / 自动化分组。
/// 三种用法共用同一个窗口与 VM：
/// - 桌面组件分区「添加设备 / 场景」：非模态、勾选即时写回、底部「全部移出」
/// - 触发条件 / 添加动作：模态、单选、底部「确定」
/// - 全局快捷键：模态、多选（勾选顺序即执行顺序）、底部「确定，带回所选」
/// </summary>
public partial class TargetPickerWindow : Window
{
    // 设计尺寸：窗体 956×736、卡片 840×620，四周各 58 是留给卡片投影的透明边
    private const double BaseWidth = 956;
    private const double BaseHeight = 736;
    private const double BaseCardWidth = 840;
    private const double BaseCardHeight = 620;
    private const double CardInset = 116;

    private readonly TargetPickerVm _vm = new();
    private bool _clamping;

    public TargetPickerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = () => Close(false);

        Opened += (_, _) =>
        {
            ClampToWorkArea();
            // 窗口可能被复用：上次滚动位置会残留，每次弹出都从顶部开始看
            BodyScroll.Offset = new Vector(0, 0);
            FocusSearch();
        };
        PositionChanged += (_, _) => ClampToWorkArea();
    }

    /// <summary>是否点了「确定」关窗（取消 / 遮罩 / Esc 为 false）。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>单选模式带回的设备。</summary>
    public MijiaDevice? SelectedDevice => _vm.SelectedDevice;

    /// <summary>即时提交模式下当前绑定的桌面组件分区。</summary>
    public DesktopGroupVm? Target => _vm.Target;

    /// <summary>多选模式带回的控制卡片（按勾选顺序）。</summary>
    public IReadOnlyList<HotkeyStep> SelectedSteps => _vm.SelectedSteps;

    /// <summary>桌面组件分区：设备 + 手动场景，勾选即时生效。</summary>
    public void ConfigureForGroup(DesktopGroupVm group)
    {
        Confirmed = false;
        _vm.ConfigureForGroup(group);
        Title = $"添加设备 / 场景 · {group.Name}";
    }

    /// <summary>触发条件 / 添加动作：单选一台设备。</summary>
    public void ConfigureSingleDevice(IEnumerable<MijiaDevice> devices, string? selectedDid,
        string emptySubtitle, string footerHint)
    {
        Confirmed = false;
        _vm.ConfigureSingleDevice(devices, selectedDid, emptySubtitle, footerHint);
        Title = "选择设备";
    }

    /// <summary>全局快捷键：设备 + 场景 + 自动化多选。</summary>
    public void ConfigureMultiple(HotkeyTargetPickerRequest request)
    {
        Confirmed = false;
        _vm.ConfigureMultiple(request);
        Title = "选择控制目标";
    }

    /// <summary>模态用法：按父窗口尺寸收一收，居中于父窗口。</summary>
    public void FitTo(Window owner)
    {
        var maxW = Math.Max(600, owner.ClientSize.Width - 40);
        var maxH = Math.Max(460, owner.ClientSize.Height - 40);

        var cardW = Math.Min(BaseCardWidth, maxW - CardInset);
        var cardH = Math.Min(BaseCardHeight, maxH - CardInset);

        PickerCard.Width = cardW;
        PickerCard.Height = cardH;
        Width = cardW + CardInset;
        Height = cardH + CardInset;

        Position = owner.PointToScreen(new Point(
            (owner.ClientSize.Width - Width) / 2,
            (owner.ClientSize.Height - Height) / 2));
    }

    /// <summary>
    /// 非模态（桌面组件分区）用法：按屏幕工作区收敛窗体与卡片，
    /// 小屏 / 高缩放下不会顶出工作区，卡片底部连同最后几行内容都能滚到。
    /// 每次弹出都按设计尺寸重算，不会沿用上次收小的值。
    /// </summary>
    public void FitToWorkArea()
    {
        var (w, h) = PopupPlacement.FitToWorkArea(this, BaseWidth, BaseHeight);
        Width = w;
        Height = h;

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

    /// <summary>点分类胶囊：下面只留那一组（「全部」= 全部分组），并把正文拉回顶部。</summary>
    private void OnRoomTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PickerTab tab }) return;
        _vm.SelectTab(tab);
        BodyScroll.Offset = new Vector(0, 0);
        e.Handled = true;
    }

    private void OnMaskPressed(object? sender, PointerPressedEventArgs e)
    {
        Close(false);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!_vm.ShowConfirm || !_vm.CanConfirm) return;
        Confirmed = true;
        Close(true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Close(false);
            return;
        }

        // 回车 = 确定（只对需要二次确认的模式生效）
        if (e.Key == Key.Enter && _vm.ShowConfirm && _vm.CanConfirm)
        {
            OnConfirmClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
