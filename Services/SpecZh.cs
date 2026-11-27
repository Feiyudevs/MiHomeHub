// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MiHomeHub.Services;

/// <summary>
/// 规格中文化：不少第三方/贴牌设备（如 chuangmi 摄像机）的 MIoT 规格只带英文 description，
/// 云端 comment 也缺失，界面就整段英文。这里按三层查表把英文换回中文：
/// 短名（camera-control / image-rollover）→ 整句描述（Camera Control / Night Shot）→ 逐词兜底。
/// 已经是中文的文案一律原样返回，不重复翻译；翻不出来的保持英文，不硬造词。
/// </summary>
public static class SpecZh
{
    /// <summary>文本里有汉字就当作已经是中文，不再走翻译。</summary>
    public static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (c is >= '\u3400' and <= '\u9FFF') return true;
            if (c is >= '\uF900' and <= '\uFAFF') return true;
        }
        return false;
    }

    /// <summary>
    /// 英文描述换中文。返回 null 表示翻不出来，调用方继续试下一个候选（短名表、英文原名）。
    /// </summary>
    public static string? Localize(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (HasCjk(s)) return s;

        var key = Normalize(s);
        if (Phrases.TryGetValue(key, out var phrase)) return phrase;

        // 逐词兜底只接受短词组：整句/长串翻出来多半不成句，宁可保留英文
        var words = Words(s);
        if (words.Count is 0 or > 4) return null;
        var parts = new List<string>(words.Count);
        foreach (var w in words)
        {
            if (Latin.TryGetValue(w, out var keep)) parts.Add(keep);
            else if (WordZh.TryGetValue(w, out var zh)) parts.Add(zh);
            else return null;
        }
        return Join(parts);
    }

    /// <summary>短名精确翻译（camera-control → 摄像机控制），未命中返回 null。</summary>
    public static string? TranslateName(string? shortName)
    {
        var s = shortName?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return Names.TryGetValue(s, out var zh) ? zh : null;
    }

    /// <summary>
    /// 枚举值标签中文化：Auto / Off / On、Not Recording、1920_1080_20 这类。
    /// 认不出来就原样返回，避免下拉里出现空白项。
    /// </summary>
    public static string LocalizeValue(string label)
    {
        var s = label?.Trim() ?? "";
        if (s.Length == 0) return s;
        if (HasCjk(s)) return s;

        if (Values.TryGetValue(Normalize(s), out var v)) return v;

        var res = Regex.Match(s, @"^(\d+)_(\d+)_(\d+)$");
        if (res.Success)
        {
            var w = int.Parse(res.Groups[1].Value);
            var h = int.Parse(res.Groups[2].Value);
            var fps = res.Groups[3].Value;
            return $"{(h > 0 ? h : w)}P {fps}fps";
        }

        var audio = Regex.Match(s, @"^(\d+)_(\d+)_(\d+)_([A-Za-z]+)$");
        if (audio.Success)
        {
            var rate = audio.Groups[1].Value;
            var ch = int.Parse(audio.Groups[2].Value);
            var bits = audio.Groups[3].Value;
            return $"{rate}Hz {(ch <= 1 ? "单声道" : ch + "声道")} {bits}bit";
        }

        return Localize(s) ?? s;
    }

    private static readonly Regex WordSplitter = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

    private static List<string> Words(string text)
    {
        var list = new List<string>();
        foreach (Match m in WordSplitter.Matches(text)) list.Add(m.Value.ToLowerInvariant());
        return list;
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), "\\s+", " ").TrimEnd('.', ',', ';');

    private static string Join(List<string> parts)
    {
        var sb = new System.Text.StringBuilder();
        var prevLatin = false;
        foreach (var p in parts)
        {
            var latin = IsLatin(p);
            // 中文与中文直接相接；两侧只要有一侧是英文就留一个空格，免得「P2P推流」糊在一起
            if (sb.Length > 0 && (latin || prevLatin)) sb.Append(' ');
            sb.Append(p);
            prevLatin = latin;
        }
        return sb.ToString();
    }

    private static bool IsLatin(string text)
    {
        foreach (var c in text)
            if (c > '\u007F') return false;
        return true;
    }

    /// <summary>规格里出现较长的英文整句时优先走这张表，避免逐词拼出病句。</summary>
    private static readonly Dictionary<string, string> Phrases = new(StringComparer.Ordinal)
    {
        ["camera"] = "摄像机",
        ["camera control"] = "摄像机控制",
        ["camera stream management for amazon alexa"] = "Alexa 推流管理",
        ["camera stream management for google home"] = "Google Home 推流管理",
        ["camera stream for amazon alexa"] = "Alexa 推流管理",
        ["camera stream for google home"] = "Google Home 推流管理",
        ["p2p stream"] = "P2P 直连推流",
        ["memory card management"] = "存储卡管理",
        ["device information"] = "设备信息",
        ["image rollover"] = "画面翻转",
        ["night shot"] = "夜视",
        ["night vision"] = "夜视",
        ["time watermark"] = "时间水印",
        ["wide dynamic range mode"] = "宽动态范围",
        ["wide dynamic range"] = "宽动态范围",
        ["glimmer full color"] = "微光全彩",
        ["recording mode"] = "录像模式",
        ["motion tracking"] = "移动追踪",
        ["motion detection"] = "移动侦测",
        ["stream status"] = "推流状态",
        ["video codec type"] = "视频编码",
        ["video attribute"] = "视频分辨率",
        ["audio codec type"] = "音频编码",
        ["audio attribute"] = "音频参数",
        ["authorization type"] = "鉴权方式",
        ["stream url"] = "推流地址",
        ["stream address"] = "推流地址",
        ["image snapshot url"] = "截图地址",
        ["expiration time"] = "过期时间",
        ["start camera stream for alexa"] = "开始 Alexa 推流",
        ["start camera stream for google"] = "开始 Google 推流",
        ["start p2p stream"] = "开始 P2P 推流",
        ["stop camera stream"] = "停止推流",
        ["stop stream"] = "停止推流",
        ["get stream configuration of camera"] = "读取推流配置",
        ["current firmware version"] = "当前固件版本",
        ["serial number"] = "序列号",
        ["device model"] = "设备型号",
        ["device manufacturer"] = "设备厂商",
        ["full color"] = "全彩",
        ["power consumption"] = "耗电量",
        ["energy consumption"] = "耗电量",
        ["target temperature"] = "目标温度",
        ["color temperature"] = "色温",
        ["relative humidity"] = "湿度",
        ["battery level"] = "电量",
        ["charge progress"] = "电量",
        ["fan level"] = "风速",
        ["wind level"] = "风速",
        ["horizontal swing"] = "左右摆风",
        ["vertical swing"] = "上下摆风",
        ["indicator light"] = "指示灯",
        ["child lock"] = "童锁",
        ["current position"] = "当前位置",
        ["target position"] = "目标位置",
        ["motion state"] = "运动状态",
        ["contact state"] = "接触状态",
        ["smoke state"] = "烟雾状态",
        ["door state"] = "门状态",
        ["leak state"] = "漏水状态"
    };

    /// <summary>逐词兜底用的一般词汇。只收不会产生歧义的词，专有名词留在英文。</summary>
    private static readonly Dictionary<string, string> WordZh = new(StringComparer.Ordinal)
    {
        // 摄像机 / 推流
        ["image"] = "图像",
        ["picture"] = "画面",
        ["rollover"] = "翻转",
        ["flip"] = "翻转",
        ["mirror"] = "镜像",
        ["night"] = "夜间",
        ["shot"] = "拍摄",
        ["watermark"] = "水印",
        ["wide"] = "宽",
        ["dynamic"] = "动态",
        ["range"] = "范围",
        ["glimmer"] = "微光",
        ["recording"] = "录像",
        ["record"] = "录像",
        ["motion"] = "移动",
        ["tracking"] = "追踪",
        ["track"] = "追踪",
        ["detect"] = "侦测",
        ["detection"] = "侦测",
        ["stream"] = "推流",
        ["video"] = "视频",
        ["audio"] = "音频",
        ["codec"] = "编码",
        ["snapshot"] = "截图",
        ["expiration"] = "过期",
        ["expire"] = "过期",
        ["authorization"] = "鉴权",
        ["authentication"] = "鉴权",
        ["management"] = "管理",
        ["memory"] = "存储",
        ["card"] = "卡",
        ["camera"] = "摄像机",
        ["p2p"] = "P2P",
        ["streaming"] = "推流",
        ["resolution"] = "分辨率",
        ["frame"] = "帧",
        ["rate"] = "率",
        ["format"] = "格式",

        // 通用属性
        ["status"] = "状态",
        ["state"] = "状态",
        ["type"] = "类型",
        ["attribute"] = "参数",
        ["attributes"] = "参数",
        ["url"] = "地址",
        ["address"] = "地址",
        ["time"] = "时间",
        ["control"] = "控制",
        ["device"] = "设备",
        ["information"] = "信息",
        ["manufacturer"] = "厂商",
        ["vendor"] = "厂商",
        ["model"] = "型号",
        ["serial"] = "序列",
        ["number"] = "号",
        ["firmware"] = "固件",
        ["version"] = "版本",
        ["current"] = "当前",
        ["switch"] = "开关",
        ["power"] = "功率",
        ["brightness"] = "亮度",
        ["color"] = "色彩",
        ["colour"] = "色彩",
        ["temperature"] = "温度",
        ["humidity"] = "湿度",
        ["battery"] = "电量",
        ["charge"] = "充电",
        ["charging"] = "充电中",
        ["percentage"] = "百分比",
        ["level"] = "档位",
        ["speed"] = "速度",
        ["angle"] = "角度",
        ["direction"] = "方向",
        ["position"] = "位置",
        ["sensitivity"] = "灵敏度",
        ["delay"] = "延时",
        ["duration"] = "时长",
        ["interval"] = "间隔",
        ["timer"] = "定时",
        ["schedule"] = "计划",
        ["countdown"] = "倒计时",
        ["mode"] = "模式",
        ["target"] = "目标",
        ["fan"] = "风扇",
        ["wind"] = "风",
        ["swing"] = "摆风",
        ["heat"] = "制热",
        ["cool"] = "制冷",
        ["dry"] = "除湿",
        ["auto"] = "自动",
        ["manual"] = "手动",
        ["lock"] = "锁",
        ["door"] = "门",
        ["window"] = "窗",
        ["curtain"] = "窗帘",
        ["sensor"] = "传感器",
        ["smoke"] = "烟雾",
        ["gas"] = "燃气",
        ["water"] = "水",
        ["leak"] = "漏水",
        ["filter"] = "滤芯",
        ["life"] = "寿命",
        ["remaining"] = "剩余",
        ["used"] = "已用",
        ["total"] = "累计",
        ["flow"] = "流量",
        ["pressure"] = "压力",
        ["quality"] = "质量",
        ["air"] = "空气",
        ["purifier"] = "净化器",
        ["humidifier"] = "加湿器",
        ["vacuum"] = "扫地机",
        ["robot"] = "机器人",
        ["dust"] = "粉尘",
        ["noise"] = "噪音",
        ["indicator"] = "指示灯",
        ["light"] = "灯",
        ["lamp"] = "灯",
        ["screen"] = "屏幕",
        ["display"] = "显示",
        ["volume"] = "音量",
        ["mute"] = "静音",
        ["alarm"] = "报警",
        ["alert"] = "报警",
        ["error"] = "故障",
        ["fault"] = "故障",
        ["notification"] = "通知",
        ["enable"] = "启用",
        ["enabled"] = "已启用",
        ["disable"] = "停用",
        ["disabled"] = "已停用",
        ["scene"] = "场景",
        ["effect"] = "效果",
        ["hour"] = "小时",
        ["minute"] = "分钟",
        ["second"] = "秒",
        ["day"] = "天"
    };

    /// <summary>原样保留的缩写/协议名，逐词翻译时按大写输出。</summary>
    private static readonly Dictionary<string, string> Latin = new(StringComparer.Ordinal)
    {
        ["h264"] = "H264",
        ["h265"] = "H265",
        ["g711"] = "G711",
        ["g726"] = "G726",
        ["aac"] = "AAC",
        ["opus"] = "OPUS",
        ["jpeg"] = "JPEG",
        ["hls"] = "HLS",
        ["rtsp"] = "RTSP",
        ["rtmp"] = "RTMP",
        ["ip"] = "IP",
        ["mac"] = "MAC",
        ["usb"] = "USB",
        ["dc"] = "DC",
        ["ac"] = "AC",
        ["wifi"] = "WiFi",
        ["zigbee"] = "Zigbee",
        ["bluetooth"] = "蓝牙",
        ["alexa"] = "Alexa",
        ["google"] = "Google",
        ["homekit"] = "HomeKit",
        ["mqtt"] = "MQTT",
        ["url"] = "URL",
        ["pm2"] = "PM2"
    };

    /// <summary>枚举值标签。整词匹配优先，长标签写全（Not Recording 比逐词拼更准）。</summary>
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        ["auto"] = "自动",
        ["automatic"] = "自动",
        ["manual"] = "手动",
        ["off"] = "关闭",
        ["on"] = "开启",
        ["true"] = "是",
        ["false"] = "否",
        ["yes"] = "是",
        ["no"] = "否",
        ["none"] = "无",
        ["unknown"] = "未知",
        ["available"] = "可用",
        ["in use"] = "使用中",
        ["unavailable"] = "不可用",
        ["not recording"] = "不录像",
        ["all record"] = "持续录像",
        ["motion record"] = "移动录像",
        ["recording"] = "录像中",
        ["enabled"] = "已开启",
        ["disabled"] = "已关闭",
        ["open"] = "打开",
        ["opened"] = "已打开",
        ["closed"] = "关闭",
        ["opening"] = "正在打开",
        ["closing"] = "正在关闭",
        ["idle"] = "空闲",
        ["running"] = "运行中",
        ["stopped"] = "已停止",
        ["paused"] = "暂停",
        ["pause"] = "暂停",
        ["play"] = "播放",
        ["normal"] = "正常",
        ["abnormal"] = "异常",
        ["low"] = "低",
        ["medium"] = "中",
        ["high"] = "高",
        ["strong"] = "强",
        ["weak"] = "弱",
        ["silent"] = "静音",
        ["day"] = "白天",
        ["night"] = "夜晚",
        ["locked"] = "上锁",
        ["unlocked"] = "解锁",
        ["charging"] = "充电中",
        ["charged"] = "已充满",
        ["full"] = "满",
        ["empty"] = "空",
        ["not installed"] = "未安装",
        ["digest"] = "摘要认证",
        ["basic"] = "基础认证"
    };

    /// <summary>MIoT 短名表：短名比描述稳定，命中就比逐词翻译准。</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // ---- 摄像机 / 推流 ----
        ["camera"] = "摄像机",
        ["camera-control"] = "摄像机控制",
        ["on"] = "开关",
        ["image-rollover"] = "画面翻转",
        ["night-shot"] = "夜视",
        ["time-watermark"] = "时间水印",
        ["wdr-mode"] = "宽动态范围",
        ["wide-dynamic-range"] = "宽动态范围",
        ["glimmer-full-color"] = "微光全彩",
        ["recording-mode"] = "录像模式",
        ["motion-tracking"] = "移动追踪",
        ["stream-status"] = "推流状态",
        ["video-codec-type"] = "视频编码",
        ["video-attribute"] = "视频分辨率",
        ["audio-codec-type"] = "音频编码",
        ["audio-attribute"] = "音频参数",
        ["authorization-type"] = "鉴权方式",
        ["authorization-status"] = "授权状态",
        ["stream-address"] = "推流地址",
        ["stream-url"] = "推流地址",
        ["image-snapshot"] = "截图地址",
        ["expiration-time"] = "过期时间",
        ["memory-card-management"] = "存储卡管理",
        ["p2p-stream"] = "P2P 直连推流",
        ["camera-stream-for-amazon-alexa"] = "Alexa 推流管理",
        ["camera-stream-for-google-home"] = "Google Home 推流管理",
        ["start-rtsp-stream"] = "开始 Alexa 推流",
        ["start-hls-stream"] = "开始 Google 推流",
        ["start-p2p-stream"] = "开始 P2P 推流",
        ["stop-stream"] = "停止推流",
        ["get-stream-configuration"] = "读取推流配置",
        ["format"] = "格式化",
        ["pop-up"] = "弹出存储卡",
        ["motion-detected"] = "移动侦测",

        // ---- 服务 ----
        ["light"] = "灯",
        ["television"] = "电视",
        ["ir-tv-control"] = "红外电视控制",
        ["remote-control-management"] = "遥控器管理",
        ["switch"] = "开关",
        ["outlet"] = "插座",
        ["curtain"] = "窗帘",
        ["air-conditioner"] = "空调",
        ["fan"] = "风扇",
        ["humidifier"] = "加湿器",
        ["purifier"] = "净化器",
        ["vacuum"] = "扫地机",
        ["sensor"] = "传感器",
        ["battery"] = "电池",
        ["environment"] = "环境",
        ["physical-controls-locked"] = "童锁",
        ["indicator-light"] = "指示灯",

        // ---- 属性 ----
        ["brightness"] = "亮度",
        ["color-temperature"] = "色温",
        ["mode"] = "模式",
        ["fan-level"] = "风速",
        ["target-temperature"] = "目标温度",
        ["temperature"] = "温度",
        ["relative-humidity"] = "湿度",
        ["humidity"] = "湿度",
        ["pm2.5"] = "PM2.5",
        ["pm2.5-density"] = "PM2.5",
        ["pm10"] = "PM10",
        ["pm10-density"] = "PM10",
        ["pm1"] = "PM1",
        ["pm1-density"] = "PM1",
        ["illuminance"] = "照度",
        ["battery-level"] = "电量",
        ["charge-progress"] = "电量",
        ["co2"] = "二氧化碳",
        ["co2-density"] = "二氧化碳",
        ["tvoc"] = "TVOC",
        ["tvoc-density"] = "eTVOC",
        ["voc-density"] = "VOC",
        ["hcho"] = "甲醛",
        ["hcho-density"] = "甲醛",
        ["noise"] = "噪音",
        ["noise-decibel"] = "噪音",
        ["dust"] = "粉尘",
        ["dust-density"] = "粉尘",
        ["tds-in"] = "TDS进水",
        ["tds-out"] = "TDS出水",
        ["air-quality"] = "空气质量",
        ["water-level"] = "水位",
        ["cumu-flow-out"] = "累计制水",
        ["cumu-flow-in"] = "累计进水",
        ["flow-out"] = "本次制水",
        ["flow-in"] = "本次进水",
        ["filter-used-flow"] = "滤芯已用",
        ["filter-left-flow"] = "滤芯剩余",
        ["discharge-water"] = "水流量",
        ["input-water-temp"] = "进水温度",
        ["single-water"] = "单次用水",
        ["single-gas"] = "单次用气",
        ["usage-time"] = "使用时长",
        ["power-consumption"] = "耗电量",
        ["electric-power"] = "功率",
        ["voltage"] = "电压",
        ["electric-current"] = "电流",
        ["contact-state"] = "接触状态",
        ["occupancy"] = "占用",
        ["smoke-state"] = "烟雾状态",
        ["leak"] = "漏水",
        ["door-state"] = "门状态",
        ["submersion-state"] = "浸水状态",
        ["default-power-on-state"] = "默认上电状态",
        ["flex-switch"] = "凌动开关",
        ["night-light-switch"] = "夜灯开关",
        ["remote-control-addable"] = "遥控器添加开关",
        ["remote-control-list"] = "遥控器管理",
        ["remote-add"] = "添加遥控",
        ["remote-del"] = "删除遥控",
        ["remote-switch"] = "遥控开关",

        // ---- 动作 ----
        ["toggle"] = "切换",
        ["brightness-up"] = "亮度+",
        ["brightness-down"] = "亮度-",
        ["channel-down"] = "频道-",
        ["channel-up"] = "频道+",
        ["volume-down"] = "音量-",
        ["volume-up"] = "音量+",
        ["volume-increase"] = "音量增加值",
        ["volume-decrease"] = "音量减少值",
        ["turn-on"] = "开机",
        ["turn-off"] = "关机",
        ["mute-on"] = "静音",
        ["mute-off"] = "取消静音",
        ["input-source-switch"] = "切换输入源",
        ["toggle-light-mode"] = "切换灯光模式",
        ["delete-remote-control"] = "删除遥控器"
    };
}
