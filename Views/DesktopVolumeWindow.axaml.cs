using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MiHome.Models;
using MiHome.Services;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 桌面媒体分区的音量浮层：独立顶层小窗（贴桌面窗上的 Popup 会被压到普通窗口之下）。
/// 滑杆直接绑这台音箱的 SpeakerVolume，改动交给主视图模型那条 600ms 节流提交，
/// 与抽屉里的音量滑杆是同一条下发路径，不会漂出第二套音量逻辑。
/// </summary>
public partial class DesktopVolumeWindow : Window
{
    private PixelRect _anchor;
    private DateTime _openedAt;
    private MijiaDevice? _device;
    private MainViewModel? _main;

    /// <summary>滑杆初值回填（绑定生效那一帧）不能算用户改动，所以事件在布局完成之后才挂。</summary>
    private bool _armed;

    /// <summary>这枚浮层是为哪个分区开的，宿主关闭时据此配对收掉。</summary>
    public DesktopMediaVm? Target { get; private set; }

    public DesktopVolumeWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    public void Bind(DesktopMediaVm vm, PixelRect anchor)
    {
        Target = vm;
        _main = vm.Main;
        _device = vm.Speaker;
        _anchor = anchor;
        DataContext = _device;

        // 先给个「按钮正下方」的粗略落点，避免窗口先在屏幕左上角闪一帧
        Position = new PixelPoint(anchor.X, anchor.Bottom);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _openedAt = DateTime.UtcNow;

        PopupPlacement.AnchorTo(this, _anchor);
        Activate();

        if (_device != null && _main != null)
        {
            VolSlider.PropertyChanged += OnSliderChanged;
            _armed = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_armed) VolSlider.PropertyChanged -= OnSliderChanged;
        _armed = false;
        base.OnClosed(e);
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_armed || e.Property != Slider.ValueProperty) return;
        if (_device != null && _main != null) _main.ScheduleSpeakerVolumeCommit(_device);
    }

    /// <summary>
    /// 宿主分区窗是 NOACTIVATE，Show 之后系统可能立刻补一条 WA_INACTIVE ——
    /// 开场 300ms 内的失活一律忽略，否则浮层刚出来就被吞掉。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 300) return;
        Close();
    }
}
