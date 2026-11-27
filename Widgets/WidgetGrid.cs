using System;
using Avalonia;

namespace MiHomeHub.Widgets;

/// <summary>
/// 组件尺寸规范（唯一出处）。
///
/// 规则：尺寸 = 基准格 <see cref="Unit"/> 的整数倍，且**只允许三种形状** ——
/// 1:1（n×n）、2:1（2n×n）、1:2（n×2n）。
/// 设计尺寸、右下角拖拽缩放的落点、读档回放的钳制全部走这一套（<see cref="Fit"/>），
/// 所以任意组件并排 / 上下叠放时边缘都落在同一套格线上，宽高又只有「倍数关系」，
/// 「一键智能布局」（<c>WidgetSmartLayout</c>）装箱不会出现宽窄混排、行高参差的碎缝。
///
/// 规则二：格数**只取 2 的幂**（2 / 4 / 8 / 16 …），3、6 这类 3 的倍数一律不用。
/// 这样任意两个组件的宽高都是 50 的 2^k 倍，彼此恒为**整数倍**关系
/// （100 ↔ 200 ↔ 400 ↔ 800），智能布局按格线装箱时不会出现半格 / 1.5 倍的余缝。
///
/// 最小一档是 2 格 = **100 DIP**：1:1 最小 100×100、2:1 最小 200×100、1:2 最小 100×200。
/// 例外：组件可以在 <see cref="Fit"/> 里传自己的 <c>minCells</c>，把下限单独放宽到 1×1（50×50）——
/// 只有内容会**整体等比缩小**（Viewbox 排版）的组件才允许这么传，普通组件仍按「设计 × 0.5」。
///
/// 基准格取 50 DIP（不是 62）：62 那套档位（124 / 248 / 496）整体偏大，
/// 小卡片没有落脚点；50 这套是 100 / 200 / 400，最小档保持 2 格但整体小了一圈，
/// 又仍然是 2 的幂倍数关系。
///
/// 各组件档位（W×H 格数 / DIP / 形状）：
/// <list type="bullet">
/// <item>时钟          4×2   200×100  2:1（下限单独放宽到 1×1 = 50×50，见 <see cref="Fit"/>）</item>
/// <item>指针时钟      4×4   200×200  1:1</item>
/// <item>翻页时钟      8×4   400×200  2:1（内容 372×124 等比缩放后居中）</item>
/// <item>天气          8×4   400×200  2:1</item>
/// <item>每日单词      8×4   400×200  2:1</item>
/// <item>系统监控      8×4   400×200  2:1</item>
/// <item>秒表/计时器   8×4   400×200  2:1</item>
/// <item>日历          8×8   400×400  1:1</item>
/// <item>便签          4×4   200×200  1:1</item>
/// <item>股票          8×4   400×200  2:1（列表默认 3 行可视 + 滚动，下方留给走势图）</item>
/// <item>媒体控制      4×4   200×200  1:1（唱片圆盘 + 环形实时频谱 + 曲名/歌手 + 媒体键）</item>
/// </list>
///
/// ⚠️ 新增 / 调整组件尺寸时必须落在格点，格数取 2 的幂，且满足 1:1 / 2:1 / 1:2 之一；
/// 内容变高变宽导致最小格数跨档时，同步更新上表。
///
/// 唯一例外：智能布局为了「上面一张宽卡、下面并排两张窄卡」的左右边缘对齐，会给宽卡补上
/// 并排卡多出来的那一个呼吸位（≤ 12 DIP，非格点值，见 <c>WidgetSmartLayout</c> ②″）。
/// 这种实例在 widgets.json 里带 <c>wf=1</c>，读档时按原值回放、**不**再走 <see cref="Fit"/>；
/// 用户一拖右下角把手就清掉标记、回到格点。别在别处把 412 这类宽度当成非法尺寸改回去。
/// </summary>
internal static class WidgetGrid
{
    /// <summary>基准格边长（DIP）。所有组件尺寸 = 格子的整数倍。</summary>
    public const double Unit = 50;

    /// <summary>组件最小格数（宽 / 高都不小于 2 格 = 100 DIP）。</summary>
    public const int MinCells = 2;

    /// <summary>
    /// 缩放下限：设计格数的 0.5 倍（即下一档 2 的幂）。
    /// 档位本身是 2 倍跳（4 ↔ 8），所以下限只能是「减半」；0.75 倍这类中间值不是合法格数。
    /// </summary>
    public const double MinScale = 0.5;

    /// <summary>放大上限：设计格数的 3 倍（向下取整），再按形状归一（实际有效档位是 2 倍）。</summary>
    public const double MaxScale = 3.0;

    /// <summary>设计尺寸：格数 × <see cref="Unit"/>。</summary>
    public static Size Of(int cols, int rows) => new(cols * Unit, rows * Unit);

    /// <summary>该格数能否作为档位：2 的幂（2/4/8/16…）。3、6 这类 3 的倍数一律不合法。</summary>
    public static bool IsLegalCells(int cells)
        => cells >= MinCells && (cells & (cells - 1)) == 0;

    /// <summary>是否落在允许的形状里（1:1 / 2:1 / 1:2，且格数都是 2 的幂）。</summary>
    public static bool IsLegal(int cols, int rows)
        => IsLegalCells(cols) && IsLegalCells(rows)
           && (cols == rows || cols == 2 * rows || rows == 2 * cols);

    /// <summary>该尺寸占几格（取整，下限 2 格）。</summary>
    public static int CellsOf(double v) => Math.Max(MinCells, (int)Math.Round(v / Unit));

    /// <summary>设计格数 → 允许的最小格数（0.5 倍向上取整，绝不超过设计值）。</summary>
    public static int MinCellsOf(int designCells)
        => Math.Max(MinCells, Math.Min(designCells, (int)Math.Ceiling(designCells * MinScale - 1e-6)));

    /// <summary>设计格数 → 允许的最大格数（3 倍向下取整，至少设计值）。</summary>
    public static int MaxCellsOf(int designCells)
        => Math.Max(designCells, (int)Math.Floor(designCells * MaxScale + 1e-6));

    /// <summary>
    /// 把任意设计尺寸归一化成合法形状（1:1 / 2:1 / 1:2），并夹进该组件的缩放上下限。
    /// 形状由「想要的宽高比」决定（取相对误差最小的合法档位），所以拖把手时只会在这三种形状间跳。
    /// </summary>
    /// <param name="want">想要的尺寸（与 <paramref name="design"/> 同一空间，DIP 或格数×Unit 都行）。</param>
    /// <param name="design">组件设计尺寸。</param>
    /// <param name="minCells">
    /// 逐组件的缩放下限（**格数**，如 <c>new Size(1, 1)</c> = 50×50）：传了就取代默认的
    /// 「设计格数 × <see cref="MinScale"/>」下限，让这个组件单独能缩到 1 格起。
    /// 只给「内容整体等比缩放、不会被裁切」的组件传（时钟就是），其它组件保持 null。
    /// </param>
    public static Size Fit(Size want, Size design, Size? minCells = null)
    {
        if (design.Width < Unit || design.Height < Unit) return want;

        int dCols = CellsOf(design.Width);
        int dRows = CellsOf(design.Height);
        // 下限：传了逐组件格数就按它（1 格 = 50 DIP），否则走「设计 × 0.5」
        int minCols = minCells.HasValue
            ? Math.Max(1, (int)Math.Round(minCells.Value.Width))
            : MinCellsOf(dCols);
        int minRows = minCells.HasValue
            ? Math.Max(1, (int)Math.Round(minCells.Value.Height))
            : MinCellsOf(dRows);
        int maxCols = MaxCellsOf(dCols), maxRows = MaxCellsOf(dRows);

        // want 的格数只在 1 格处兜底（不能再用 CellsOf 的 2 格下限，否则拖到 50×50 会被抬成 2×1）
        int wantCols = double.IsFinite(want.Width) && want.Width > 0
            ? Math.Max(1, (int)Math.Round(want.Width / Unit))
            : dCols;
        int wantRows = double.IsFinite(want.Height) && want.Height > 0
            ? Math.Max(1, (int)Math.Round(want.Height / Unit))
            : dRows;

        var (c, r) = FitShape(wantCols, wantRows, minCols, minRows, maxCols, maxRows);
        return Of(c, r);
    }

    /// <summary>
    /// 在 [minCols,maxCols]×[minRows,maxRows] 里挑最接近 (cols,rows) 的合法形状。
    /// 直接枚举三种形状的各档（格数很小，枚举比手推边界稳），取相对误差最小的一档；
    /// 同分时保留先枚举到的那档 —— 由小到大枚举 = 同分偏向小尺寸。
    /// </summary>
    private static (int Cols, int Rows) FitShape(int cols, int rows,
        int minCols, int minRows, int maxCols, int maxRows)
    {
        int bestCols = 0, bestRows = 0;
        double best = double.MaxValue;

        void Consider(int c, int r)
        {
            if (c < minCols || c > maxCols || r < minRows || r > maxRows) return;
            // ⚠️ 必须转 double：Math.Abs/Math.Max 都是 int 版，int/int 会把相对误差截断成 0，
            // 于是「先枚举到的最小档」永远同分胜出 —— 设计尺寸本身会被拽到缩放下限。
            double score = Math.Abs(c - cols) / (double)Math.Max(MinCells, cols)
                         + Math.Abs(r - rows) / (double)Math.Max(MinCells, rows);
            if (score < best - 1e-9)
            {
                best = score;
                bestCols = c;
                bestRows = r;
            }
        }

        // 只枚举 2 的幂：1、2、4、8、16…（10 格起这一档给 1×1 例外组件用；
        // 其它组件的 minCols/minRows 都在 2 格以上，1 格档会在 Consider 里被直接刷掉）
        for (int n = 1; n <= 32; n *= 2)
        {
            if (n > maxCols && n > maxRows) break;
            Consider(n, n);        // 1:1
            Consider(2 * n, n);    // 2:1
            Consider(n, 2 * n);    // 1:2
        }

        // 上下限把合法形状全排除了（极端存档）：原样返回，交给调用方兜底
        return bestCols > 0 ? (bestCols, bestRows) : (cols, rows);
    }
}

