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
    /// <summary>
    /// 浮层相对名称左边多出来的留白（DIP），同时是输入框的左内距：
    /// 两者相等时文字正好落回原名位置，而卡片左侧只留出一条窄缝、不再压住标题栏的竖条。
    /// </summary>
    private const double PadDip = 4;

    /// <summary>浮层紧凑高度（DIP）：CJK 名称文字墨迹约 14，配 LineHeight 15 后贴字面即可。</summary>
    private const double BoxHeightDip = 18;

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

        // Show 之后才知道真实缩放：宽度按名称实际宽度给，高度固定紧凑、以文字中心对齐
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        Width = Math.Clamp(_anchor.Width / scale + PadDip * 2, 72, 240);
        Height = BoxHeightDip;

        var anchorCenterY = _anchor.Y + _anchor.Height / 2.0;
        Position = new PixelPoint(
            _anchor.X - (int)Math.Round(PadDip * scale),
            (int)Math.Round(anchorCenterY - BoxHeightDip * scale / 2.0));

        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Input);

        // 布局真正跑完后，用卡片实测到的屏幕位置做一次校正
        Dispatcher.UIThread.Post(LogGeometry, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 校正浮层位置：按常量估的偏移在「客户区被 Fluent 模板撑高」时会偏，
    /// 所以直接量出卡片当前真正落在哪，再整体挪到名称的垂直中心上。
    /// </summary>
    private void CorrectOntoAnchor()
    {
        if (_closing) return;

        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var cardOnScreen = Card.PointToScreen(new Point(0, 0));
        var cardH = (int)Math.Round(Card.Bounds.Height * scale);
        var target = new PixelPoint(
            _anchor.X - (int)Math.Round(PadDip * scale),
            _anchor.Y + _anchor.Height / 2 - cardH / 2);

        Position = new PixelPoint(
            Position.X + target.X - cardOnScreen.X,
            Position.Y + target.Y - cardOnScreen.Y);
    }

    private void LogGeometry()
    {
        CorrectOntoAnchor();

        try
        {
            var scale = RenderScaling > 0 ? RenderScaling : 1;
            var cardOnScreen = Card.PointToScreen(new Point(0, 0));
            var line = $"anchor=({_anchor.X},{_anchor.Y},{_anchor.Width},{_anchor.Height}) " +
                       $"pos=({Position.X},{Position.Y}) winBounds=({Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}) " +
                       $"win.Height={Height} cardH={Card.Bounds.Height} cardOnScreen=({cardOnScreen.X},{cardOnScreen.Y}) " +
                       $"boxH={NameBox.Bounds.Height} scale={scale}\n";
            System.IO.File.AppendAllText(@"I:\AICode\MiHomeWin\rename_dbg.txt", line);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(@"I:\AICode\MiHomeWin\rename_dbg.txt", "ERR " + ex + "\n");
        }
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
