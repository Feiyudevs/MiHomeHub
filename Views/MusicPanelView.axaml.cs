using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 音箱播放控制浮层：由标题栏音乐胶囊唤出。
/// 可见性由动画驱动（打开淡入轻落 / 关闭收起后隐藏），与语音浮层同一套开合手感。
/// </summary>
public partial class MusicPanelView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private bool _volumePointerInput;
    private bool _volumeKeyInput;
    private bool _volumeWheelInput;

    public MusicPanelView()
    {
        InitializeComponent();
        // 只提交真实输入期间的变化；按需重建或服务端回填不能被误当成音量操作。
        VolSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(VolSlider).Properties.IsLeftButtonPressed) _volumePointerInput = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolSlider.AddHandler(PointerReleasedEvent, (_, _) => _volumePointerInput = false,
            RoutingStrategies.Bubble, handledEventsToo: true);
        VolSlider.AddHandler(PointerCaptureLostEvent, (_, _) => _volumePointerInput = false,
            RoutingStrategies.Direct, handledEventsToo: true);
        VolSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) => _volumePointerInput = false,
            RoutingStrategies.Bubble, handledEventsToo: true);
        VolSlider.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                _volumeKeyInput = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolSlider.AddHandler(KeyUpEvent, (_, _) => _volumeKeyInput = false,
            RoutingStrategies.Bubble, handledEventsToo: true);
        VolSlider.AddHandler(PointerWheelChangedEvent, (_, _) =>
        {
            _volumeWheelInput = true;
            Dispatcher.UIThread.Post(() => _volumeWheelInput = false);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolSlider.LostFocus += (_, _) => _volumeKeyInput = false;
        DetachedFromVisualTree += (_, _) =>
            _volumePointerInput = _volumeKeyInput = _volumeWheelInput = false;
        VolSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty
                && (_volumePointerInput || _volumeKeyInput || _volumeWheelInput)
                && Vm?.MediaSpeaker is { } d)
                Vm.ScheduleSpeakerVolumeCommit(d);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 若初始就是打开状态则直接落位（无动画）
        if (Vm is { IsMusicOpen: true })
        {
            MusicPanel.IsVisible = true;
            MusicPanel.Opacity = 1;
        }
    }

    /// <summary>
    /// 左沿对齐唤出按钮：由 MainWindow 传入按钮左沿在浮层坐标系中的位置（已夹在窗口内）。
    /// </summary>
    public void AlignToButton(double left)
    {
        MusicPanel.Margin = new Thickness(left, 2, 0, 0);
    }

    /// <summary>卡片本体宽度（宿主 UserControl 是整窗宽，夹取必须用卡片宽）。</summary>
    public double CardWidth => MusicPanel.Bounds.Width > 0 ? MusicPanel.Bounds.Width : 344;

    public void Animate(bool open) => OverlayReveal.Run(MusicPanel, open, () => Vm?.IsMusicOpen == true);
}
