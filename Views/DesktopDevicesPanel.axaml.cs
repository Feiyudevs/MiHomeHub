using Avalonia.Controls;

namespace MiHome.Views;

/// <summary>桌面设备分区的内容：设备宫格 + 空态。样式与主窗口那张设备卡同源，只在这里覆盖成玻璃底。</summary>
public partial class DesktopDevicesPanel : UserControl
{
    public DesktopDevicesPanel()
    {
        InitializeComponent();
    }
}
