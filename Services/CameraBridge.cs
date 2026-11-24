// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MiHome.Models;

namespace MiHome.Services;

/// <summary>
/// 摄像头画面桥。
///
/// 米家云端（api.mijia.tech）只提供 MIoT 属性读写：开关、移动侦测、云存储开关这些能拿到，
/// **画面拿不到**——实时流走的是本地 P2P（MISS/TUTK 固件，或 MIoT-Spec 的 p2p-stream / start-stream
/// SRTP 通道），必须由带 P2P 原生库的实现去握手解密，云端 HTTP 接口不含视频数据。
///
/// 因此这里的做法是接一个本地 go2rtc（https://github.com/AlexxIT/go2rtc，v1.9.13 起内置
/// xiaomi/miss 拉流）：go2rtc 用小米账号登录拿密钥，在局域网直连摄像头，转成
/// WebRTC / RTSP。本类只负责探测它与拼地址，视频由 go2rtc 自己的页面渲染。
/// </summary>
public static class CameraBridge
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>go2rtc 根地址（设置里可改），默认本机 1984。</summary>
    public static string BaseUrl
    {
        get
        {
            var raw = (AppSettingsStore.Go2rtcBase ?? "").Trim();
            if (raw.Length == 0) raw = "http://127.0.0.1:1984";
            return raw.TrimEnd('/');
        }
    }

    /// <summary>go2rtc 控制台（添加小米账号、查看已发现设备）。</summary>
    public static string ConsoleUrl => BaseUrl + "/";

    /// <summary>预览页：WebRTC 优先，浏览器原生渲染，不用额外解码器。</summary>
    public static string PlayUrl(string stream) =>
        $"{BaseUrl}/webrtc.html?src={Uri.EscapeDataString(stream)}";

    /// <summary>
    /// 约定：go2rtc 里的流名直接用摄像头 did，界面按钮就不用让用户再填一次名字。
    /// did 里只有数字和点，不需要额外转义。
    /// </summary>
    public static string StreamName(MijiaDevice device) => device.Did;

    /// <summary>探测 go2rtc 是否在线：读 /api/streams（空网关也返回 {}，能连上就是在线）。</summary>
    public static async Task<bool> PingAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/api/streams");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>已配置的流名；go2rtc 未启动或没有流时返回空列表。</summary>
    public static async Task<List<string>> GetStreamNamesAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(BaseUrl + "/api/streams");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new List<string>();
            return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>用默认浏览器打开（go2rtc 的 WebRTC 页面需要浏览器环境）。</summary>
    public static bool Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
