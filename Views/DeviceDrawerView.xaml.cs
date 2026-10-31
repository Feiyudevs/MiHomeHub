using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Threading.Tasks;

namespace MiHome.Views;

public partial class DeviceDrawerView : UserControl
{
    private const double Slide = 430;
    private static readonly Easing Ease = new CubicEaseOut();

    public DeviceDrawerView()
    {
        InitializeComponent();
    }

    public void Animate(bool open)
    {
        var drawer = DeviceDrawer;
        if (drawer?.RenderTransform is not TranslateTransform tx) return;

        var duration = TimeSpan.FromMilliseconds(open ? 300 : 230);

        drawer.IsVisible = true;
        tx.X = open ? Slide : 0;

        var slideAnim = new Animation
        {
            Duration = duration,
            Easing = Ease,
            FillMode = FillMode.Forward
        };
        slideAnim.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Setter(TranslateTransform.XProperty, open ? 0 : Slide) }
        });

        _ = slideAnim.RunAsync(drawer).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                tx.X = 0;
                if (!open) drawer.IsVisible = false;
            }));
    }
}
