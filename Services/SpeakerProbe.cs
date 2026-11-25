// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MiHomeHub.Models;

namespace MiHomeHub.Services;

/// <summary>
/// 音箱播放状态只读探针：把每台音箱的 playing-state / volume 规格与云端回读值连读几次落盘，
/// 用来判断「没在播放却显示播放中」是规格映射的问题，还是设备本身一直回 1。
///
/// 用法：MiHomeHub.exe --speaker-probe
/// 输出：%APPDATA%\Feiyu\MiHomeHub\speaker-probe\summary.txt
/// </summary>
internal static class SpeakerProbe
{
    public static async Task RunAsync()
    {
        var dir = Path.Combine(AppPaths.Dir, "speaker-probe");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "summary.txt");
        var sb = new StringBuilder();
        void W(string s)
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }

        void Flush() => File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

        W($"音箱播放状态探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken))
        {
            W("!! 没有登录态");
            Flush();
            return;
        }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync())
        {
            W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}");
            Flush();
            return;
        }

        var client = new MijiaClient(auth);
        var devices = await client.GetAllDevicesAsync();
        var speakers = devices.Where(d => (d.SpecType ?? "").Contains("speaker", StringComparison.Ordinal)).ToList();
        W($"设备总数 {devices.Count}，候选音箱 {speakers.Count}");

        foreach (var d in speakers)
        {
            W("");
            W($"== {d.Name}  did={d.Did}  target={d.TargetDid}  model={d.Model}  在线={d.IsOnline}");
            W($"   spec={d.SpecType}");

            DeviceSpec? spec = null;
            try
            {
                spec = DeviceSpecService.ParseRoot(await client.GetSpecAsync(d.Did));
                W($"   getspec(设备级) 有效={spec.Valid} 属性数={spec.Properties.Count}");
            }
            catch (Exception ex)
            {
                W($"   getspec(设备级) 失败：{ex.GetType().Name}: {ex.Message}");
            }

            // 复刻 DeviceSpecService 的兜底：设备级失败就用公开规格站
            if (spec is not { Valid: true } && !string.IsNullOrEmpty(d.SpecType))
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    var url = "https://miot-spec.org/miot-spec-v2/instance?type=" + Uri.EscapeDataString(d.SpecType);
                    var json = await http.GetStringAsync(url);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    spec = DeviceSpecService.ParseRoot(doc.RootElement);
                    W($"   公开规格站 有效={spec.Valid} 属性数={spec.Properties.Count}");
                }
                catch (Exception ex)
                {
                    W($"   公开规格站 失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (spec != null && spec.Valid)
            {
                foreach (var p in spec.Properties.Where(p => p.Name is "playing-state" or "volume" or "mute"))
                {
                    var opts = string.Join(" / ", p.Options.Select(o => $"{o.Value}={o.Label}"));
                    W($"   prop siid={p.Siid} piid={p.Piid} name={p.Name} fmt={p.Format} " +
                      $"read={p.Readable} write={p.Writable} opts=[{opts}]");
                }
            }

            var play = spec?.PlayingState;
            if (play == null)
            {
                W("   !! 规格里没有 playing-state，App 不会回读播放状态（IsPlaying 只会跟着下发指令猜）");
                continue;
            }

            var keys = new[] { (d.Did, play.Siid, play.Piid) }.ToList();
            var volume = spec!.SpeakerVolume;
            if (volume is { } v) keys.Add((d.Did, v.Siid, v.Piid));

            // 连读三次（间隔 3 秒），看云端值是否稳定、是否一直回 1
            for (var i = 1; i <= 3; i++)
            {
                try
                {
                    var map = await client.ReadPropsAsync(keys);
                    var ps = map.TryGetValue((d.Did, play.Siid, play.Piid), out var pv) ? pv : null;
                    var vol = volume is { } vv && map.TryGetValue((d.Did, vv.Siid, vv.Piid), out var volv) ? volv : null;
                    W($"   第 {i} 次回读：playing-state={Show(ps)}（siid={play.Siid} piid={play.Piid}） volume={Show(vol)}");
                }
                catch (Exception ex)
                {
                    W($"   第 {i} 次回读失败：{ex.GetType().Name}: {ex.Message}");
                }
                if (i < 3) await Task.Delay(3000);
            }
        }

        W("");
        W("判定口径：0=停止 1=播放中 2=暂停（MIoT play-control playing-state）");
        Flush();
        Console.WriteLine($"已写出：{outPath}");
    }

    private static string Show(object? v) => v switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => v.ToString()!
    };

    /// <summary>
    /// 播放状态真伪实验：读一次 playing-state → 下发 pause（siid 3 / aiid 3）→ 等 5 秒再读。
    /// 值若从 1 变成 0/2，说明设备真会如实回状态；仍是 1，则这台设备的 playing-state 恒为 1。
    /// </summary>
    public static async Task RunPauseTestAsync(string did)
    {
        var dir = Path.Combine(AppPaths.Dir, "speaker-probe");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        void W(string s)
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync()) { W("!! 续期失败"); return; }
        var client = new MijiaClient(auth);

        var device = (await client.GetAllDevicesAsync()).FirstOrDefault(d => d.Did == did);
        if (device == null) { W($"!! 找不到设备 {did}"); return; }

        W($"设备：{device.Name} ({device.Did})");
        var before = await Read(client, did);
        W($"暂停前 playing-state = {before}");

        await client.RunActionAsync(did, 3, 3);
        W("已下发 pause（siid=3 aiid=3），等待 5 秒");
        await Task.Delay(5000);

        var after = await Read(client, did);
        W($"暂停后 playing-state = {after}");
        W(after == "1" ? "结论：这台设备的 playing-state 恒为 1，不能当作播放判断依据"
                       : "结论：设备会如实回状态，之前的 1 是真在播");

        File.WriteAllText(Path.Combine(dir, "pause-test.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static async Task<string> Read(MijiaClient client, string did)
    {
        var map = await client.ReadPropsAsync(new List<(string, int, int)> { (did, 3, 1) });
        return map.TryGetValue((did, 3, 1), out var v) ? Show(v) : "<null>";
    }

    /// <summary>
    /// 文本指令探针：给指定 did（留空则全部音箱）下发 execute-text-directive，
    /// 把云端原始回包（含每条 code / message）落盘，区分「没送到设备」还是「送到了但设备不执行」。
    ///
    /// 用法：MiHomeHub.exe --speaker-directive-probe [did] [文本]
    /// 输出：%APPDATA%\Feiyu\MiHomeHub\speaker-probe\directive.txt
    /// </summary>
    public static async Task RunDirectiveProbeAsync(string? did, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = "现在几点了";

        var dir = Path.Combine(AppPaths.Dir, "speaker-probe");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        void W(string s)
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }
        void Flush() => File.WriteAllText(Path.Combine(dir, "directive.txt"), sb.ToString(), new UTF8Encoding(false));

        W($"文本指令探针  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  文本=\"{text}\"");

        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { W("!! 没有登录态"); Flush(); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync()) { W($"!! 续期失败：{auth.LastRefreshError ?? "未知"}"); Flush(); return; }

        var client = new MijiaClient(auth);
        var devices = await client.GetAllDevicesAsync();
        var speakers = devices.Where(d => (d.SpecType ?? "").Contains("speaker", StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrWhiteSpace(did))
            speakers = speakers.Where(d => d.Did == did).ToList();
        if (speakers.Count == 0) { W($"!! 没有匹配的音箱（did={did}）"); Flush(); return; }

        foreach (var d in speakers)
        {
            W("");
            W($"== {d.Name}  did={d.Did}  model={d.Model}  在线={d.IsOnline}");
            W($"   spec={d.SpecType}");

            DeviceSpec? spec = null;
            try { spec = DeviceSpecService.ParseRoot(await client.GetSpecAsync(d.Did)); }
            catch (Exception ex) { W($"   getspec(设备级) 失败：{ex.GetType().Name}"); }

            if (spec is not { Valid: true } && !string.IsNullOrEmpty(d.SpecType))
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    var json = await http.GetStringAsync("https://miot-spec.org/miot-spec-v2/instance?type=" + Uri.EscapeDataString(d.SpecType));
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    spec = DeviceSpecService.ParseRoot(doc.RootElement);
                }
                catch (Exception ex) { W($"   公开规格站 失败：{ex.GetType().Name}"); }
            }

            if (spec is not { Valid: true }) { W("   !! 拿不到规格，跳过"); continue; }

            var text_ = spec.FindAction("execute-text-directive");
            foreach (var a in spec.Actions.Where(a => a.Name.Contains("text", StringComparison.Ordinal) ||
                                                      a.Name.Contains("music", StringComparison.Ordinal) ||
                                                      a.Name is "play" or "pause" or "wake-up" or "play-radio"))
                W($"   action siid={a.Siid} aiid={a.Aiid} name={a.Name} 入参={a.InputCount}");

            if (text_ == null) { W("   !! 规格里没有 execute-text-directive，App 会判「不支持文本指令」"); continue; }

            // 一次下发三种入参形态，看云端 code 是否不同（成功一般是 0）
            var variants = new (string Label, object Args)[]
            {
                ("[文本,false]", new object[] { text, false }),
                ("[文本,true]", new object[] { text, true }),
                ("[文本]", new object[] { text }),
            };

            // 探针里的设备没经过 ApplySpec，TargetDid 还是空的，直接打 did 即可（音箱没有虚拟子设备）
            var target = string.IsNullOrEmpty(d.TargetDid) ? d.Did : d.TargetDid;

            foreach (var v in variants)
            {
                try
                {
                    var raw = await client.RequestRawAsync("/miotspec/action", new
                    {
                        @params = new { did = target, siid = text_.Siid, aiid = text_.Aiid, @in = v.Args }
                    });
                    W($"   {v.Label} → {raw.GetRawText()}");
                }
                catch (Exception ex)
                {
                    W($"   {v.Label} → 异常 {ex.GetType().Name}: {ex.Message}");
                }
                await Task.Delay(2000);
            }
        }

        W("");
        W("判定口径：外层 code=0 且 result 里每条 code=0 才是设备真的收下了；result 带负 code 说明设备/固件不支持这条动作。");
        Flush();
        Console.WriteLine($"已写出：{Path.Combine(dir, "directive.txt")}");
    }

    /// <summary>
    /// 单次读某台设备的 playing-state（3/1），用于核对下发后云端多久落定。
    ///
    /// 用法：MiHomeHub.exe --speaker-state &lt;did&gt;
    /// </summary>
    public static async Task RunReadStateAsync(string did)
    {
        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { Console.WriteLine("!! 没有登录态"); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync()) { Console.WriteLine("!! 续期失败"); return; }
        var client = new MijiaClient(auth);
        var map = await client.ReadPropsAsync(new List<(string, int, int)> { (did, 3, 1) });
        var v = map.TryGetValue((did, 3, 1), out var val) ? Show(val) : "<null>";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} playing-state={v}");
    }

    /// <summary>
    /// 设备级规格接口原始回包诊断：getspec 一直报 Base64 解不开，这里把原始字节头部落盘，
    /// 看它到底是 JSON / gzip / 被 RC4 加密的 base64，再决定怎么解。
    ///
    /// 用法：MiHomeHub.exe --speaker-probe-spec &lt;did&gt;
    /// 输出：%APPDATA%\Feiyu\MiHomeHub\speaker-probe\spec-raw.txt
    /// </summary>
    public static async Task RunSpecRawAsync(string did)
    {
        var dir = Path.Combine(AppPaths.Dir, "speaker-probe");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        var auth = MijiaAuth.LoadOrEmpty();
        if (string.IsNullOrEmpty(auth.Auth.ServiceToken)) { Console.WriteLine("!! 没有登录态"); return; }
        if (!auth.HasValidAuth() && !await auth.RefreshServiceTokenAsync()) { Console.WriteLine("!! 续期失败"); return; }
        var client = new MijiaClient(auth);

        var (bytes, nonce) = await client.PostSignedAsync("/miotspec/device/getspec",
            new { did, force = false, language = "zh_CN" });
        sb.AppendLine($"did={did}  字节数={bytes.Length}");

        // 依次试几个候选路径，看哪个真的返回规格（当前 /miotspec/device/getspec 回 404）
        foreach (var uri in new[]
                 {
                     "/miotspec/device/getspec",
                     "/miotspec/specv2/device/getspec",
                     "/miotspec/specv2/getspec",
                     "/miotspec/device/getspeclist",
                     "/miotspec/spec/device/getspec",
                 })
        {
            try
            {
                var (b, n) = await client.PostSignedAsync(uri, new { did, force = false, language = "zh_CN" });
                var text = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 120)).Replace("\n", " ");
                sb.AppendLine($"  {uri} → {b.Length} 字节：{text}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {uri} → 异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine("");
        sb.AppendLine($"前 4 字节(hex)={string.Join(" ", bytes.Take(4).Select(b => b.ToString("X2")))}  gzip={bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B}");
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 400));
        sb.AppendLine("-- 原始文本头 --");
        sb.AppendLine(head);

        // 试 RC4 解密
        try
        {
            var signed = MijiaCrypto.SignedNonce(auth.Auth.SSecurity, nonce);
            var dec = MijiaCrypto.Rc4Decrypt(signed, Encoding.UTF8.GetString(bytes).Trim());
            sb.AppendLine($"-- RC4 解密后 {dec.Length} 字节 --");
            sb.AppendLine(Encoding.UTF8.GetString(dec, 0, Math.Min(dec.Length, 400)));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"-- RC4 解密失败：{ex.GetType().Name}: {ex.Message}");
        }

        var outPath = Path.Combine(dir, "spec-raw.txt");
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"已写出：{outPath}");
    }
}
