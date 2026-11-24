// Copyright (C) 2026 Feiyu
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace MiHome.Models;

/// <summary>
/// 风扇（落地扇/循环扇/塔扇）的控制参数，全部从设备 MIoT 规格推出：
/// 档位可能在 fan 服务的 fan-level，也可能在 speed-level；摆风是 bool 属性；
/// 模式（直吹/自然风/睡眠）是带 value-list 的 mode。不同型号服务与 piid 都不一样，不能写死。
/// </summary>
public sealed class FanControlSchema
{
    public bool Valid { get; set; }

    /// <summary>风速档位 siid/piid；没有档位属性时 LevelPiid = 0。</summary>
    public int LevelSiid { get; set; }
    public int LevelPiid { get; set; }

    /// <summary>档位是连续量程（滑杆）还是离散档（分段按钮）。</summary>
    public bool LevelHasRange { get; set; }
    public double LevelMin { get; set; } = 1;
    public double LevelMax { get; set; } = 3;
    public double LevelStep { get; set; } = 1;

    /// <summary>离散档位（来自 value-list），如 1档/2档/3档/自然风。</summary>
    public List<AcOptionItem> Levels { get; } = new();

    /// <summary>左右摆风 / 上下摆风（可写 bool）。0 表示该型号没有这一路。</summary>
    public int SwingHSiid { get; set; }
    public int SwingHPiid { get; set; }
    public int SwingVSiid { get; set; }
    public int SwingVPiid { get; set; }

    public bool HasSwingH => SwingHPiid > 0;
    public bool HasSwingV => SwingVPiid > 0;

    /// <summary>档位是离散档（有 value-list），界面出分段按钮。</summary>
    public bool HasLevels => LevelPiid > 0 && Levels.Count > 0;

    /// <summary>
    /// 无级调速（speed-level，部分型号与挡位并存，如 dmaker.fan.p9 的 1–100）。
    /// 和挡位是两条独立属性，界面给滑杆，不替换挡位按钮。
    /// </summary>
    public int SpeedSiid { get; set; }
    public int SpeedPiid { get; set; }
    public bool SpeedHasRange { get; set; }
    public double SpeedMin { get; set; } = 1;
    public double SpeedMax { get; set; } = 100;
    public double SpeedStep { get; set; } = 1;
    public bool HasSpeed => SpeedPiid > 0;

    /// <summary>档位是连续量程（无 value-list 但有 value-range），界面出滑杆。</summary>
    public bool HasLevelRange => LevelPiid > 0 && Levels.Count == 0 && LevelHasRange;

    /// <summary>有模式枚举时界面出模式分段按钮。</summary>
    public bool HasModes => ModePiid > 0 && Modes.Count > 0;

    /// <summary>模式（可写枚举），没有就是 0。</summary>
    public int ModeSiid { get; set; }
    public int ModePiid { get; set; }
    public List<AcOptionItem> Modes { get; } = new();
}
