using Avalonia;
using Avalonia.Media;
using System;

namespace MiHome;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // WinUI 合成路径每帧都会新建 WinRT IAsyncAction 代理，这些代理被合成线程持有、GC 收不掉，
    // 实测空闲时 332 个/秒（约 8MB/3 分钟），托盘常驻几小时就能涨到 1GB 以上。
    // 换成 DirectComposition：同样走 GPU/DWM，但不碰 WinRT 异步对象，泄漏消失，画面无差异。
    // 别再试 RedirectionSurface —— 桌面分区窗在那条路径下一帧都不上屏，屏幕上只剩窗口自带的磨砂底，
    // 鼠标移进去触发一次重绘才出内容（可视树本身是好的，离屏渲染能画出完整卡片）。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.DirectComposition }
            })
            // 默认字族必须显式指定为系统里「名字能自解析、字形自足」的字体。
            // 用平台默认（Segoe UI）时，Avalonia 的字型缓存对不上名（请求名 ≠ 实际返回名），
            // 每排版一次都新建一份 GlyphTypeface + 整张 Name 表且释放不掉：
            // 实测空列表、无设备、只点下拉/开关的进程里也能堆出 3906 个 GlyphTypeface、
            // 20 万个字体名表对象（约 17MB 垃圾），滚动/交互越频繁涨得越快。
            // 这条同时覆盖 PopupRoot / ToolTip 等不继承 Window 字体的独立顶层。
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "Microsoft YaHei UI"
            })
            .LogToTrace();
}
