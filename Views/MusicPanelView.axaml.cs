using System;
using Avalonia;
using Avalonia.Controls;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 音箱播放控制浮层：由标题栏音乐胶囊唤出。
/// 可见性由动画驱动（打开淡入轻落 / 关闭收起后隐藏），与语音浮层同一套开合手感。
/// </summary>
public partial class MusicPanelView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MusicPanelView()
    {
        InitializeComponent();
        // 滑条值一变就交给统一的 600ms 防抖提交；回填的同值下发是幂等的，不必额外区分来源
        VolSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && Vm?.MediaSpeaker is { } d)
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

    public void Animate(bool open) => OverlayReveal.Run(MusicPanel, open, () => Vm?.IsMusicOpen == true);
}
