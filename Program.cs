using Avalonia;
using System;

namespace MiHome;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // WinUI 合成路径每帧都会新建 WinRT IAsyncAction 代理，这些代理被合成线程持有、GC 收不掉，
    // 实测空闲时 332 个/秒（约 8MB/3 分钟），托盘常驻几小时就能涨到 1GB 以上。
    // 换成 DWM 重定向表面：依旧走 GPU，但绕开 WinRT 异步对象，泄漏随之消失。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface }
            })
            .LogToTrace();
}
