using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MiHome.ViewModels;

namespace MiHome.Views;

/// <summary>
/// 分区重命名的输入浮层。
/// 贴桌面的分区窗是 Progman 的从属窗口：它一旦被激活，整个 Progman 窗口集（包括其它桌面组件
/// 的分区窗口）都会一起抬到普通软件之上。所以键盘输入必须交给这个独立的普通窗口，
/// 分区窗全程保持 NOACTIVATE，点击任何东西都不会抬起桌面层。
/// </summary>
public partial class DesktopRenameWindow : Window
{
    /// <summary>浮层相对名称左边多出来的留白（DIP）：输入框内距 8，让文字落在原位。</summary>
    private const double PadDip = 8;

    /// <summary>浮层高度与名称行高之差的一半（DIP）：垂直居中盖住原名称。</summary>
    private const double CenterDip = 7;

    private PixelRect _anchor;
    private DateTime _openedAt;
    private bool _closing;

    /// <summary>这枚浮层在改哪个分区，宿主据此配对 / 关闭。</summary>
    public DesktopZoneVm? Target { get; private set; }

    public DesktopRenameWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    public void Bind(DesktopZoneVm vm, PixelRect anchor)
    {
        Target = vm;
        DataContext = vm;
        _anchor = anchor;
        NameBox.Text = vm.Name;

        // 先落到名称上，避免在屏幕左上角闪一帧
        Position = new PixelPoint(anchor.X, anchor.Y);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _openedAt = DateTime.UtcNow;

        // Show 之后才知道真实缩放：宽度按名称实际宽度给，短名字也不至于只剩一条缝
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        Width = Math.Clamp(_anchor.Width / scale + PadDip * 2, 120, 240);
        Position = new PixelPoint(
            _anchor.X - (int)Math.Round(PadDip * scale),
            _anchor.Y - (int)Math.Round(CenterDip * scale));

        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    /// <summary>失焦即提交：与原内联改名的「点窗外提交」保持一致。</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing) return;
        // 刚 Show 出来时系统可能补一条 WA_INACTIVE，别把浮层直接吞掉
        if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 200) return;
        Commit();
    }

    private void Commit()
    {
        if (_closing) return;
        _closing = true;

        var name = NameBox.Text?.Trim() ?? "";
        if (Target != null && name.Length > 0 && name != Target.Name)
            Target.Name = name.Length > 24 ? name[..24] : name;

        Close();
    }

    private void Cancel()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
