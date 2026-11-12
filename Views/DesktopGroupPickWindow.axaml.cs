// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.Models;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 「添加到桌面组件」选择分区弹窗：铺满主窗口客户区，遮罩压暗软件本体，卡片居中。
/// 已有分区多选，点「新建分区」才展开名称输入，底部「取消 / 确定」统一收口。
/// </summary>
public partial class DesktopGroupPickWindow : Window
{
    /// <summary>卡片相对窗口四周的留白：宽度要够投影，高度再留出标题与底部操作区。</summary>
    private const double EdgePadding = 96;

    private readonly DesktopGroupPickVm _vm = new();

    private Window? _owner;

    public DesktopGroupPickWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.CloseRequested = Close;
        _vm.CreateRevealed = FocusName;
        Closed += OnClosed;
    }

    public void Bind(MijiaDevice device) => _vm.Bind(device);

    /// <summary>
    /// 铺满父窗口客户区并让卡片居中：遮罩范围与设置弹窗一致，
    /// 弹窗看起来是挂在软件上的一层，而不是飘在桌面中央。
    /// </summary>
    public void FitTo(Window owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            DetachOwner();
            _owner = owner;
            owner.PositionChanged += OnOwnerMoved;
            owner.SizeChanged += OnOwnerResized;
        }

        ApplyBounds();
    }

    private void ApplyBounds()
    {
        if (_owner == null) return;

        var size = _owner.ClientSize;
        Width = Math.Max(420, size.Width);
        Height = Math.Max(360, size.Height);
        Position = _owner.PointToScreen(new Point(0, 0));

        Card.CardWidth = Math.Min(460, Math.Max(340, Width - EdgePadding));
        Card.CardMaxHeight = Math.Max(320, Height - EdgePadding);
    }

    private void OnOwnerMoved(object? sender, PixelPointEventArgs e) => ApplyBounds();

    private void OnOwnerResized(object? sender, SizeChangedEventArgs e) => ApplyBounds();

    /// <summary>展开新建分区后把光标放进名称输入框，直接输入即可。</summary>
    private void FocusName() => Dispatcher.UIThread.Post(() =>
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }, DispatcherPriority.Input);

    /// <summary>点分区行：切换勾选（DataTemplate 里的命令绑定在编译期拿不到，走 Tag + 代码后置）。</summary>
    private void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GroupPickItem item })
            _vm.ToggleCommand.Execute(item);
        e.Handled = true;
    }

    private void OnShowCreateClick(object? sender, RoutedEventArgs e)
    {
        _vm.ShowCreateCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _vm.CloseCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            _vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.ConfirmCommand.CanExecute(null))
        {
            _vm.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, System.EventArgs e) => DetachOwner();

    private void DetachOwner()
    {
        if (_owner == null) return;
        _owner.PositionChanged -= OnOwnerMoved;
        _owner.SizeChanged -= OnOwnerResized;
        _owner = null;
    }
}
