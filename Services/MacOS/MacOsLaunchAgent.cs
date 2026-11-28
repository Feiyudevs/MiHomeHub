// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MiHomeHub.Services.MacOS;

/// <summary>
/// macOS 开机启动 = 当前用户的 LaunchAgent（~/Library/LaunchAgents/com.mihomehub.app.plist）。
/// 对应 Windows 侧的 HKCU Run 键：都只对当前用户生效，不需要管理员权限。
/// <para>
/// 装在 LaunchAgents 而不是 LaunchDaemons：这是 GUI 程序，要能随用户登录在交互会话里跑。
/// ProgramType 用 Interactive 是为了让系统别把它当后台任务限流。
/// </para>
/// </summary>
internal static class MacOsLaunchAgent
{
    /// <summary>LaunchAgent 的 Label，同时用作 plist 文件名。</summary>
    private const string Label = "com.mihomehub.app";

    private const string FileName = "com.mihomehub.app.plist";

    /// <summary>
    /// plist 里写死的环境变量标记：launchd 拉起进程时带上它，
    /// 进程据此判断「我是被开机自启拉起来的那个副本」。
    /// </summary>
    private const string LaunchdMarkerEnv = "MIHOMEHUB_LAUNCHD";

    /// <summary>
    /// 当前进程是不是 launchd（开机自启）拉起来的。
    /// <para>
    /// 两个用途：(1) 这种进程不能去 bootout 那个 job——摘掉自己正在跑的那个 job 等于自杀；
    /// (2) 这种进程如果发现已有实例在跑，要安静退出，不能去把已有实例的主窗口顶出来
    /// （launchd 每次 bootstrap 都会拉起一个副本，用户看到的就是「刚启动就自己弹窗」）。
    /// </para>
    /// </summary>
    public static bool LaunchedByLaunchd =>
        OperatingSystem.IsMacOS() &&
        string.Equals(Environment.GetEnvironmentVariable(LaunchdMarkerEnv), "1", StringComparison.Ordinal);

    /// <summary>启动项当前是否指向传入的可执行文件路径。</summary>
    public static bool IsEnabled(string executablePath)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (string.IsNullOrEmpty(executablePath)) return false;
        try
        {
            var path = PlistPath();
            if (!File.Exists(path)) return false;
            var target = ReadProgramArgument(path);
            return target is not null && string.Equals(target, executablePath, StringComparison.Ordinal);
        }
        catch
        {
            // plist 损坏或读不了：按「没启用」处理，启动项在下次 Apply 时会被重写
            return false;
        }
    }

    /// <summary>
    /// 最近一次失败的原因（写不进 plist / launchd 不可用等）。
    /// <para>
    /// 启动期对齐（Sync）和设置页开关（Apply）失败时都会记在这里，
    /// 界面把它原样显示出来，而不是让用户对着一个「没生效」的开关猜。
    /// </para>
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>写入 / 删除启动项，返回是否成功。</summary>
    public static bool Apply(string executablePath, bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (!enabled) return Delete();
        if (string.IsNullOrEmpty(executablePath)) return false;
        try
        {
            var path = PlistPath();
            // 拿不到用户目录时会拼出相对路径，那会把 plist 写到当前工作目录，必须挡住
            if (!Path.IsPathRooted(path)) return false;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            WritePlist(path, executablePath);
            // 主动让 launchd 加载：plist 放好之后系统会自己发现，
            // 但那要等目录监听生效；显式 bootstrap 一次，登录自启立刻可用。
            // 自己就是 launchd 拉起来的进程时不能做（bootout 会把这个 job 摘掉 = 把自己杀了）。
            if (!LaunchedByLaunchd) LoadAgent(path);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            // 写不进 plist（目录只读、被占、权限被改）时不能把异常抛给界面：
            // 这里返回 false，由调用方把开关弹回去并把原因显示出来。
            LastError = DescribeError(ex);
            DesktopLog.Write($"[launchagent] 写入启动项失败：{LastError}");
            return false;
        }
    }

    /// <summary>
    /// 启动时对齐一次：升级或换安装目录后旧的可执行文件路径会失效，
    /// 这里让 plist 重新指向当前路径；关掉了开机启动就把残留的 plist 清掉。
    /// <para>
    /// plist 没变的时候**什么都不做**：早先这里每次启动都 bootout+bootstrap 一次，
    /// 而 plist 里 RunAtLoad=true —— bootstrap 会让 launchd 立刻再拉一个实例，
    /// 那个副本抢不到单实例锁，还会把已有实例的主窗口顶出来
    /// （实测：启动后 1 秒内出现第二个进程，主窗口自己弹了出来）。
    /// 只有 launchd 里压根没加载这个 job 时才补一次加载。
    /// </para>
    /// </summary>
    public static void Sync(string executablePath, bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (string.IsNullOrEmpty(executablePath)) return;
        if (!enabled)
        {
            Delete();
            return;
        }

        // 这一段必须整体兜住异常：它是启动期做的事，
        // ~/Library/LaunchAgents 不可写（受管设备 ACL、只读 home、目录被占）时
        // WritePlist 会抛 UnauthorizedAccessException —— 以前这个异常一路抛到
        // App.OnFrameworkInitializationCompleted，进程直接 SIGABRT，
        // 用户看到的是「双击没反应」，连主窗口都没有。
        // 开机自启失败只该是「没开上」，不该是「整个应用起不来」。
        try
        {
            var path = PlistPath();
            if (PlistMatches(path, executablePath))
            {
                // plist 内容没变：别去动 launchd。
                // launchd 里没加载这个 job 时补一次（系统重启前写的、或被 bootout 过）。
                if (!LaunchedByLaunchd && !IsAgentLoaded()) LoadAgent(path);
                LastError = null;
                return;
            }

            // plist 内容变了（可执行文件路径变了，或者键值有新内容）：
            // 重写并让 launchd 里的 job 跟上，否则下次登录还是旧内容。
            WritePlist(path, executablePath);
            if (!LaunchedByLaunchd) LoadAgent(path);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = DescribeError(ex);
            DesktopLog.Write($"[launchagent] 启动项同步失败（不影响应用启动）：{LastError}");
        }
    }

    /// <summary>把异常翻译成一句用户看得懂的话。</summary>
    private static string DescribeError(Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException => "没有权限写入 ~/Library/LaunchAgents，开机启动没开上",
            IOException => $"写入 ~/Library/LaunchAgents 失败（{ex.Message}）",
            _ => $"开机启动项注册失败（{ex.Message}）",
        };
    }

    /// <summary>
    /// 盘上的 plist 是否就是当前版本该写的那份。
    /// <para>
    /// 比较的是**整个文件内容**，不只是可执行文件路径：光比路径会漏掉键值层面的变化
    /// （比如给 launchd 拉起的进程打来源标记那次，路径没变但内容变了，
    /// 只比路径就永远不会写新标记，开机自启拉起来的进程还是会去 bootout 自己的 job）。
    /// </para>
    /// </summary>
    private static bool PlistMatches(string path, string executablePath)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var expected = BuildPlist(executablePath);
            var current = File.ReadAllBytes(path);
            if (current.Length != expected.Length) return false;
            for (var i = 0; i < expected.Length; i++)
            {
                if (current[i] != expected[i]) return false;
            }
            return true;
        }
        catch
        {
            // 读不了就当成「不一致」，重写一次总比留着旧内容强
            return false;
        }
    }

    private static bool Delete()
    {
        try
        {
            var path = PlistPath();
            // 先从 launchd 里摘掉：光删文件的话，已经加载的 agent 还会留着
            UnloadAgent();
            if (!File.Exists(path)) return true; // 本来就没有也算成功
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PlistPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", FileName);

    /// <summary>
    /// 原子写入 plist：先写临时文件再替换。
    /// 写到一半断电不会留下半截 plist——launchd 读到坏 plist 会直接忽略它，开机启动就静默失效了。
    /// </summary>
    private static void WritePlist(string path, string executablePath)
    {
        var xml = BuildPlist(executablePath);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, xml);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>生成 LaunchAgent 的 plist 内容（UTF-8 字节）。</summary>
    /// <remarks>
    /// 必须用显式 UTF-8 的 XmlWriter 落盘：XDocument.ToString() 走的是 StringWriter，
    /// 它会把 XML 头里的 encoding 改写成 utf-16，而文件字节仍是 UTF-8，
    /// plutil 与 launchd 会直接拒绝解析（开机自启静默失效）。
    /// </remarks>
    private static byte[] BuildPlist(string executablePath)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", new XElement("string", executablePath)),
                    // 登录时启动一次
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    // 进程自己退出了不要拉起来：GUI 程序崩了就等用户下次开
                    new XElement("key", "KeepAlive"), new XElement("false"),
                    // 标记：被 launchd 拉起的进程带上它，就不会去 bootout 自己的 job，
                    // 也不会在发现已有实例时把主窗口顶出来
                    new XElement("key", "EnvironmentVariables"),
                    new XElement("dict",
                        new XElement("key", LaunchdMarkerEnv), new XElement("string", "1")),
                    new XElement("key", "ProcessType"), new XElement("string", "Interactive"))));

        var settings = new XmlWriterSettings
        {
            // 不带 BOM 的纯 UTF-8：plist 规范也是这么要求的
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\n",
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 让 launchd 现在就加载这个 agent（失败也不影响下次登录自启）。
    /// <para>
    /// 注意：<c>launchctl bootstrap</c> 在同 Label 的 job 已加载时会直接失败
    /// （"service already loaded"），而且失败后 launchd 里留着的还是**旧 job**——
    /// 旧的可执行路径会让自启启动到旧版本上（实测踩过）。
    /// 所以这里先把旧 job 摘掉再 bootstrap。
    /// </para>
    /// </summary>
    private static void LoadAgent(string plistPath)
    {
        if (!File.Exists(plistPath)) return;
        if (RunningInsideAgentJob())
        {
            // 我们自己就是这个 job：bootout 会把正在跑的这个 job 摘掉 = 进程当场被杀
            //（实测：登录自启拉起来的进程每次启动都在这里把自己杀掉）。
            // 只能等下次登录让 launchd 重新加载新 plist。
            DesktopLog.Write("[launchagent] 当前进程就是 launchd job，跳过重载（下次登录生效）");
            return;
        }
        UnloadAgent();
        var output = RunLaunchctl($"bootstrap gui/{CurrentUserId()} \"{plistPath}\"");
        if (!string.IsNullOrEmpty(output))
        {
            DesktopLog.Write($"[launchagent] bootstrap 失败: {output}");
        }
    }

    /// <summary>
    /// 当前进程是不是正跑在 launchd 的这个 job 里。
    /// <para>
    /// 判据：<c>launchctl print</c> 里正在运行的 job 会列出它自己的 pid，
    /// 跟自己的 pid 一样就说明「我就是这个 job」。
    /// 这个判据不依赖 plist 里的环境标记，所以**第一次**从旧 plist 升级过来时也管用
    /// （旧 plist 没有标记，但照样会被 bootout 掉）。
    /// </para>
    /// </summary>
    private static bool RunningInsideAgentJob()
    {
        var output = RunLaunchctl($"print gui/{CurrentUserId()}/{Label}");
        if (string.IsNullOrEmpty(output)) return false;
        var pid = Environment.ProcessId.ToString();
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("pid = ", StringComparison.Ordinal)) continue;
            if (text.Substring("pid = ".Length).Trim() == pid) return true;
        }
        return false;
    }

    /// <summary>把 agent 从 launchd 里摘掉（关掉开机自启时用）。</summary>
    private static void UnloadAgent()
    {
        RunLaunchctl($"bootout gui/{CurrentUserId()}/{Label}");
    }

    /// <summary>
    /// launchd 里当前是否已经加载了这个 agent。
    /// <c>launchctl print</c> 成功（退出码 0）就是已加载。
    /// </summary>
    private static bool IsAgentLoaded()
    {
        return RunLaunchctlExitCode($"print gui/{CurrentUserId()}/{Label}") == 0;
    }

    /// <summary>跑 launchctl，返回它的输出（stdout+stderr 合并）；失败返回空串。</summary>
    private static string RunLaunchctl(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/launchctl", arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);
            return (stdout + stderr).Trim();
        }
        catch
        {
            // launchctl 不可用时不影响主流程：plist 已经在 LaunchAgents 目录里，
            // 下次登录系统自己会加载
            return "";
        }
    }

    /// <summary>跑 launchctl 并返回退出码（0 = 成功；launchctl 不可用时返回 -1）。</summary>
    private static int RunLaunchctlExitCode(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/launchctl", arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return -1;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int geteuid();

    private static int CurrentUserId() => geteuid();

    /// <summary>从 plist 里读回 ProgramArguments[0]（读不到返回 null）。</summary>
    private static string? ReadProgramArgument(string path)
    {
        var settings = new XmlReaderSettings
        {
            // Apple 的 plist 带 DOCTYPE 声明：必须显式允许忽略 DTD，否则读回来直接抛异常
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, settings);
        var doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict");
        if (dict is null) return null;

        // plist 的 dict 是 key / value 严格交替的，按两个一组读
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            if (children[i].Name.LocalName != "key" || children[i].Value != "ProgramArguments") continue;
            var array = children[i + 1];
            if (array.Name.LocalName != "array") return null;
            return array.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "string")?.Value;
        }
        return null;
    }
}
