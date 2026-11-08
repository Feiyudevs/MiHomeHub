using System;
using Avalonia;
using Avalonia.Controls;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 我的家庭浮层：由标题栏家庭胶囊唤出，与小爱 / 音乐浮层共用同一套开合动画与位置。
/// </summary>
public partial class HomePanelView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public HomePanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 若初始就是打开状态则直接落位（无动画）
        if (Vm is { IsHomeOpen: true })
        {
            HomePanel.IsVisible = true;
            HomePanel.Opacity = 1;
        }
    }

    /// <summary>
    /// 左沿对齐唤出按钮：由 MainWindow 传入按钮左沿在浮层坐标系中的位置（已夹在窗口内）。
    /// </summary>
    public void AlignToButton(double left)
    {
        HomePanel.Margin = new Thickness(left, 2, 0, 0);
    }

    /// <summary>卡片本体宽度（宿主 UserControl 是整窗宽，夹取必须用卡片宽）。</summary>
    public double CardWidth => HomePanel.Bounds.Width > 0 ? HomePanel.Bounds.Width : 260;

    public void Animate(bool open) => OverlayReveal.Run(HomePanel, open, () => Vm?.IsHomeOpen == true);
}
