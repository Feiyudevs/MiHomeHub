using Avalonia;
using Avalonia.Media;
using MiHome.Services;
using System;
using System.Runtime.Versioning;

namespace MiHome;

[SupportedOSPlatform("windows")]
internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例：已有常驻进程时唤起它的主界面，自己直接退出
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalShowMain();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 常驻控制台默认使用软件渲染，避免为低频界面常驻 ANGLE/D3D 驱动资源。
    // 本机同界面隔离实测约省 119 MiB 私有提交；显式 --gpu 可回到 DirectComposition。
    // COM 调试登记表的持续增长由 csproj 的 Release 运行时选项单独规避。
    private static bool UseGpuRendering => Environment.GetCommandLineArgs().Any(argument =>
        argument is "--gpu");

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = UseGpuRendering
                    ? new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
                    : new[] { Win32RenderingMode.Software },
                CompositionMode = UseGpuRendering
                    ? new[] { Win32CompositionMode.DirectComposition }
                    : new[] { Win32CompositionMode.RedirectionSurface }
            })
            // 默认字族必须显式指定为系统里「名字能自解析、字形自足」的字体。
            // 用平台默认（Segoe UI）时，Avalonia 的字型缓存对不上名（请求名 ≠ 实际返回名），
            // 每排版一次都新建一份 GlyphTypeface + 整张 Name 表且释放不掉：
            // 实测空列表、无设备、只点下拉/开关的进程里也能堆出 3906 个 GlyphTypeface、
            // 20 万个字体名表对象（约 17MB 垃圾），滚动/交互越频繁涨得越快。
            // 这条同时覆盖 PopupRoot / ToolTip 等不继承 Window 字体的独立顶层。
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "Microsoft YaHei UI",
                // 11.3.9 平台缺字匹配会重复创建 GlyphTypeface，HarfBuzz 回调使其无法回收。
                // 显式回退优先走字体缓存，也覆盖控件自行设置其他字体时的中文缺字。
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") }
                }
            })
            .LogToTrace();
}
