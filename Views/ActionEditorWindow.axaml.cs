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
/// 「添加动作」弹窗宿主：无边框透明窗体里放一张居中的 DialogPopup 卡片。
/// 内容与状态都挂在主视图模型上（设备 / 属性 / 取值三级选择器），
/// 「添加 / 取消」直接走 ConfirmAddAction / CancelAddAction，这里只管开窗与铺满父窗口。
/// </summary>
public partial class ActionEditorWindow : Window
{
    /// <summary>卡片相对窗口四周的留白：宽度要够投影，高度再留出标题与投影。</summary>
    private const double EdgePadding = 96;

    private readonly MainViewModel _vm;

    /// <summary>由视图模型驱动的关闭（添加 / 取消）标记，避免 Closed 里再取消一次。</summary>
    private bool _closedByViewModel;

    private Window? _owner;

    /// <summary>设备选择窗是否已经开着（防双击连开两个）。</summary>
    private bool _devicePickerOpen;

    public ActionEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += OnClosed;
    }

    /// <summary>铺满父窗口客户区并让卡片居中，遮罩范围与其它弹窗一致。</summary>
    public void FitTo(Window owner)
    {
        _owner = owner;
        ApplyBounds();

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

        EditorPopup.CardWidth = Math.Min(500, Math.Max(320, Width - EdgePadding));
        EditorPopup.CardMaxHeight = Math.Max(320, Height - EdgePadding);
    }

    private void OnOwnerMoved(object? sender, PixelPointEventArgs e) => ApplyBounds();

    private void OnOwnerResized(object? sender, SizeChangedEventArgs e) => ApplyBounds();

    /// <summary>
    /// ① 设备行：弹出与触发条件同一套的设备选择窗（卡片网格 + 房间胶囊 + 搜索），
    /// 回来把 did 写进视图模型，后面的「② 属性」联动由 OnSelectedActionDeviceValueChanged 自己完成。
    /// </summary>
    private async void OnPickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_devicePickerOpen) return;

        _devicePickerOpen = true;
        try
        {
            var picker = new TriggerDevicePickerWindow();
            picker.SetContext("点一台设备选中，确定后写入动作",
                              "确定后回到添加动作，继续选属性和值");
            picker.Load(_vm.TriggerDeviceCandidates, _vm.SelectedActionDeviceValue?.ToString());
            picker.FitTo(this);

            if (await picker.ShowDialog<MijiaDevice?>(this) is { } device)
                _vm.SelectedActionDeviceValue = device.Did;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ActionEditor] 打开设备选择窗失败：{ex.Message}");
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
        _vm.CancelAddActionCommand.Execute(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsActionEditorOpen)) return;
        if (_vm.IsActionEditorOpen || _closedByViewModel) return;

        _closedByViewModel = true;
        Close();
    }

    /// <summary>窗口被直接关掉时把编辑态收干净，避免下次打开残留上一次的设备 / 属性。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_owner != null)
        {
            _owner.PositionChanged -= OnOwnerMoved;
            _owner.SizeChanged -= OnOwnerResized;
            _owner = null;
        }

        if (_vm.IsActionEditorOpen) _vm.CancelAddActionCommand.Execute(null);
    }
}
