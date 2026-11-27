namespace DeltaCrafter.Core.L0;

/// <summary>
/// 交易行「出售」页左侧容器栏的一个槽位（0 = 主仓库，1..N = 玩家已装备的扩容箱）。
/// </summary>
internal sealed record MarketBoxTab(int Index, double CenterX, double CenterY, bool IsHighlighted);

/// <summary>
/// 物品格子绑定标志、可售亮度与交易行左侧箱子栏检测（纯像素函数，无 Win32 依赖）。
/// 实机机制：
/// 1) 绑定物品在格子左下角存在高对比度白色斜线方框角标；
/// 2) 在「交易行 → 出售（请选择要上架的道具）」页中，非绑定且可上架物品保持正常高亮，
///    绑定或损耗不可售物品由游戏引擎整体压暗且点击不会弹出「上架物品」窗口；
/// 3) 本检测器与交易行引擎过滤组成双保险：先优先高亮且无绑定角标的候选格子，再进上架弹窗核验全名。
/// </summary>
internal static class ItemBindingBadgeDetector
{
    /// <summary>16:9 画面下「请选择要上架的道具」网格单格归一化宽高（9 列横跨 0.308 宽）。</summary>
    internal const double GridCellWidthNorm = 0.0342;
    internal const double GridCellHeightNorm = 0.0608;

    /// <summary>左侧容器切换栏（主仓库 + 扩容箱）实机标定中心 X、首格中心 Y 与垂直步距。</summary>
    internal const double BoxStripCenterXNorm = 0.628;
    internal const double BoxSlot0CenterYNorm = 0.186;
    internal const double BoxSlotPitchYNorm = 0.0468;
    internal const int MaxBoxTabs = 10;

    /// <summary>
    /// 检测指定物品格子左下角是否包含白色斜线方框绑定角标。
    /// 依据实机绑定样本（高级燃料 vs 糖三角/鸟雕/便携燃料瓶）标定：
    /// 仅当左下角区域同时存在成片低饱和度高白像素（角标笔画）与暗底像素（排除纯白底色物品）
    /// 且呈紧致图标分布时判定为绑定。
    /// </summary>
    internal static bool HasBoundBadge(CapturedFrame frame, int tileLeft, int tileTop, int tileWidth, int tileHeight)
    {
        if (tileWidth < 16 || tileHeight < 16 || frame.Width <= 0 || frame.Height <= 0)
            return false;

        int x0 = Math.Clamp(tileLeft + (int)Math.Round(tileWidth * 0.02), 0, frame.Width - 1);
        int x1 = Math.Clamp(tileLeft + (int)Math.Round(tileWidth * 0.28), x0, frame.Width - 1);
        int y0 = Math.Clamp(tileTop + (int)Math.Round(tileHeight * 0.72), 0, frame.Height - 1);
        int y1 = Math.Clamp(tileTop + (int)Math.Round(tileHeight * 0.98), y0, frame.Height - 1);

        int patchW = x1 - x0 + 1;
        int patchH = y1 - y0 + 1;
        int total = patchW * patchH;
        if (total < 16) return false;

        int whitePixels = 0;
        int darkPixels = 0;
        int minWx = int.MaxValue, maxWx = -1, minWy = int.MaxValue, maxWy = -1;
        int whiteColsWithStroke = 0;

        for (int x = x0; x <= x1; x++)
        {
            int colWhite = 0;
            for (int y = y0; y <= y1; y++)
            {
                int o = (y * frame.Width + x) * 4;
                byte b = frame.Bgra[o], g = frame.Bgra[o + 1], r = frame.Bgra[o + 2];
                int maxC = Math.Max(r, Math.Max(g, b));
                int minC = Math.Min(r, Math.Min(g, b));
                int lum = (r + g + b) / 3;

                if (r >= 185 && g >= 185 && b >= 185 && (maxC - minC) <= 38)
                {
                    whitePixels++;
                    colWhite++;
                    if (x < minWx) minWx = x;
                    if (x > maxWx) maxWx = x;
                    if (y < minWy) minWy = y;
                    if (y > maxWy) maxWy = y;
                }
                else if (lum <= 125)
                {
                    darkPixels++;
                }
            }
            if (colWhite >= 2) whiteColsWithStroke++;
        }

        double whiteRatio = (double)whitePixels / total;
        double darkRatio = (double)darkPixels / total;
        if (whiteRatio < 0.05 || whiteRatio > 0.60) return false;
        // 绑定图标叠加在深色格角上，周围必须存在暗色背景像素，防止整块纯白物品误报。
        if (darkRatio < 0.25) return false;
        if (maxWx <= minWx || maxWy <= minWy) return false;

        int spanW = maxWx - minWx + 1;
        int spanH = maxWy - minWy + 1;
        double aspect = (double)spanW / spanH;
        // 白色斜线方框近似正方形（允许不同缩放比例下的轻微畸变）。
        if (aspect < 0.55 || aspect > 1.85) return false;
        int minStrokeSpan = Math.Max(4, (int)Math.Round(Math.Min(patchW, patchH) * 0.30));
        if (spanW < minStrokeSpan || spanH < minStrokeSpan) return false;
        if (whiteColsWithStroke < Math.Max(3, minStrokeSpan / 2)) return false;

        return true;
    }

    /// <summary>
    /// 评估交易行「出售」页内一个格子的亮度得分（高亮=非绑定可售，压暗=绑定或损毁不可售）。
    /// 返回值越高表示越可能是亮起的可上架物品；用于在同页存在多个同名候选时优先点击亮格。
    /// </summary>
    internal static double MeasureTileHighlightScore(CapturedFrame frame, int tileLeft, int tileTop, int tileWidth, int tileHeight)
    {
        if (tileWidth < 8 || tileHeight < 8 || frame.Width <= 0 || frame.Height <= 0)
            return 0;

        int x0 = Math.Clamp(tileLeft, 0, frame.Width - 1);
        int y0 = Math.Clamp(tileTop, 0, frame.Height - 1);
        int x1 = Math.Clamp(tileLeft + tileWidth - 1, x0, frame.Width - 1);
        int y1 = Math.Clamp(tileTop + tileHeight - 1, y0, frame.Height - 1);

        long lumSum = 0;
        int brightPx = 0;
        int total = 0;
        int step = Math.Max(1, Math.Min(tileWidth, tileHeight) / 24);
        for (int y = y0; y <= y1; y += step)
        {
            for (int x = x0; x <= x1; x += step)
            {
                int o = (y * frame.Width + x) * 4;
                int lum = (frame.Bgra[o] + frame.Bgra[o + 1] + frame.Bgra[o + 2]) / 3;
                lumSum += lum;
                if (lum >= 120) brightPx++;
                total++;
            }
        }
        if (total == 0) return 0;
        double avgLum = (double)lumSum / total;
        double brightPct = (double)brightPx / total * 100.0;
        return avgLum + brightPct * 2.5;
    }

    /// <summary>
    /// 估算交易行「出售」页左侧容器栏里存在的所有有效容器（槽位 0 为主仓库，1..N 为实际装备的扩容箱）。
    /// 空槽位背景平滑无图标对比度，自动跳过；适配不同玩家 0~9 个扩容箱配置。
    /// </summary>
    internal static IReadOnlyList<MarketBoxTab> DetectEquippedBoxTabs(CapturedFrame frame)
    {
        var tabs = new List<MarketBoxTab>();
        if (frame.Width <= 0 || frame.Height <= 0) return tabs;

        int halfW = Math.Max(3, (int)Math.Round(frame.Width * 0.008));
        int halfH = Math.Max(3, (int)Math.Round(frame.Height * 0.013));
        int cxPx = Math.Clamp((int)Math.Round(frame.Width * BoxStripCenterXNorm), halfW, frame.Width - 1 - halfW);

        for (int index = 0; index < MaxBoxTabs; index++)
        {
            double cyNorm = BoxSlot0CenterYNorm + index * BoxSlotPitchYNorm;
            if (cyNorm > 0.66) break;
            int cyPx = Math.Clamp((int)Math.Round(frame.Height * cyNorm), halfH, frame.Height - 1 - halfH);

            int minLum = 255, maxLum = 0;
            long sumLum = 0;
            int count = 0;
            for (int y = cyPx - halfH; y <= cyPx + halfH; y++)
            {
                for (int x = cxPx - halfW; x <= cxPx + halfW; x++)
                {
                    int o = (y * frame.Width + x) * 4;
                    int lum = (frame.Bgra[o] + frame.Bgra[o + 1] + frame.Bgra[o + 2]) / 3;
                    if (lum < minLum) minLum = lum;
                    if (lum > maxLum) maxLum = lum;
                    sumLum += lum;
                    count++;
                }
            }
            if (count == 0) continue;
            double avgLum = (double)sumLum / count;
            int contrast = maxLum - minLum;
            bool highlighted = avgLum >= 115.0 && maxLum >= 180;

            // 槽位 0(主仓库)始终有效；后续槽位需具备真实图标对比度才视为已装备的扩容箱。
            if (index == 0 || highlighted || contrast >= 36)
                tabs.Add(new MarketBoxTab(index, BoxStripCenterXNorm, cyNorm, highlighted));
            else if (index >= 1)
                break; // 交易行出售页左侧箱子紧邻排列，首个空白槽位之后不再有已装备箱子
        }
        return tabs;
    }

    internal static bool IsBoxTabHighlighted(CapturedFrame frame, double centerXNorm, double centerYNorm)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return false;
        int halfW = Math.Max(3, (int)Math.Round(frame.Width * 0.008));
        int halfH = Math.Max(3, (int)Math.Round(frame.Height * 0.013));
        int cxPx = Math.Clamp((int)Math.Round(frame.Width * centerXNorm), halfW, frame.Width - 1 - halfW);
        int cyPx = Math.Clamp((int)Math.Round(frame.Height * centerYNorm), halfH, frame.Height - 1 - halfH);

        long sumLum = 0;
        int maxLum = 0;
        int count = 0;
        for (int y = cyPx - halfH; y <= cyPx + halfH; y++)
        {
            for (int x = cxPx - halfW; x <= cxPx + halfW; x++)
            {
                int o = (y * frame.Width + x) * 4;
                int lum = (frame.Bgra[o] + frame.Bgra[o + 1] + frame.Bgra[o + 2]) / 3;
                if (lum > maxLum) maxLum = lum;
                sumLum += lum;
                count++;
            }
        }
        return count > 0 && (double)sumLum / count >= 115.0 && maxLum >= 180;
    }
}
