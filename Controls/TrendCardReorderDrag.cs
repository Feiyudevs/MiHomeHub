// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MiHomeHub.Models;

namespace MiHomeHub.Controls;

/// <summary>
/// 「数据看板」编辑弹窗里的行排序：只从每行左侧的六点抓手起拖。
///
/// 交互沿用场景「执行顺序」的那一套：按住抓手后行不会自己跳动，被拖的那一行先淡下去，
/// 指针停在哪一行，就在那一行的上沿或下沿亮一条主题色细线，告诉用户松手会插到这里；
/// 松手才真正换位，并把顺序交给 ViewModel 落盘。
/// 指针拖到列表上下的留白里也认，贴到首 / 尾；拖回原位不画线，松手什么也不改。
/// </summary>
public sealed class TrendCardReorderDrag
{
    /// <summary>按下后移动超过这个距离（DIP）才进入拖动，避免手指轻微抖动就把行搬走。</summary>
    private const double DragThreshold = 4;

    /// <summary>行模板根节点的名字：找容器要落点线、挂拖动样式都靠它。</summary>
    private const string RowName = "OptionRow";

    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);

    private readonly ItemsControl _items;
    private readonly ObservableCollection<TrendCardOptionItem> _rows;
    private readonly Action _commit;

    private IPointer? _pointer;
    private TrendCardOptionItem? _item;
    private Border? _draggedRow;
    private Border? _hoverRow;
    private int _hoverIndex = -1;
    private bool _hoverBelow;
    private bool _pressed;
    private bool _dragging;
    private Point _start;

    public TrendCardReorderDrag(ItemsControl items, ObservableCollection<TrendCardOptionItem> rows, Action commit)
    {
        _items = items;
        _rows = rows;
        _commit = commit;

        // 隧道阶段先看：只有按在抓手上才接管，其余点击照常交给行里的开关
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

        // 抓手所在那一行：按的行 / 抓的行必须对得上，避免在多列布局里拖错列
        if (SourceRow(e.Source) is not { DataContext: TrendCardOptionItem item }) return;
        if (!_rows.Contains(item)) return;

        _pointer = e.Pointer;
        _item = item;

        // 抓手只管拖，这一下不再交给行里的开关
        e.Handled = true;
        // 先拿到捕获再置 pressed：捕获从行内控件转到列表本身时，
        // 那次 PointerCaptureLost 属于正常交接，不能让拖动在起步瞬间被取消
        _pointer.Capture(_items);

        _pressed = true;
        _start = e.GetPosition(_items);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _item is null) return;

        var position = e.GetPosition(_items);
        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed)
        {
            Finish(commit: true);
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(position.X - _start.X) < DragThreshold &&
                Math.Abs(position.Y - _start.Y) < DragThreshold)
                return;

            _dragging = true;
            _items.Cursor = GrabCursor;

            // 被拖的那一行淡下去，其余行保持原样，视线能跟住正在移动的是哪一张卡
            _draggedRow = RowBorder(_rows.IndexOf(_item));
            _draggedRow?.Classes.Set("dragging", true);
        }

        UpdateHint(position.Y);
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

        // 捕获被系统抢走（切窗、弹窗关闭）时按当前落点结算，别让用户白拖一回
        Finish(commit: true);
    }

    /// <summary>
    /// 指针在哪一行决定落点线画在谁身上：在行内就用这一行的上 / 下半区判断，
    /// 落在行间空隙或列表上下留白时退到最近的一行，拖出列表也仍贴首 / 尾。
    /// </summary>
    private void UpdateHint(double y)
    {
        var count = _items.ItemCount;
        var from = _item is null ? -1 : _rows.IndexOf(_item);
        if (count < 2 || from < 0)
        {
            ClearHint();
            return;
        }

        var target = NearestRow(y, out var below);
        if (target < 0)
        {
            ClearHint();
            return;
        }

        // 目标行下半区 → 插到它后面；上半区 → 插到它前面。移走自己后下标要往前收一格。
        var to = target + (below ? 1 : 0);
        if (from < to) to--;
        to = Math.Clamp(to, 0, count - 1);

        // 落回原位不画线：只有松开真的会换位置时才给这个提示
        if (to == from)
        {
            ClearHint();
            return;
        }

        SetHint(target, below);
    }

    private void SetHint(int index, bool below)
    {
        if (_hoverRow is not null && _hoverIndex == index && _hoverBelow == below) return;

        ClearHint();

        var row = RowBorder(index);
        if (row is null) return;

        _hoverRow = row;
        _hoverIndex = index;
        _hoverBelow = below;
        row.Classes.Set(below ? "dropafter" : "dropbefore", true);
    }

    private void ClearHint()
    {
        if (_hoverRow is { } row)
        {
            row.Classes.Set("dropbefore", false);
            row.Classes.Set("dropafter", false);
        }

        _hoverRow = null;
        _hoverIndex = -1;
        _hoverBelow = false;
    }

    /// <summary>松手时按最后一次亮线的位置换位；没拖出有效落点就什么都不改。</summary>
    private void Finish(bool commit)
    {
        var from = _item is null ? -1 : _rows.IndexOf(_item);
        var target = _hoverIndex;
        var below = _hoverBelow;

        Reset();

        if (!commit || from < 0 || target < 0) return;

        var to = target + (below ? 1 : 0);
        if (from < to) to--;
        to = Math.Clamp(to, 0, _rows.Count - 1);
        if (to == from) return;

        _rows.Move(from, to);
        _commit();
    }

    private void Reset()
    {
        _draggedRow?.Classes.Set("dragging", false);
        _draggedRow = null;
        ClearHint();

        var pointer = _pointer;
        _pointer = null;
        _item = null;
        _pressed = false;
        _dragging = false;
        _start = default;

        _items.Cursor = null;
        pointer?.Capture(null);
    }

    /// <summary>指针纵坐标离哪一行最近（在行内时距离算 0），并判断落在这一行的上半还是下半。</summary>
    private int NearestRow(double y, out bool below)
    {
        below = false;

        var best = -1;
        var bestDistance = double.MaxValue;
        var bestRect = default(Rect);

        for (var i = 0; i < _items.ItemCount; i++)
        {
            if (ContainerRect(i) is not { } rect) continue;

            var distance = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0.0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                bestRect = rect;
            }

            if (distance == 0) break;
        }

        if (best < 0) return -1;

        below = y > bestRect.Y + bestRect.Height / 2;
        return best;
    }

    /// <summary>行模板根节点（Border#OptionRow）：落点线和拖动淡出都挂在它身上。</summary>
    private Border? RowBorder(int index)
    {
        if (index < 0 || _items.ContainerFromIndex(index) is not ContentPresenter presenter) return null;

        if (presenter.Child is Border { Name: RowName } border) return border;

        return presenter.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(candidate => candidate.Name == RowName);
    }

    private Rect? ContainerRect(int index)
    {
        if (_items.ContainerFromIndex(index) is not { } container) return null;
        if (container.TranslatePoint(new Point(0, 0), _items) is not { } pos) return null;
        return new Rect(pos, container.Bounds.Size);
    }

    /// <summary>这一下是不是按在行模板左侧的六点抓手上（Border#Grip）：往上找的第一个具名 Border 得是抓手。</summary>
    private static bool IsGripSource(object? source)
    {
        if (source is not Visual visual) return false;

        var named = visual.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Name is RowName or "Grip");
        return named?.Name == "Grip";
    }

    /// <summary>从命中的可视元素往上找第一层行容器（Border#OptionRow）。</summary>
    private static Border? SourceRow(object? source)
    {
        if (source is not Visual visual) return null;

        return visual.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Name == RowName);
    }
}
