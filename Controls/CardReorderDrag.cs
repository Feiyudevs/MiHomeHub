// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MiHome.Controls;

/// <summary>
/// 宫格卡片的拖拽排序：按住卡片拖过阈值进入排序，源卡淡成虚影、落点边亮一条竖线，松手才真正改顺序。
/// 一个实例只管一面宫格（设备 / 场景各一），两块互不干扰。
///
/// 指针事件挂在 ItemsControl 上走隧道（Tunnel）：卡片本体是 Button，按下那一刻还分不清这一下是点击还是拖动，
/// 所以按下时不抢事件、让卡片照常进入按下态；真的拖过阈值才把捕获接过来 —— 之后松手就不再算点击，
/// 控制窗不会被误弹（样式上的虚影与虚线框由容器上的 dragging 类点亮，见 DesktopDevicesPanel）。
/// </summary>
public sealed class CardReorderDrag
{
    /// <summary>按下后移动超过这个距离（DIP）才算拖动，否则仍是一次点击。与分区标题栏拖动同一档。</summary>
    private const double DragThreshold = 4;

    /// <summary>落点线两端各留的空白（DIP）：竖线留上下、横线留左右，别顶到卡片圆角。</summary>
    private const double BarInset = 8;

    /// <summary>落点线粗细（DIP），与 DropBar 样式里的 Width 一对。</summary>
    private const double BarThickness = 3;

    /// <summary>指针落到两张卡之间的缝隙时，仍算「离得最近的那张」，超出这个距离（DIP）才认为已经出格。</summary>
    private const double OutOfGridDistance = 60;

    private readonly ItemsControl _items;
    private readonly Rectangle _bar;
    private readonly Rectangle _frame;
    private readonly Action<int, int> _commit;

    private bool _pressed;
    private bool _dragging;
    private int _from = -1;
    private int _to = -1;
    private Point _start;

    public CardReorderDrag(ItemsControl items, Rectangle bar, Rectangle frame, Action<int, int> commit)
    {
        _items = items;
        _bar = bar;
        _frame = frame;
        _commit = commit;

        items.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // 上一次要是被别处打断过（捕获被抢、没走到松手），残态在这次按下时清掉
        Reset();

        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;
        if (Grid() is not { } grid || grid.Children.Count < 2) return;
        if (!Hit(grid, e.GetPosition(Overlay), false, out var index, out _)) return;

        _pressed = true;
        _from = index;
        _start = e.GetPosition(_items);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed)
        {
            Reset();
            return;
        }

        if (!_dragging)
        {
            var p = e.GetPosition(_items);
            if (Math.Abs(p.X - _start.X) < DragThreshold && Math.Abs(p.Y - _start.Y) < DragThreshold) return;

            _dragging = true;
            SetGhost(_from, true);
            if (Container(_from) is { } source) e.Pointer.Capture(source);
        }

        UpdateDrop(e.GetPosition(Overlay));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 先把状态取出来再清：Capture(null) 会同步派发捕获丢失，顺序错了这里就以为没拖过
        var dragging = _dragging;
        var from = _from;
        var to = _to;
        Reset();
        if (!dragging) return;

        e.Pointer.Capture(null);
        // 拖过就不算点击：这一下不能交给卡片，否则松手会弹出控制窗
        e.Handled = true;
        if (to >= 0 && to != from) _commit(from, to);
    }

    /// <summary>
    /// 落点提示分两层，跟着宫格的排列方向走 —— 都必须贴在卡与卡之间：
    /// 一条半透明的框罩住「即将被替换的那一张」（松手后拖动卡就落在它的格子里），
    /// 再加一条落点线说明插在框的哪一边。单列（卡片上下叠）用上下半判断、画横线；
    /// 多列（卡片并排）用左右半判断、画竖线。写回模型的下标要先扣掉被拖走的那张。
    /// </summary>
    private void UpdateDrop(Point p)
    {
        if (Grid() is not { } grid || !Hit(grid, p, true, out var index, out var rect))
        {
            HideDrop();
            return;
        }

        var stacked = Columns(grid) <= 1;
        var before = stacked ? p.Y < rect.Center.Y : p.X < rect.Center.X;
        var insert = before ? index : index + 1;
        _to = Math.Clamp(insert > _from ? insert - 1 : insert, 0, grid.Children.Count - 1);

        MarkTarget(_to == _from ? -1 : _to);

        _bar.IsVisible = true;
        if (stacked)
        {
            // 横线：卡的上/下沿就在相邻两张卡的缝正中，两端各收一点，别顶到卡片圆角
            _bar.Width = Math.Max(10, rect.Width - BarInset * 2);
            _bar.Height = BarThickness;
            Canvas.SetLeft(_bar, rect.X + BarInset);
            Canvas.SetTop(_bar, (before ? rect.Y : rect.Bottom) - BarThickness / 2);
        }
        else
        {
            _bar.Width = BarThickness;
            _bar.Height = Math.Max(10, rect.Height - BarInset * 2);
            Canvas.SetLeft(_bar, (before ? rect.X : rect.Right) - BarThickness / 2);
            Canvas.SetTop(_bar, rect.Y + BarInset);
        }
    }

    /// <summary>把落位框罩到第 index 张卡上；index 为负（拖回原位、没有替换对象）时只留落点线。</summary>
    private void MarkTarget(int index)
    {
        if (index < 0 || !TryCardRect(index, out var card))
        {
            _frame.IsVisible = false;
            return;
        }

        _frame.IsVisible = true;
        _frame.Width = card.Width;
        _frame.Height = card.Height;
        Canvas.SetLeft(_frame, card.X);
        Canvas.SetTop(_frame, card.Y);
    }

    private void HideDrop()
    {
        _bar.IsVisible = false;
        _frame.IsVisible = false;
    }

    /// <summary>首行有几张卡：宫格是按列铺满再换行的，首行的数量就是列数。</summary>
    private int Columns(Panel grid)
    {
        if (grid.Children.Count == 0 || !TryRect(grid.Children[0], out var first)) return 1;

        var columns = 1;
        while (columns < grid.Children.Count
               && TryRect(grid.Children[columns], out var r)
               && Math.Abs(r.Y - first.Y) < 0.5)
            columns++;
        return columns;
    }

    private void Reset()
    {
        SetGhost(_from, false);
        HideDrop();
        _pressed = false;
        _dragging = false;
        _from = -1;
        _to = -1;
    }

    /// <summary>宫格面板就是 ItemsControl 里那块 AdaptiveGridPanel，卡片容器按顺序排在其 Children 里。</summary>
    private AdaptiveGridPanel? Grid() =>
        _items.GetVisualDescendants().OfType<AdaptiveGridPanel>().FirstOrDefault();

    private Control? Container(int index) =>
        index >= 0 && Grid() is { } grid && index < grid.Children.Count
            ? grid.Children[index] as Control
            : null;

    private void SetGhost(int index, bool on)
    {
        if (Container(index) is not { } control) return;
        if (on) control.Classes.Add("dragging");
        else control.Classes.Remove("dragging");
    }

    /// <summary>竖线浮层与宫格左上角对齐，坐标一律换成相对它来算。</summary>
    private Visual Overlay => _bar.Parent as Visual ?? _items;

    /// <summary>
    /// 指针在哪一格：命中矩形优先。按下时只认命中（没按在卡上就不该起拖）；
    /// 拖动中指针会短暂滑出宫格，这时按最近的一张算，拖得太远才收起竖线。
    /// </summary>
    private bool Hit(Panel grid, Point p, bool loose, out int index, out Rect rect)
    {
        index = -1;
        rect = default;
        var nearest = double.MaxValue;

        for (var i = 0; i < grid.Children.Count; i++)
        {
            if (!TryRect(grid.Children[i], out var r)) continue;

            if (r.Contains(p))
            {
                index = i;
                rect = r;
                return true;
            }

            if (!loose) continue;

            var dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - r.Right);
            var dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - r.Bottom);
            var d = dx * dx + dy * dy;
            if (d < nearest)
            {
                nearest = d;
                index = i;
                rect = r;
            }
        }

        return loose && index >= 0 && nearest < OutOfGridDistance * OutOfGridDistance;
    }

    private bool TryRect(Visual v, out Rect rect)
    {
        rect = default;
        if (v.TranslatePoint(new Point(0, 0), Overlay) is not Point pos) return false;
        rect = new Rect(pos, v.Bounds.Size);
        return true;
    }

    /// <summary>
    /// 卡片外壳（模板里那张带外距的 Border#Card）的实际矩形：落位框要跟卡片本体严丝合缝，
    /// 不能罩住外距（框比卡大一圈会在缝里露出边角）。取不到就退回容器矩形。
    /// </summary>
    private bool TryCardRect(int index, out Rect rect)
    {
        rect = default;
        if (Container(index) is not { } container) return false;

        var shell = container.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "Card");
        if (shell == null) return TryRect(container, out rect);

        if (shell.TranslatePoint(new Point(0, 0), Overlay) is not Point pos) return false;
        rect = new Rect(pos, shell.Bounds.Size);
        return true;
    }
}
