// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 触发条件的添加 / 编辑弹窗宿主：无边框透明窗体里放一张居中的 DialogPopup 卡片。
/// 内容与状态都挂在主视图模型上（设备 / 条件 / 取值三级选择器是同一套属性），
/// 所以「确定 / 取消」直接走 ConfirmTrigger / CancelTriggerEditor，不需要在这里搬数据。
///
/// 窗体本身铺满父窗口客户区（见 <see cref="FitTo"/>），DialogPopup 的遮罩才能像设置弹窗
/// 那样压暗整个主窗口；卡片在遮罩里居中，四周留白给投影。
/// </summary>
public partial class TriggerEditorWindow : Window
{
    /// <summary>卡片相对窗口四周的留白：宽度要够投影，高度再留出标题与投影。</summary>
    private const double EdgePadding = 96;

    private readonly MainViewModel _vm;

    /// <summary>由视图模型驱动的关闭（确定 / 取消）标记，避免 Closed 里再取消一次。</summary>
    private bool _closedByViewModel;

    private Window? _owner;

    /// <summary>设备选择窗是否已经开着（防双击连开两个）。</summary>
    private bool _devicePickerOpen;

    public TriggerEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += OnClosed;
    }

    /// <summary>
    /// 铺满父窗口客户区并让卡片居中。父窗口是无边框（ExtendClientArea）窗，客户区即整窗，
    /// 遮罩范围与设置弹窗（主窗口里 RowSpan=2 的 DialogPopup）一致。
    /// </summary>
    public void FitTo(Window owner)
    {
        _owner = owner;
        ApplyBounds();

        // 父窗口被挪动 / 改尺寸时跟着走（模态期间基本不会动，接上更稳）
        owner.PositionChanged += OnOwnerMoved;
        owner.SizeChanged += OnOwnerResized;
    }

    private void ApplyBounds()
    {
        if (_owner == null) return;

        var size = _owner.ClientSize;
        Width = Math.Max(420, size.Width);
        Height = Math.Max(360, size.Height);
        Position = _owner.PointToScreen(new Point(0, 0));

        // 卡片随窗口收一点，小窗口下也不会顶到遮罩外
        EditorPopup.CardWidth = Math.Min(500, Math.Max(320, Width - EdgePadding));
        EditorPopup.CardMaxHeight = Math.Max(320, Height - EdgePadding);
    }

    private void OnOwnerMoved(object? sender, PixelPointEventArgs e) => ApplyBounds();

    private void OnOwnerResized(object? sender, SizeChangedEventArgs e) => ApplyBounds();

    /// <summary>
    /// ① 设备行：弹出与桌面组件同一套的设备选择窗，回来把 did 写进视图模型，
    /// 后面的「② 条件」联动由 OnSelectedTriggerDeviceValueChanged 自己完成。
    /// </summary>
    private async void OnPickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_devicePickerOpen) return;

        _devicePickerOpen = true;
        try
        {
            var picker = new TriggerDevicePickerWindow();
            picker.Load(_vm.TriggerDeviceCandidates, _vm.SelectedTriggerDeviceValue?.ToString());
            picker.FitTo(this);

            if (await picker.ShowDialog<MijiaDevice?>(this) is { } device)
                _vm.SelectedTriggerDeviceValue = device.Did;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TriggerEditor] 打开设备选择窗失败：{ex.Message}");
        }
        finally
        {
            _devicePickerOpen = false;
        }
    }

    /// <summary>Esc 等同于「取消」，和遮罩点击、右上角关闭走同一个出口。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _vm.CancelTriggerEditorCommand.Execute(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsTriggerEditorOpen)) return;
        if (_vm.IsTriggerEditorOpen || _closedByViewModel) return;

        _closedByViewModel = true;
        Close();
    }

    /// <summary>窗口被直接关掉（系统关闭 / 代码 Close）时，把编辑器状态收干净，避免下次打开残留。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_owner != null)
        {
            _owner.PositionChanged -= OnOwnerMoved;
            _owner.SizeChanged -= OnOwnerResized;
            _owner = null;
        }

        if (_vm.IsTriggerEditorOpen) _vm.CancelTriggerEditorCommand.Execute(null);
    }
}
