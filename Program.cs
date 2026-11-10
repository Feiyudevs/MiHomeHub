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
        // 隐藏诊断口：把场景/自动化候选接口全打一遍，原始响应落盘后退出，不启界面。
        // 见 Services/SceneProbe.cs；服务端结构确认后可连同该文件一起删掉。
        if (args.Any(a => string.Equals(a, "--scene-probe", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 设备组（group.xxx）成员信息探针：把设备列表原始响应落盘，确认组内成员从哪来。
        if (args.Any(a => string.Equals(a, "--group-probe", StringComparison.OrdinalIgnoreCase)))
        {
            GroupProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 启停接口探针：只做幂等写（把当前 enable 原样写回），确认接口名后再落业务代码。
        if (args.Any(a => string.Equals(a, "--scene-probe-enable", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunEnableAsync().GetAwaiter().GetResult();
            return;
        }

        // 编辑接口探针（阶段一）：只用不存在的 scene_id / 空 body 试，不改动任何真实场景。
        if (args.Any(a => string.Equals(a, "--scene-probe-edit", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunEditAsync().GetAwaiter().GetResult();
            return;
        }

        // 克隆探针：用真实自动化当模板，scene_id=0 + enable=false 新建一条测试副本，成功后立即删除。
        // 顺序探针：克隆多动作场景、颠倒顺序后新建，读回校验 order 是否生效，随后删除。
        if (args.Any(a => string.Equals(a, "--scene-probe-order", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunOrderAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Any(a => string.Equals(a, "--scene-probe-clone", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunCloneAsync().GetAwaiter().GetResult();
            return;
        }

        // 设备触发条件能力探针（只读）：确认按设备取自动化能力列表的云端接口。
        var tp = Array.FindIndex(args, a => string.Equals(a, "--trigger-probe", StringComparison.OrdinalIgnoreCase));
        if (tp >= 0)
        {
            var did = tp + 1 < args.Length && !args[tp + 1].StartsWith("--", StringComparison.Ordinal) ? args[tp + 1] : null;
            TriggerProbe.RunAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 触发条件可写性实验（新建禁用副本 → 读回 → 删除）。
        var tt = Array.FindIndex(args, a => string.Equals(a, "--trigger-test", StringComparison.OrdinalIgnoreCase));
        if (tt >= 0)
        {
            var did = tt + 1 < args.Length && !args[tt + 1].StartsWith("--", StringComparison.Ordinal) ? args[tt + 1] : null;
            TriggerProbe.RunTriggerTestAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 触发条件端到端验证：用正式代码生成触发源，建禁用副本 → 读回 → 删除。
        var te = Array.FindIndex(args, a => string.Equals(a, "--trigger-e2e", StringComparison.OrdinalIgnoreCase));
        if (te >= 0)
        {
            var did = te + 1 < args.Length && !args[te + 1].StartsWith("--", StringComparison.Ordinal) ? args[te + 1] : null;
            TriggerProbe.RunCatalogTestAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 弹窗 XAML 自检：直接实例化触发条件 / 添加动作弹窗，InitializeComponent 抛异常就落盘。
        if (args.Any(a => string.Equals(a, "--dialog-probe", StringComparison.OrdinalIgnoreCase)))
        {
            DialogProbe.Run();
            return;
        }

        // 创建链路探针：复刻 UI 新建场景 / 自动化的云端提交，看真实报错。
        if (args.Any(a => string.Equals(a, "--create-probe", StringComparison.OrdinalIgnoreCase)))
        {
            CreateProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 播放状态真伪实验：读 → pause → 再读，确认这台设备的 playing-state 是否恒为 1。
        var sp = Array.FindIndex(args, a => string.Equals(a, "--speaker-probe-pause", StringComparison.OrdinalIgnoreCase));
        if (sp >= 0)
        {
            var did = sp + 1 < args.Length ? args[sp + 1] : "";
            SpeakerProbe.RunPauseTestAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 单次读 playing-state：核对下发后云端多久落定。
        var st = Array.FindIndex(args, a => string.Equals(a, "--speaker-state", StringComparison.OrdinalIgnoreCase));
        if (st >= 0)
        {
            var did = st + 1 < args.Length ? args[st + 1] : "";
            SpeakerProbe.RunReadStateAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 设备级规格接口原始回包诊断：看 getspec 到底回了什么，导致 base64 解不开。
        var ss = Array.FindIndex(args, a => string.Equals(a, "--speaker-probe-spec", StringComparison.OrdinalIgnoreCase));
        if (ss >= 0)
        {
            var did = ss + 1 < args.Length ? args[ss + 1] : "";
            SpeakerProbe.RunSpecRawAsync(did).GetAwaiter().GetResult();
            return;
        }

        // 音箱播放状态只读探针：连读 playing-state / volume，落盘后退出。
        if (args.Any(a => string.Equals(a, "--speaker-probe", StringComparison.OrdinalIgnoreCase)))
        {
            SpeakerProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

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
