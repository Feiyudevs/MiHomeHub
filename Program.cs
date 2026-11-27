// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Media;
using MiHomeHub.Services;
using MiHomeHub.Services.Setup;
using System;
using System.Runtime.Versioning;

namespace MiHomeHub;

[SupportedOSPlatform("windows")]
internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 安装/卸载入口必须早于单实例、托盘与所有探针：--setup / --uninstall 参数由这里接管
        if (SetupEntry.TryRun(args)) return;

        // 更新的无界面安装：不启 Avalonia、不建窗口，跑完事务后拉起新版本再结束本进程
        if (SetupEntry.Active && SetupEntry.Options is { Silent: true } silentOptions)
            Environment.Exit(SilentSetup.Run(silentOptions));

        // 品牌改名后首次启动：把旧 MiHome 目录的登录态/缓存/偏好搬到 MiHomeHub（仅迁移一次）
        AppPaths.TryMigrateLegacy();

        // 内置摄像头画面链路自检（只读）：释放内置画面源、硬解取帧、落一张样张后退出。
        var clp = Array.FindIndex(args, a => a.Equals("--camera-live-probe", StringComparison.OrdinalIgnoreCase));
        if (clp >= 0)
        {
            var rest = args.Skip(clp + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var did = rest.Length > 0 ? rest[0] : null;
            var count = rest.Length > 1 ? int.TryParse(rest[1], out var n) ? n : 60 : 60;
            Services.CameraMedia.CameraLiveProbe.RunAsync(did, count).GetAwaiter().GetResult();
            return;
        }

        // 内置摄像头声音自检（只读）：起画面源、走 RTSP 收音频、落一段 WAV 后退出。
        var cap = Array.FindIndex(args, a => a.Equals("--camera-audio-probe", StringComparison.OrdinalIgnoreCase));
        if (cap >= 0)
        {
            var rest = args.Skip(cap + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var did = rest.Length > 0 ? rest[0] : null;
            var seconds = rest.Length > 1 ? int.TryParse(rest[1], out var s) ? s : 4 : 4;
            Services.CameraMedia.CameraLiveProbe.RunAudioAsync(did, seconds).GetAwaiter().GetResult();
            return;
        }

        // 隐藏诊断口：把场景/自动化候选接口全打一遍，原始响应落盘后退出，不启界面。
        // 见 Services/SceneProbe.cs；服务端结构确认后可连同该文件一起删掉。
        if (args.Any(a => string.Equals(a, "--scene-probe", StringComparison.OrdinalIgnoreCase)))
        {
            SceneProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 热水器「使用记录」接口探针（只读）：把设备上报历史/操作记录/统计接口逐个打一遍，落盘原始响应。
        if (args.Any(a => string.Equals(a, "--usage-probe", StringComparison.OrdinalIgnoreCase)))
        {
            UsageProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 设备组（group.xxx）成员信息探针：把设备列表原始响应落盘，确认组内成员从哪来。
        if (args.Any(a => string.Equals(a, "--group-probe", StringComparison.OrdinalIgnoreCase)))
        {
            GroupProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 设备拟物图来源探针（只读）：原始 icon 字段 + productconfig 的 icon_real。
        var ip = Array.FindIndex(args, a => string.Equals(a, "--icon-probe", StringComparison.OrdinalIgnoreCase));
        if (ip >= 0)
        {
            var extra = args.Skip(ip + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            IconProbe.RunAsync(extra).GetAwaiter().GetResult();
            return;
        }

        // 设备原始响应导出（只读）：把某个型号设备的完整原始字段落盘，确认图片字段的真实名字。
        var rpr = Array.FindIndex(args, a => string.Equals(a, "--icon-probe-raw", StringComparison.OrdinalIgnoreCase));
        if (rpr >= 0)
        {
            var filter = args.Skip(rpr + 1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
            IconProbe.RunRawAsync(filter).GetAwaiter().GetResult();
            return;
        }

        // 产品配置原始字段探针（只读）：确认某个型号在云端到底有哪些图标字段。
        var pp = Array.FindIndex(args, a => string.Equals(a, "--product-probe", StringComparison.OrdinalIgnoreCase));
        if (pp >= 0)
        {
            var models = args.Skip(pp + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            IconProbe.RunProductRawAsync(models).GetAwaiter().GetResult();
            return;
        }

        // 米家插件元数据探针（只读）：确认型号的插件包下载地址/版本，用于核对列表图来源。
        var plp = Array.FindIndex(args, a => string.Equals(a, "--plugin-probe", StringComparison.OrdinalIgnoreCase));
        if (plp >= 0)
        {
            var models = args.Skip(plp + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            IconProbe.RunPluginRawAsync(models).GetAwaiter().GetResult();
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

        // 文本指令探针：下发 execute-text-directive 并把云端原始回包（含 code）打印出来。
        var sd = Array.FindIndex(args, a => string.Equals(a, "--speaker-directive-probe", StringComparison.OrdinalIgnoreCase));
        if (sd >= 0)
        {
            var did = sd + 1 < args.Length && !args[sd + 1].StartsWith("--", StringComparison.Ordinal) ? args[sd + 1] : "";
            var text = sd + 2 < args.Length ? args[sd + 2] : "";
            SpeakerProbe.RunDirectiveProbeAsync(did, text).GetAwaiter().GetResult();
            return;
        }

        // 音箱播放状态只读探针：连读 playing-state / volume，落盘后退出。
        if (args.Any(a => string.Equals(a, "--speaker-probe", StringComparison.OrdinalIgnoreCase)))
        {
            SpeakerProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 单实例：已有常驻进程时唤起它的主界面，自己直接退出
        // 安装界面是独立任务，不抢主程序互斥体（否则托盘实例会把它顶回去）
        if (!SetupEntry.Active && !SingleInstance.TryAcquire())
        {
            SingleInstance.SignalShowMain();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 渲染管线默认走显卡（AngleEgl + WinUIComposition），拖窗口和滚动不会再靠 CPU 逐帧重绘。
    // 代价是常驻 ANGLE/D3D 资源（同界面约多 119 MiB 私有提交）；要省内存可在设置里关掉硬件加速，
    // 或命令行带 --lowmem。没有可用显卡时 Avalonia 会自动回退软件渲染，所以默认打开不会让老机器起不来。
    // 渲染管线只在启动时决定，改设置后必须重启进程才生效。
    // COM 调试登记表的持续增长由 csproj 的 Release 运行时选项单独规避。
    private static bool UseGpuRendering
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Any(argument => argument is "--lowmem")) return false;
            if (args.Any(argument => argument is "--gpu")) return true;
            return AppSettingsStore.UseGpu;
        }
    }

    /// <summary>
    /// 合成后端：默认用 WinUIComposition（Windows.UI.Composition），**不再默认走 DirectComposition**。
    /// 11.3.x 的 DirectComposition 合成循环不按垂直同步等待（WaitForCommitCompletion 常立即返回），
    /// 窗口最小化/收起进托盘后仍会满速空转：实测常驻吃满 1 个核，dwm.exe 也被拖着常年 20%~30%，
    /// 开机自启那一下整机跟着掉帧（表现为「一启动电脑就卡一下」）。上游 #16750/#20873 的结论与建议
    /// 都是把 DirectComposition 从合成列表里去掉。仍想复现旧行为可带 --directcomp。
    /// </summary>
    private static bool UseDirectComposition =>
        Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--directcomp", StringComparison.OrdinalIgnoreCase));

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = UseGpuRendering
                    ? new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
                    : new[] { Win32RenderingMode.Software },
                // Win10 17134 起才支持 WinUIComposition；老版本会自动落到 RedirectionSurface。
                // 顺序里不放 DirectComposition：它在 Win11 上同样有满速空转问题。
                CompositionMode = UseGpuRendering
                    ? (UseDirectComposition
                        ? new[] { Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface }
                        : new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface })
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
