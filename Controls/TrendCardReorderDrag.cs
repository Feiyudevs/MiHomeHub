// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MiHomeHub.Models;

namespace MiHomeHub.Controls;

/// <summary>
/// 「数据看板」编辑弹窗里的行排序：只从每行左侧的六点抓手起拖，按住后上下移动，
/// 指针越过相邻行的中线就把这一行换过去 —— 列表当场重排，用户看到的就是最终顺序，
/// 松手时把顺序交给 ViewModel 落盘。
///
/// 没有浮卡和落点线：弹窗里只有十来行、行高一致，实时换位比画落点更直接，
/// 也不会出现「浮卡挡住下面几行、看不清最终位置」的问题。
/// </summary>
public sealed class TrendCardReorderDrag
{
    /// <summary>按下后移动超过这个距离（DIP）才进入拖动，避免手指轻微抖动就把行搬走。</summary>
    private const double DragThreshold = 4;

    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);

    private readonly ItemsControl _items;
    private readonly ObservableCollection<TrendCardOptionItem> _rows;
    private readonly Action _commit;

    private IPointer? _pointer;
    private Control? _dragged;
    private bool _pressed;
    private bool _dragging;
    private bool _dirty;
    private int _from = -1;
    private Point _start;

    public TrendCardReorderDrag(ItemsControl items, ObservableCollection<TrendCardOptionItem> rows, Action commit)
    {
        _items = items;
        _rows = rows;
        _commit = commit;

        // 隧道阶段先看：只有按在抓手上才接管，其余点击照常交给开关
        items.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Reset();

        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;
        if (!IsGripSource(e.Source) || _items.ItemCount < 2) return;

        var index = IndexAt(e.GetPosition(_items));
        if (index < 0 || index >= _items.ItemCount) return;

        _pointer = e.Pointer;

        // 抓手只管拖，这一下不再交给行里的开关
        e.Handled = true;
        // 先拿到捕获再置 pressed：捕获从行内控件转到列表本身时，
        // 那次 PointerCaptureLost 属于正常交接，不能让拖动在起步瞬间被取消
        _pointer.Capture(_items);

        _pressed = true;
        _from = index;
        _start = e.GetPosition(_items);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed)
        {
            Finish(commit: true);
            return;
        }

        var position = e.GetPosition(_items);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _start.X) < DragThreshold &&
                Math.Abs(position.Y - _start.Y) < DragThreshold)
                return;

            _dragging = true;
            _items.Cursor = GrabCursor;
            MarkDragged();
        }

        UpdateTarget(position.Y);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;

        e.Handled = true;
        Finish(commit: true);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // 只认列表自己丢掉捕获：从行内控件转到列表的那一次是正常交接，不能当取消
        if (!_pressed || !ReferenceEquals(e.Source, _items)) return;

        // 捕获被系统抢走（切窗、弹窗关闭）时顺序已经改过了，仍要落盘
        Finish(commit: true);
    }

    /// <summary>
    /// 指针在「去掉被拖行之后」的列表里跨过了几行中线，就插到第几位。
    /// 换位后立刻刷新被拖行的下标，下一次移动只跟相邻行比较，不会被自己刚换过去的位置带得来回跳。
    /// </summary>
    private void UpdateTarget(double y)
    {
        var count = _items.ItemCount;
        if (count < 2) return;

        var insert = 0;
        for (var i = 0; i < count; i++)
        {
            if (i == _from) continue;
            if (ContainerRect(i) is not { } rect) continue;
            if (y > rect.Y + rect.Height / 2) insert++;
        }

        var target = Math.Clamp(insert, 0, count - 1);
        if (target == _from) return;

        _rows.Move(_from, target);
        _from = target;
        _dirty = true;
        MarkDragged();
    }

    /// <summary>被拖的那一行淡一点，其余行保持原样，视线能跟住正在移动的是哪一张卡。</summary>
    private void MarkDragged()
    {
        if (_dragged is { } previous) previous.ClearValue(Visual.OpacityProperty);

        _dragged = _items.ContainerFromIndex(_from) as Control;
        if (_dragged is not null) _dragged.Opacity = 0.55;
    }

    private void Finish(bool commit)
    {
        var dirty = _dirty;
        Reset();
        if (commit && dirty) _commit();
    }

    private void Reset()
    {
        if (_dragged is { } dragged) dragged.ClearValue(Visual.OpacityProperty);
        _dragged = null;

        var pointer = _pointer;
        _pointer = null;
        _pressed = false;
        _dragging = false;
        _dirty = false;
        _from = -1;

        _items.Cursor = null;
        pointer?.Capture(null);
    }

    /// <summary>指针命中哪一行（按行的容器矩形，点在行间空隙不算）。</summary>
    private int IndexAt(Point p)
    {
        for (var i = 0; i < _items.ItemCount; i++)
        {
            if (_items.ContainerFromIndex(i) is not { } container) continue;
            if (container.TranslatePoint(new Point(0, 0), _items) is not { } pos) continue;
            if (new Rect(pos, container.Bounds.Size).Contains(p)) return i;
        }
        return -1;
    }

    private Rect? ContainerRect(int index)
    {
        if (_items.ContainerFromIndex(index) is not { } container) return null;
        if (container.TranslatePoint(new Point(0, 0), _items) is not { } pos) return null;
        return new Rect(pos, container.Bounds.Size);
    }

    /// <summary>这一下是不是按在行模板左侧的六点抓手上（Border#Grip）。</summary>
    private static bool IsGripSource(object? source) =>
        source is Visual visual
        && visual.GetSelfAndVisualAncestors().OfType<Control>().Any(control => control.Name == "Grip");
}
