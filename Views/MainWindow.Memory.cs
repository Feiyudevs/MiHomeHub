using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MiHome.Services;

namespace MiHome.Views;

public partial class MainWindow
{
    private static readonly TimeSpan ScrollTrimDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastInteraction = DateTimeOffset.UtcNow;
    private DispatcherTimer? _idleTrimTimer;
    private DispatcherTimer? _scrollTrimTimer;
    private bool _memoryTrimmersActive;
    private bool _memoryTrimmersClosed;
    private bool _idleTrimmed;
    private bool _scrollTrimPending;
    private bool _scrollThumbDragging;

    private bool CanRunAutomaticMemoryTrimmers =>
        !_memoryTrimmersClosed && IsVisible && WindowState != WindowState.Minimized;

    private void InitIdleMemoryTrimmer()
    {
        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTrimTimer.Tick += OnIdleMemoryTrimTick;
        _scrollTrimTimer = new DispatcherTimer { Interval = ScrollTrimDelay };
        _scrollTrimTimer.Tick += OnScrollMemoryTrimTick;

        void MarkInteraction(object? sender, RoutedEventArgs e) => MarkMemoryInteraction();
        AddHandler(InputElement.PointerMovedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerWheelChangedEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, MarkInteraction, RoutingStrategies.Tunnel);
        AddHandler(ScrollViewer.ScrollChangedEvent, OnMemoryScrollChanged,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Thumb.DragStartedEvent, OnMemoryScrollThumbStarted,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Thumb.DragCompletedEvent, OnMemoryScrollThumbCompleted,
            RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) =>
        {
            _memoryTrimmersClosed = true;
            StopAutomaticMemoryTrimmers();
        };
        UpdateAutomaticMemoryTrimmerActivity();
    }

    private void MarkMemoryInteraction()
    {
        if (!_memoryTrimmersActive) return;
        _lastInteraction = DateTimeOffset.UtcNow;
        _idleTrimmed = false;
    }

    private void UpdateAutomaticMemoryTrimmerActivity()
    {
        // Avalonia 在基类构造/InitializeComponent 期间也会调用 OnPropertyChanged。
        if (_idleTrimTimer is null) return;
        if (!CanRunAutomaticMemoryTrimmers)
        {
            StopAutomaticMemoryTrimmers();
            return;
        }
        if (_memoryTrimmersActive) return;
        _memoryTrimmersActive = true;
        MarkMemoryInteraction();
        _idleTrimTimer.Start();
    }

    private void StopAutomaticMemoryTrimmers()
    {
        _memoryTrimmersActive = false;
        _idleTrimTimer?.Stop();
        CancelScrollMemoryTrim();
        _scrollThumbDragging = false;
    }

    private void CancelScrollMemoryTrim()
    {
        _scrollTrimTimer?.Stop();
        _scrollTrimPending = false;
    }

    private void OnMemoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 只响应真实偏移，排除加载/换尺寸带来的 Extent 或 Viewport 变化。
        if (e.OffsetDelta == default || !_memoryTrimmersActive) return;
        MarkMemoryInteraction();
        _scrollTrimPending = true;
        ScheduleScrollMemoryTrim();
    }

    private void ScheduleScrollMemoryTrim()
    {
        _scrollTrimTimer?.Stop();
        if (_scrollTrimTimer is null || !_scrollTrimPending || _scrollThumbDragging
            || !CanRunAutomaticMemoryTrimmers) return;
        _scrollTrimTimer.Interval = ScrollTrimDelay;
        _scrollTrimTimer.Start();
    }

    private static bool IsScrollBarThumb(VectorEventArgs e) =>
        e.Source is Visual visual && visual.FindAncestorOfType<ScrollBar>() is not null;

    private void OnMemoryScrollThumbStarted(object? sender, VectorEventArgs e)
    {
        if (!_memoryTrimmersActive || !IsScrollBarThumb(e)) return;
        _scrollThumbDragging = true;
        _scrollTrimTimer?.Stop();
    }

    private void OnMemoryScrollThumbCompleted(object? sender, VectorEventArgs e)
    {
        if (!IsScrollBarThumb(e)) return;
        _scrollThumbDragging = false;
        ScheduleScrollMemoryTrim();
    }

    private void OnScrollMemoryTrimTick(object? sender, EventArgs e)
    {
        _scrollTrimTimer?.Stop();
        if (!_scrollTrimPending) return;
        if (!CanRunAutomaticMemoryTrimmers)
        {
            CancelScrollMemoryTrim();
            return;
        }
        if (_scrollThumbDragging) return;
        if (MemoryTrimmer.TryTrim(out var retryAfter))
        {
            _scrollTrimPending = false;
            // 本段空闲已经整理过，不再在30秒后重复完整 GC。
            _idleTrimmed = true;
        }
        else if (retryAfter > TimeSpan.Zero && _scrollTrimTimer is not null)
        {
            // 最近启动/收起/滚停整理尚在10秒节流内：延后一次，不丢掉本次滚停。
            _scrollTrimTimer.Interval = retryAfter + TimeSpan.FromMilliseconds(50);
            _scrollTrimTimer.Start();
        }
    }

    private void OnIdleMemoryTrimTick(object? sender, EventArgs e)
    {
        if (!CanRunAutomaticMemoryTrimmers)
        {
            StopAutomaticMemoryTrimmers();
            return;
        }
        if (_idleTrimmed || _scrollThumbDragging
            || DateTimeOffset.UtcNow - _lastInteraction < IdleTrimDelay) return;
        if (MemoryTrimmer.TryTrim(out _))
        {
            _idleTrimmed = true;
        }
    }
}
