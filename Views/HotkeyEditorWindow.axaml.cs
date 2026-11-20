// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 「新建 / 编辑快捷键」弹窗宿主：控制卡片 + 组合键录制 + 保存。
/// 卡片、动作胶囊、组合键状态都绑在 <see cref="MainViewModel"/> 的草稿属性上，
/// 窗口只负责开窗、关窗、选择目标与窗口内兜底录制。
/// </summary>
public partial class HotkeyEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private HotkeyModifier _fallbackModifiers = HotkeyModifier.None;

    // ===== 控制卡片拖拽排序（与创建自动化弹窗同一套做法）=====
    private const string StepDragFormat = "MiHome.HotkeyDraftStep";
    private Point _stepGripStart;
    private HotkeyDraftStepVm? _stepDragCandidate;
    private Control? _hoveredStepCard;

    public HotkeyEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.CloseHotkeyEditorRequested = () => Close();
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.CloseHotkeyEditorRequested = null;
            // 被外部关掉（不是保存 / 取消）时也要把草稿与录制状态清干净
            if (vm.IsHotkeyEditorOpen) vm.CancelHotkeyEditorCommand.Execute(null);
        };

        // 兜底录制：全局钩子被安全软件挡掉时，前台窗口仍能收到按键
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 让弹窗本身铺满主窗客户区：遮罩才能整块压暗页面，而不是在应用中间留一块被裁切的暗色矩形。
    /// </summary>
    public void FitTo(Window owner)
    {
        var origin = owner.PointToScreen(new Point(0, 0));
        Position = new PixelPoint((int)origin.X, (int)origin.Y);
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 每次开始录制都从零记录窗口内兜底用的修饰键状态（上一次可能残留）
        if (e.PropertyName == nameof(MainViewModel.IsRecordingHotkey) && _vm.IsRecordingHotkey)
            _fallbackModifiers = HotkeyModifier.None;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_vm.IsRecordingHotkey) return;

        if (e.Key == Key.Escape)
        {
            _vm.CancelRecordHotkeyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var mod = ModifierOf(e.Key);
        if (mod != HotkeyModifier.None)
        {
            _fallbackModifiers |= mod;
            _vm.PreviewCaptureModifiers(_fallbackModifiers);
            e.Handled = true;
            return;
        }

        var vk = ToVirtualKey(e.Key);
        if (vk == 0) return;

        var modifiers = _fallbackModifiers;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifier.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifier.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifier.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifier.Win;

        _vm.ApplyCapturedHotkey(new HotkeyCombo(modifiers, vk));
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_vm.IsRecordingHotkey) return;

        var mod = ModifierOf(e.Key);
        if (mod == HotkeyModifier.None) return;
        _fallbackModifiers &= ~mod;
        _vm.PreviewCaptureModifiers(_fallbackModifiers);
    }

    private static HotkeyModifier ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => HotkeyModifier.Ctrl,
        Key.LeftShift or Key.RightShift => HotkeyModifier.Shift,
        Key.LeftAlt or Key.RightAlt => HotkeyModifier.Alt,
        Key.LWin or Key.RWin => HotkeyModifier.Win,
        _ => HotkeyModifier.None,
    };

    /// <summary>Avalonia Key → Win32 虚拟键码；只映射录制实际用得上的字母、数字、F 键与常用键。</summary>
    private static int ToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 0x41 + ((int)key - (int)Key.A);
        if (key is >= Key.D0 and <= Key.D9) return 0x30 + ((int)key - (int)Key.D0);
        if (key is >= Key.F1 and <= Key.F24) return 0x70 + ((int)key - (int)Key.F1);

        return key switch
        {
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Back => 0x08,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            _ => 0,
        };
    }

    // ===== 拖拽排序 =====

    /// <summary>按住左侧点阵手柄才开始记录候选卡，避免与卡片里的动作切换组抢指针。</summary>
    private void OnStepGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip) return;
        var point = e.GetCurrentPoint(grip);
        if (!point.Properties.IsLeftButtonPressed) return;

        _stepGripStart = point.Position;
        _stepDragCandidate = grip.DataContext as HotkeyDraftStepVm;
    }

    /// <summary>移动监在整张卡片上：指针拖出手柄范围后仍然收得到事件。</summary>
    private async void OnStepPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_stepDragCandidate == null || sender is not Control card) return;

        var point = e.GetCurrentPoint(card);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _stepDragCandidate = null;
            return;
        }

        var delta = point.Position - _stepGripStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        var step = _stepDragCandidate;
        _stepDragCandidate = null;
        step.IsDragging = true;

        try
        {
            var data = new DataObject();
            data.Set(StepDragFormat, step);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch
        {
            // Esc / 指针丢失导致的取消不必打扰用户
        }
        finally
        {
            step.IsDragging = false;
            if (_hoveredStepCard != null)
            {
                ClearStepDropHint(_hoveredStepCard);
                _hoveredStepCard = null;
            }
        }
    }

    private void OnStepDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(StepDragFormat)) return;
        if (sender is not Control card) return;

        if (_hoveredStepCard != null && !ReferenceEquals(_hoveredStepCard, card))
            ClearStepDropHint(_hoveredStepCard);
        _hoveredStepCard = card;

        var isSource = e.Data.Get(StepDragFormat) is HotkeyDraftStepVm s
                       && ReferenceEquals(s, card.DataContext);
        card.Classes.Set("dragover", !isSource);
        var below = e.GetPosition(card).Y > card.Bounds.Height / 2;
        card.Classes.Set("dropafter", !isSource && below);
        card.Classes.Set("dropbefore", !isSource && !below);

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnStepDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Control card) return;
        ClearStepDropHint(card);
        if (ReferenceEquals(_hoveredStepCard, card)) _hoveredStepCard = null;
    }

    private static void ClearStepDropHint(Control card)
    {
        card.Classes.Set("dragover", false);
        card.Classes.Set("dropbefore", false);
        card.Classes.Set("dropafter", false);
    }

    private void OnStepDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control card) return;
        ClearStepDropHint(card);
        _hoveredStepCard = null;
        if (e.Data.Get(StepDragFormat) is not HotkeyDraftStepVm source) return;
        if (card.DataContext is not HotkeyDraftStepVm target) return;

        var from = _vm.HotkeyDraftSteps.IndexOf(source);
        var to = _vm.HotkeyDraftSteps.IndexOf(target);
        if (from < 0 || to < 0) return;

        // 落点在目标卡片下半部分 → 插到它后面
        if (e.GetPosition(card).Y > card.Bounds.Height / 2) to++;
        if (from < to) to--;

        _vm.MoveHotkeyDraftStep(from, to);
        e.Handled = true;
    }

    /// <summary>点「选择目标」：复用全局唯一的设备选择弹窗（多选模式）。</summary>
    private async void OnPickTargetsClick(object? sender, RoutedEventArgs e)
    {
        var request = _vm.BuildHotkeyTargetPickerRequest();
        if (request.Devices.Count == 0 && request.SceneCandidates.Count == 0 && request.Automations.Count == 0) return;

        try
        {
            var picker = new TargetPickerWindow();
            picker.ConfigureMultiple(request);
            picker.FitTo(this);
            await picker.ShowDialog(this);
            if (picker.Confirmed) _vm.ApplyHotkeyTargetPickerResult(picker.SelectedSteps);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyEditor] 打开选择弹窗失败：{ex}");
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnMaskPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm.CancelHotkeyEditorCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _vm.CancelHotkeyEditorCommand.Execute(null);
            e.Handled = true;
        }
    }
}
