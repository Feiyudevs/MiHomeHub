// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MiHome.Controls;

/// <summary>
/// 侧栏房间行的拖拽排序，两种入口：
/// ① 在房间行上按住约 0.3 秒（长按）进入拖动；按住后直接上下拖也算，移动超过阈值就进拖动，
/// ② 鼠标悬停时卡片内右侧浮出一个六点抓手（模板里的 Border#Grip），按住抓手立即拖动，不必等长按。
///
/// 拖动中的反馈（都在 NavDragLayer 这层纯视觉画布上）：
/// · 一张跟着指针走的浮卡，写着正在拖的房间名；
/// · 一条横贯卡片宽度的落点线，卡在「松手后会插进去」的那条缝里；
/// · 列表本身不动，原来那一行淡成占位，松手才真正换位并落盘。
///
/// 「全部」固定在第一行不参与排序。
/// </summary>
public sealed class NavReorderDrag
{
    /// <summary>按下后移动超过这个距离（DIP）就直接进入拖动，不必等长按计时。</summary>
    private const double DragThreshold = 4;

    /// <summary>长按多久进入拖动（毫秒）。太短会和单击抢手感，太长又像卡住。</summary>
    private const int HoldDelayMs = 320;

    /// <summary>指针离列表视口上下边多近开始自动滚动，以及每 16ms 滚多少（DIP）。</summary>
    private const double EdgeZone = 26;
    private const double EdgeStep = 9;

    /// <summary>落点线高度的一半：线中心压在插入缝上。</summary>
    private const double BarHalf = 1.5;

    /// <summary>行卡片相对容器矩形的内缩，与模板里 Border#Bg 的 Margin="10,3,10,3" 对齐。</summary>
    private const double CardInsetX = 10;
    private const double CardInsetY = 3;

    /// <summary>固定在顶部的「全部」所在下标。</summary>
    private const int LockedRow = 0;

    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);

    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _overlay;
    private readonly Border _ghost;
    private readonly TextBlock _ghostLabel;
    private readonly Border _bar;
    private readonly Action<int, int> _move;
    private readonly Action _commit;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _scrollTimer;

    private IPointer? _pointer;
    private bool _pressed;
    private bool _dragging;
    private bool _fromGrip;
    private int _from = -1;
    private int _to = -1;
    private double _ghostHeight;

    /// <summary>按下点相对行卡片顶边的偏移：浮卡从原位被「拎起」，跟着指针走时保持这个抓取位置。</summary>
    private double _grabOffsetY;
    private Point _pressAt;
    private Point _lastOverlayAt;
    private Point _lastScrollAt;
    private Button? _slot;

    public NavReorderDrag(ItemsControl items, ScrollViewer scroll, Canvas overlay,
        Border ghost, TextBlock ghostLabel, Border bar, Action<int, int> move, Action commit)
    {
        _items = items;
        _scroll = scroll;
        _overlay = overlay;
        _ghost = ghost;
        _ghostLabel = ghostLabel;
        _bar = bar;
        _move = move;
        _commit = commit;

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldDelayMs) };
        _holdTimer.Tick += OnHoldTick;
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += OnScrollTick;

        // 隧道阶段先看，按在抓手上要就地接管；房间行本身是 Button，其余情况都放行给它点击
        items.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        items.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // 上一次要是被别处打断过（捕获被抢、没走到松手），残态在这次按下时清掉
        Reset();

        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;
        if (_items.ItemCount <= LockedRow + 1) return;

        var index = IndexAt(e.GetPosition(_items));
        if (index <= LockedRow) return;

        _pressed = true;
        _fromGrip = IsGripSource(e.Source);
        _from = index;
        // 之后一律用浮层坐标（浮卡、落点线都画在这层），只在这里换算一次
        _pressAt = e.GetPosition(_overlay);
        _lastOverlayAt = _pressAt;
        _lastScrollAt = e.GetPosition(_scroll);
        _pointer = e.Pointer;

        if (_fromGrip)
        {
            // 抓手只管拖：这一下不交给按钮，免得松手时把房间选中
            e.Handled = true;
            BeginDrag();
            return;
        }

        _holdTimer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;

        _lastOverlayAt = e.GetPosition(_overlay);
        _lastScrollAt = e.GetPosition(_scroll);

        if (!_dragging)
        {
            if (_fromGrip) return;
            var p = _lastOverlayAt;
            if (Math.Abs(p.X - _pressAt.X) < DragThreshold && Math.Abs(p.Y - _pressAt.Y) < DragThreshold) return;

            // 按住就开始上下拖也能直接进拖动（不用干等长按）：只要先进入拖动，这一下就不再算点击
            BeginDrag();
            if (!_dragging) return;
        }

        UpdateTarget(_lastOverlayAt);
        MoveGhost(_lastOverlayAt.Y);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;

        var dragging = _dragging;
        var from = _from;
        var to = _to;
        Reset();
        if (!dragging) return;

        // 拖过就不算点击，这一下不能交给行按钮（否则松手会切房间）
        e.Handled = true;
        if (to >= 0 && to != from)
        {
            _move(from, to);
            _commit();
        }
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging)
        {
            // 拖动开始那一刻，行按钮先把捕获交给列表，这一次丢捕获是正常交接
            if (e.Source is Visual v
                && v.GetSelfAndVisualAncestors().OfType<Button>().Any(b => b.Classes.Contains("NavRow")))
                return;
            Reset();
            return;
        }

        CancelPress();
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (!_pressed || _dragging) return;
        BeginDrag();
    }

    private void BeginDrag()
    {
        // 拖动一旦开始，长按计时就没用了（按住直接拖的情况计时器还挂着，必须停掉，
        // 否则 320ms 后它会把浮卡按当时的位置重新拎一次）
        _holdTimer.Stop();

        if (!_pressed || _from <= LockedRow || _from >= _items.ItemCount || CardRect(_from) is not { } card)
        {
            CancelPress();
            return;
        }

        _dragging = true;
        _slot = Row(_from);
        _slot?.Classes.Set("dragging", true);

        // 浮卡按卡片实际尺寸摆：宽度跟行一致，并按「按下点在卡片里的位置」把它从原位拎起来，
        // 开头那一帧就正好盖住被拖的那一行，不会先跳到别处
        _ghostLabel.Text = _slot?.Content as string ?? "";
        _ghost.Width = card.Width;
        _ghost.Height = card.Height;
        _ghostHeight = card.Height;
        _grabOffsetY = Math.Clamp(_lastOverlayAt.Y - card.Y, 0, card.Height);
        Canvas.SetLeft(_ghost, card.X);
        MoveGhost(_lastOverlayAt.Y);
        _ghost.IsVisible = true;

        ApplyGrabCursor(true);
        _pointer?.Capture(_items);
        _scrollTimer.Start();
        UpdateTarget(_lastOverlayAt);
    }

    /// <summary>
    /// 算落点：指针上方有几行（按行中心线判断）就是插入缝的下标，扣掉被拖走的那一行换算成 Move 的目标下标。
    /// 列表保持不动，落点只由这条横线表达。
    /// </summary>
    private void UpdateTarget(Point at)
    {
        var count = _items.ItemCount;
        if (count <= LockedRow + 1) return;

        var insert = InsertIndex(at.Y);
        var target = insert > _from ? insert - 1 : insert;
        _to = Math.Clamp(target, LockedRow + 1, count - 1);

        // 挪回原位就不用画线（什么都不会变）
        if (_to == _from)
        {
            _bar.IsVisible = false;
            return;
        }

        ShowBar(_to > _from ? _to + 1 : _to);
    }

    private int InsertIndex(double y)
    {
        var count = _items.ItemCount;
        var above = 0;
        var measured = 0;
        for (var i = 0; i < count; i++)
        {
            if (ContainerRect(i) is not { } rect) continue;
            measured++;
            if (y > rect.Y + rect.Height / 2) above++;
        }
        // 容器一时没量出来（重排那一瞬间）就按原位算，别把行甩到列表最上面
        return measured == 0 ? _from : above;
    }

    /// <summary>把落点线摆到第 gap 条缝上（gap = 行数 表示最后一行下面）。</summary>
    private void ShowBar(int gap)
    {
        var count = _items.ItemCount;
        if (count <= 0)
        {
            _bar.IsVisible = false;
            return;
        }

        var refIndex = Math.Clamp(gap >= count ? count - 1 : gap, LockedRow, count - 1);
        var card = CardRect(refIndex);
        var boundary = gap >= count
            ? ContainerRect(count - 1)?.Bottom
            : ContainerRect(gap)?.Top;

        if (card is not { } c || boundary is not { } y)
        {
            _bar.IsVisible = false;
            return;
        }

        _bar.Width = c.Width;
        Canvas.SetLeft(_bar, c.X);
        Canvas.SetTop(_bar, y - BarHalf);
        _bar.IsVisible = true;
    }

    /// <summary>浮卡竖着跟指针（保持按下时的抓取点），但夹在房间列表的视口里，别跑到上面的导航行上。</summary>
    private void MoveGhost(double overlayY)
    {
        var top = overlayY - _grabOffsetY;
        if (_scroll.TranslatePoint(new Point(0, 0), _overlay) is { } viewport && _scroll.Bounds.Height > 0)
        {
            var max = viewport.Y + _scroll.Bounds.Height - _ghostHeight;
            top = Math.Clamp(top, viewport.Y, Math.Max(viewport.Y, max));
        }
        Canvas.SetTop(_ghost, top);
    }

    /// <summary>指针拖到列表视口上下边缘时自动滚动，滚完按新的内容坐标重算落点。</summary>
    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (!_dragging)
        {
            _scrollTimer.Stop();
            return;
        }

        var viewport = _scroll.Viewport.Height;
        if (viewport <= 0) return;

        var offset = _scroll.Offset;
        var max = Math.Max(0, _scroll.Extent.Height - viewport);

        double delta = 0;
        if (_lastScrollAt.Y < EdgeZone) delta = -EdgeStep;
        else if (_lastScrollAt.Y > viewport - EdgeZone) delta = EdgeStep;

        var next = Math.Clamp(offset.Y + delta, 0, max);
        if (Math.Abs(next - offset.Y) < 0.5) return;

        _scroll.Offset = new Vector(offset.X, next);
        // 行跟着滚上去了、指针没动：直接按同一个指针位置重算落点（落点线跟着行跑）
        UpdateTarget(_lastOverlayAt);
    }

    private void Reset()
    {
        _holdTimer.Stop();
        _scrollTimer.Stop();

        var dragging = _dragging;
        var pointer = _pointer;
        _pointer = null;
        _pressed = false;
        _dragging = false;
        _fromGrip = false;
        _from = -1;
        _to = -1;

        _ghost.IsVisible = false;
        _bar.IsVisible = false;
        _slot?.Classes.Set("dragging", false);
        _slot = null;

        if (!dragging) return;

        ApplyGrabCursor(false);
        pointer?.Capture(null);
    }

    /// <summary>取消这次按下（没进入拖动）：捕获仍归行按钮自己，这里不动指针。</summary>
    private void CancelPress()
    {
        _holdTimer.Stop();
        _pressed = false;
        _fromGrip = false;
        _from = -1;
        _pointer = null;
    }

    /// <summary>拖动期间整条列表（含别的行）都显示抓取光标，松手恢复。</summary>
    private void ApplyGrabCursor(bool on)
    {
        _items.Cursor = on ? GrabCursor : null;
        foreach (var row in _items.GetVisualDescendants().OfType<Button>())
        {
            if (on) row.Cursor = GrabCursor;
            else row.ClearValue(InputElement.CursorProperty);
        }
    }

    /// <summary>指针命中哪一行（按容器矩形，命中空白区域不算）。坐标是 ItemsControl 空间。</summary>
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

    private Button? Row(int index)
    {
        var container = _items.ContainerFromIndex(index);
        if (container == null) return null;
        return container.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("NavRow"));
    }

    /// <summary>行容器矩形（换到浮层坐标）。</summary>
    private Rect? ContainerRect(int index)
    {
        if (_items.ContainerFromIndex(index) is not { } container) return null;
        if (container.TranslatePoint(new Point(0, 0), _overlay) is not { } pos) return null;
        return new Rect(pos, container.Bounds.Size);
    }

    /// <summary>行里那张圆角卡片（Border#Bg）的矩形：落点线、浮卡都跟它对齐，别压到外距。</summary>
    private Rect? CardRect(int index)
    {
        if (ContainerRect(index) is not { } rect) return null;
        return new Rect(rect.X + CardInsetX, rect.Y + CardInsetY,
            Math.Max(0, rect.Width - CardInsetX * 2),
            Math.Max(0, rect.Height - CardInsetY * 2));
    }

    /// <summary>这一下是不是按在行模板的抓手上（Grip 是 ControlTemplate 里的具名 Border）。</summary>
    private static bool IsGripSource(object? source) =>
        source is Visual visual
        && visual.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c.Name == "Grip");
}
