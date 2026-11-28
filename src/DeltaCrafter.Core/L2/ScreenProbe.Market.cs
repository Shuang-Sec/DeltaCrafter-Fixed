using System.Text;
using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;

namespace DeltaCrafter.Core.L2;

/// <summary>交易行「请选择要上架的道具」网格中检出的一个候选物品格。</summary>
internal sealed record MarketGridCandidate(
    string LabelText,
    int MatchScore,
    double ClickFrameX,
    double ClickFrameY,
    double TileHighlightScore,
    bool HasBoundBadge);

/// <summary>「△ 上架物品」弹窗的单帧只读核验快照。</summary>
internal sealed record MarketListingSnapshot(
    bool IsSafe,
    string RejectReason,
    string ModalTitle,
    string ItemTitle,
    string MetaText,
    string PriceText,
    string IncomeText,
    string ActionButtonText,
    int? SellQuantity,
    int? TotalQuantity,
    int? SlotsWillUse,
    int? SlotsAvailable,
    int? ExpectedIncome);

/// <summary>
/// 交易行出售页与「△ 上架物品」弹窗的 OCR 识别、安全校验与候选定位原语。
/// 所有判定与解析均抽离为纯函数，便于离线单元测试与实机截图回放。
/// </summary>
public sealed partial class ScreenProbe
{
    private static readonly NRect MarketTopNavRoi = new() { X = 0.06, Y = 0.025, W = 0.51, H = 0.05 };
    private static readonly NRect MarketSubNavRoi = new() { X = 0.05, Y = 0.080, W = 0.26, H = 0.045 };
    private static readonly string[] ForbiddenVendorKeywords = ["军需处", "回收价", "回收", "绑定", "不可上架"];
    private static readonly Regex FractionPattern = new(@"(?<a>\d{1,4})\s*[/／]\s*(?<b>\d{1,4})", RegexOptions.Compiled);
    private static readonly Regex ListingMetaSlotsPattern = new(@"(?:使用售位|售位|槽位)[^0-9]{0,6}(?<a>\d{1,2})\s*[/／]\s*(?<b>\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex ListingMetaQtyPattern = new(@"(?:出售数量|数量)[^0-9]{0,6}(?<a>\d{1,4})\s*[/／]\s*(?<b>\d{1,4})", RegexOptions.Compiled);
    private static readonly Regex AmountNumberPattern = new(@"\d{1,3}(?:[,，]\d{3})+|\d{2,9}", RegexOptions.Compiled);
    private static readonly Regex AlnumTokenPattern = new(@"[A-Z0-9]{2,}(?:-[A-Z0-9]+)*", RegexOptions.Compiled);

    /// <summary>从顶栏行中选取唯一「交易行」标签（兼容历史日志实测误读「交男行」）。</summary>
    internal static OcrLine? SelectMarketEntry(IReadOnlyList<OcrLine> lines)
    {
        var matches = lines.Where(line => Normalize(line.Text) is "交易行" or "交男行").ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>顶栏多个页签被 OCR 合并为同行时，从逐词边界框提取唯一「交易行」中心。</summary>
    internal static OcrLine? SelectMarketEntryFromWords(IReadOnlyList<IReadOnlyList<OcrWordBox>> lineWords) =>
        SelectUniqueNavTokenFromWords(lineWords, ["交易行", "交男行"], excludedPrefix: null);

    /// <summary>交易行二级导航栏定位「出售」子页签。</summary>
    internal static OcrLine? SelectMarketSellSubTab(IReadOnlyList<OcrLine> lines)
    {
        var matches = lines.Where(line => Normalize(line.Text) == "出售").ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static OcrLine? SelectMarketSellSubTabFromWords(IReadOnlyList<IReadOnlyList<OcrWordBox>> lineWords) =>
        SelectUniqueNavTokenFromWords(lineWords, ["出售"], excludedPrefix: null);

    private static OcrLine? SelectUniqueNavTokenFromWords(
        IReadOnlyList<IReadOnlyList<OcrWordBox>> lineWords,
        IReadOnlyList<string> targets,
        string? excludedPrefix)
    {
        var matches = new List<OcrLine>();
        foreach (var words in lineWords)
        {
            if (words.Count == 0) continue;
            var sb = new StringBuilder();
            var owners = new List<OcrWordBox>();
            foreach (var word in words)
            {
                string norm = Normalize(word.Text);
                foreach (char c in norm)
                {
                    sb.Append(c);
                    owners.Add(word);
                }
            }
            string full = sb.ToString();
            foreach (string target in targets)
            {
                if (full.Length < target.Length) continue;
                int searchFrom = 0;
                while (true)
                {
                    int idx = full.IndexOf(target, searchFrom, StringComparison.Ordinal);
                    if (idx < 0) break;
                    searchFrom = idx + target.Length;
                    if (excludedPrefix is { Length: > 0 } prefix &&
                        idx >= prefix.Length &&
                        full.AsSpan(idx - prefix.Length, prefix.Length).SequenceEqual(prefix))
                        continue;
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    for (int k = idx; k < idx + target.Length; k++)
                    {
                        var box = owners[k];
                        minX = Math.Min(minX, box.Left);
                        minY = Math.Min(minY, box.Top);
                        maxX = Math.Max(maxX, box.Right);
                        maxY = Math.Max(maxY, box.Bottom);
                    }
                    matches.Add(new OcrLine(target, (minX + maxX) / 2.0, (minY + maxY) / 2.0));
                }
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>同帧词级定位并点击顶栏「交易行」页签；若顶栏已确认为大厅/特勤处导航栏且仅交易行单字糊掉，允许退回标定点。</summary>
    internal async Task<bool> ClickMarketEntryAsync(nint hwnd, CancellationToken ct)
    {
        var frame = Capture(hwnd);
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        bool confirmedTopBar = false;
        foreach (double scale in new[] { 1.5, 2.0, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            var detailed = await _ocr.ReadDetailedAsync(frame, MarketTopNavRoi, scale);
            if (IsLobbyMenuHeader(detailed.Readout.FullText)) confirmedTopBar = true;
            var entry = SelectMarketEntry(detailed.Readout.Lines)
                ?? SelectMarketEntryFromWords(detailed.LineWords);
            if (entry is null) continue;
            ClickFramePoint(hwnd, entry.CenterX, entry.CenterY);
            _log.Information("已按当前帧顶栏「交易行」标签点击导航，原图坐标({X:0},{Y:0})。",
                entry.CenterX, entry.CenterY);
            return true;
        }
        if (confirmedTopBar)
        {
            ClickPoint(hwnd, AnchorKeys.SpecOpsHome, AnchorKeys.PointMarketEntry);
            _log.Information("已确认顶栏导航存在，按标定点点击「交易行」。");
            return true;
        }
        return false;
    }

    /// <summary>在交易行二级导航栏定位并点击「出售」子页签。</summary>
    internal async Task<bool> ClickMarketSellSubTabAsync(nint hwnd, CancellationToken ct)
    {
        var frame = Capture(hwnd);
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        bool confirmedMarketSubBar = false;
        foreach (double scale in new[] { 2.0, 1.5, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            var detailed = await _ocr.ReadDetailedAsync(frame, MarketSubNavRoi, scale);
            string compact = Normalize(detailed.Readout.FullText);
            if (compact.Contains("购买", StringComparison.Ordinal) || compact.Contains("交易记录", StringComparison.Ordinal))
                confirmedMarketSubBar = true;
            var tab = SelectMarketSellSubTab(detailed.Readout.Lines)
                ?? SelectMarketSellSubTabFromWords(detailed.LineWords);
            if (tab is null) continue;
            ClickFramePoint(hwnd, tab.CenterX, tab.CenterY);
            _log.Information("已按交易行子导航「出售」标签点击，原图坐标({X:0},{Y:0})。", tab.CenterX, tab.CenterY);
            return true;
        }
        if (confirmedMarketSubBar)
        {
            ClickPoint(hwnd, AnchorKeys.MarketSell, AnchorKeys.PointSellTab);
            _log.Information("已确认交易行子导航栏存在，按标定点点击「出售」。");
            return true;
        }
        return false;
    }

    /// <summary>判定「交易行 → 出售（请选择要上架的道具）」页面是否就绪。</summary>
    internal static bool IsMarketSellPageText(string rightHeader, string leftHeader)
    {
        string right = Normalize(rightHeader);
        string left = Normalize(leftHeader);
        if (right.Contains("军需处", StringComparison.Ordinal) || left.Contains("军需处", StringComparison.Ordinal))
            return false;
        bool rightOk = right.Contains("要上架", StringComparison.Ordinal)
            || right.Contains("上架的道具", StringComparison.Ordinal)
            || (right.Contains("请选", StringComparison.Ordinal) && right.Contains("上架", StringComparison.Ordinal));
        bool leftOk = left.Contains("上架数量", StringComparison.Ordinal);
        return rightOk || (leftOk && right.Contains("上架", StringComparison.Ordinal));
    }

    internal async Task<bool> IsOnMarketSellFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        var spec = Screen(AnchorKeys.MarketSell);
        foreach (double scale in new[] { 2.0, 1.5 })
        {
            ct.ThrowIfCancellationRequested();
            string right = (await _ocr.ReadAsync(frame, spec.Probe.Roi, scale)).FullText;
            string left = (await _ocr.ReadAsync(frame, spec.Roi(AnchorKeys.RoiMarketSlotCount), scale)).FullText;
            if (IsMarketSellPageText(right, left)) return true;
        }
        return false;
    }

    internal Task<bool> IsOnMarketSellAsync(nint hwnd, CancellationToken ct) =>
        IsOnMarketSellFromFrameAsync(Capture(hwnd), ct);

    /// <summary>解析「上架数量 7/15」中的已用与上限栏位。</summary>
    internal static bool TryParseMarketSlotUsage(string text, out int used, out int max)
    {
        used = 0; max = 0;
        string compact = Normalize(text);
        var match = FractionPattern.Match(compact);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["a"].Value, out int a) ||
            !int.TryParse(match.Groups["b"].Value, out int b))
            return false;
        if (b < 1 || b > 60 || a < 0 || a > b) return false;
        used = a;
        max = b;
        return true;
    }

    internal async Task<(int Used, int Max)?> ReadMarketSlotUsageFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        var roi = Screen(AnchorKeys.MarketSell).Roi(AnchorKeys.RoiMarketSlotCount);
        string lastRaw = "";
        // 2560×1440 下「上架数量 7/15」字号较大，1.0x 原图优先；同时兼容小屏 1.5x/2.0x/3.0x。
        foreach (double scale in new[] { 1.0, 1.5, 2.0, 3.0 })
        {
            ct.ThrowIfCancellationRequested();
            string text = (await _ocr.ReadAsync(frame, roi, scale)).FullText;
            if (Normalize(text).Length > 0) lastRaw = text;
            if (TryParseMarketSlotUsage(text, out int used, out int max))
            {
                _log.Debug("交易行上架栏位解析({Scale:0.#}x):{Used}/{Max} (原文:{Raw})", scale, used, max, Compact(text));
                return (used, max);
            }
        }
        // 大字白字在复杂背景上做一次局部对比度增强回退。
        var enhanced = CropAndEnhanceContrast(frame, roi, 145);
        string enhText = (await _ocr.ReadAsync(enhanced, roi: null, 1.5)).FullText;
        if (TryParseMarketSlotUsage(enhText, out int eUsed, out int eMax))
        {
            _log.Debug("交易行上架栏位解析(对比度增强):{Used}/{Max} (原文:{Raw})", eUsed, eMax, Compact(enhText));
            return (eUsed, eMax);
        }
        _log.Debug("交易行上架栏位未解析出合法分数(原文:{Raw}, 增强:{Enh})。", Compact(lastRaw), Compact(enhText));
        return null;
    }

    /// <summary>
    /// 检测两个帧在网格+滚动条区域内的像素是否几乎无变化，用于判断滚动已触底或当前箱子无需滚动。
    /// 阈值设为 5%（单格悬停高亮或大红收藏品闪光仅变动 1%–3%，而真实滚动 -10 档通常变动 60%–95%），
    /// 并向右延伸覆盖滚动条区域，确保即便连续多行相同物品也能凭滚动条位置准确感知是否仍在下翻。
    /// </summary>
    internal static bool IsGridRegionUnchanged(CapturedFrame previous, CapturedFrame current, NRect gridRoi)
    {
        if (previous.Width != current.Width || previous.Height != current.Height || previous.Width <= 0)
            return false;
        var expanded = new NRect
        {
            X = gridRoi.X,
            Y = gridRoi.Y,
            W = Math.Min(1.0 - gridRoi.X, gridRoi.W + 0.014),
            H = gridRoi.H,
        };
        var (left, top, width, height) = PixelMapper.ToPixelRect(expanded, current.Width, current.Height);
        if (width < 8 || height < 8) return false;
        int stepX = Math.Max(4, width / 45);
        int stepY = Math.Max(4, height / 45);
        int changed = 0, total = 0;
        for (int y = top; y < top + height; y += stepY)
        {
            int row = y * current.Width * 4;
            for (int x = left; x < left + width; x += stepX)
            {
                int o = row + x * 4;
                int diff = Math.Abs(previous.Bgra[o] - current.Bgra[o])
                         + Math.Abs(previous.Bgra[o + 1] - current.Bgra[o + 1])
                         + Math.Abs(previous.Bgra[o + 2] - current.Bgra[o + 2]);
                if (diff >= 32) changed++;
                total++;
            }
        }
        return total > 0 && (double)changed / total < 0.05;
    }

    /// <summary>解析「出售数量: 60/90」与「使用售位: 1/8」。</summary>
    internal static (int? SellQty, int? TotalQty, int? WillUseSlots, int? AvailableSlots) ParseListingMeta(string metaText)
    {
        string compact = Normalize(metaText);
        int? sellQty = null, totalQty = null, willUse = null, available = null;

        var qtyMatch = ListingMetaQtyPattern.Match(compact);
        if (qtyMatch.Success &&
            int.TryParse(qtyMatch.Groups["a"].Value, out int sq) &&
            int.TryParse(qtyMatch.Groups["b"].Value, out int tq) &&
            sq >= 1 && tq >= sq)
        {
            sellQty = sq;
            totalQty = tq;
        }

        var slotMatch = ListingMetaSlotsPattern.Match(compact);
        if (slotMatch.Success &&
            int.TryParse(slotMatch.Groups["a"].Value, out int wu) &&
            int.TryParse(slotMatch.Groups["b"].Value, out int av) &&
            wu >= 0 && av >= 0 && av <= 60)
        {
            willUse = wu;
            available = av;
        }

        // 若中文标签因低清截断只读出两个分数（左数量、右售位），按顺序回退提取。
        if (sellQty is null || available is null)
        {
            var all = FractionPattern.Matches(compact);
            if (all.Count >= 1 && sellQty is null &&
                int.TryParse(all[0].Groups["a"].Value, out int sq0) &&
                int.TryParse(all[0].Groups["b"].Value, out int tq0) &&
                sq0 >= 1 && tq0 >= sq0)
            {
                sellQty = sq0;
                totalQty = tq0;
            }
            if (all.Count >= 2 && available is null &&
                int.TryParse(all[1].Groups["a"].Value, out int wu1) &&
                int.TryParse(all[1].Groups["b"].Value, out int av1) &&
                wu1 >= 0 && av1 >= 0 && av1 <= 60)
            {
                willUse = wu1;
                available = av1;
            }
        }
        return (sellQty, totalQty, willUse, available);
    }

    internal static bool TryParsePositiveAmount(string text, out int amount)
    {
        amount = 0;
        var matches = AmountNumberPattern.Matches(Normalize(text));
        int best = 0;
        foreach (Match m in matches)
        {
            string digits = m.Value.Replace(",", "", StringComparison.Ordinal).Replace("，", "", StringComparison.Ordinal);
            if (int.TryParse(digits, out int val) && val > best)
                best = val;
        }
        if (best <= 0) return false;
        amount = best;
        return true;
    }

    /// <summary>
    /// 「△ 上架物品」弹窗安全闸门（纯函数）：
    /// 1) 任何区域出现「军需处」「回收」「不可上架」「绑定」一律硬拒绝；
    /// 2) 标题必须含「上架物品」（或「上架」），按钮必须含「上架」且不含「出售/下架」；
    /// 3) 必须出现「预期收入」或有效正数预期收益；若读取到售位则要求剩余售位足够。
    /// </summary>
    internal static (bool IsSafe, string RejectReason) ValidateListingModal(
        string modalTitle,
        string metaText,
        string priceText,
        string incomeText,
        string actionButtonText)
    {
        string title = Normalize(modalTitle);
        string meta = Normalize(metaText);
        string price = Normalize(priceText);
        string income = Normalize(incomeText);
        string button = Normalize(actionButtonText);
        string combined = title + "\n" + meta + "\n" + price + "\n" + income + "\n" + button;

        foreach (string forbidden in ForbiddenVendorKeywords)
        {
            if (combined.Contains(forbidden, StringComparison.Ordinal))
                return (false, $"检测到禁售/军需处关键字「{forbidden}」，已硬中止");
        }
        if (combined.Contains("售位已满", StringComparison.Ordinal) || combined.Contains("栏位已满", StringComparison.Ordinal))
            return (false, "交易行售位已满");

        if (!title.Contains("上架物品", StringComparison.Ordinal) && !title.Contains("上架", StringComparison.Ordinal))
            return (false, $"弹窗标题「{title}」不是上架物品窗口");

        if (button.Contains("下架", StringComparison.Ordinal) || button.Contains("出售", StringComparison.Ordinal))
            return (false, $"底部按钮「{button}」存在非上架风险，已拒点");
        if (!button.Contains("上架", StringComparison.Ordinal))
            return (false, $"未在确认按钮区读到「上架」（实际:{button}）");

        bool hasIncomeSignal = income.Contains("收入", StringComparison.Ordinal)
            || TryParsePositiveAmount(income, out _)
            || TryParsePositiveAmount(price, out _);
        if (!hasIncomeSignal)
            return (false, $"未识别到有效价格或预期收入（价格:{price}，收入:{income}）");

        var (_, _, willUse, available) = ParseListingMeta(meta);
        if (available is { } avail && (avail <= 0 || (willUse is { } wu && wu > avail)))
            return (false, $"交易行可用售位不足({willUse}/{avail})");

        return (true, "");
    }

    /// <summary>单帧完整读取并校验「△ 上架物品」弹窗，供正式执行与实机截图离线回放共用。</summary>
    internal async Task<MarketListingSnapshot> ReadListingModalFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        var spec = Screen(AnchorKeys.MarketListing);
        ct.ThrowIfCancellationRequested();
        string modalTitle = (await _ocr.ReadAsync(frame, spec.Probe.Roi, 2.0)).FullText;
        if (!Normalize(modalTitle).Contains("上架", StringComparison.Ordinal))
            modalTitle = (await _ocr.ReadAsync(frame, spec.Probe.Roi, 1.5)).FullText;

        string itemTitle = await ReadListingItemTitleFromFrameAsync(frame, spec.Roi(AnchorKeys.RoiDetailTitle), ct);
        string metaText = (await _ocr.ReadAsync(frame, spec.Roi(AnchorKeys.RoiListingMeta), 2.0)).FullText;
        if (ParseListingMeta(metaText).SellQty is null)
        {
            string meta3x = (await _ocr.ReadAsync(frame, spec.Roi(AnchorKeys.RoiListingMeta), 3.0)).FullText;
            if (ParseListingMeta(meta3x).SellQty is not null || Normalize(meta3x).Length > Normalize(metaText).Length)
                metaText = meta3x;
        }
        string priceText = (await _ocr.ReadAsync(frame, spec.Roi(AnchorKeys.RoiListingPrice), 2.0)).FullText;
        string incomeText = (await _ocr.ReadAsync(frame, spec.Roi(AnchorKeys.RoiListingIncome), 2.0)).FullText;

        string actionButtonText = "";
        var actionRoi = spec.Roi(AnchorKeys.RoiActionButton);
        foreach (double scale in new[] { 2.0, 3.0, 1.5, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            actionButtonText = (await _ocr.ReadAsync(frame, actionRoi, scale)).FullText;
            if (Normalize(actionButtonText).Contains("上架", StringComparison.Ordinal)) break;
        }
        // 若原图多倍率仍读不出按钮字，再做一次温和灰度提亮回退（不猜测文案）。
        if (!Normalize(actionButtonText).Contains("上架", StringComparison.Ordinal))
        {
            var enhanced = CropAndEnhanceContrast(frame, actionRoi, 115);
            string enhancedText = (await _ocr.ReadAsync(enhanced, roi: null, 2.0)).FullText;
            if (Normalize(enhancedText).Contains("上架", StringComparison.Ordinal))
                actionButtonText = enhancedText;
        }

        var (isSafe, rejectReason) = ValidateListingModal(modalTitle, metaText, priceText, incomeText, actionButtonText);
        var (sellQty, totalQty, willUse, available) = ParseListingMeta(metaText);
        int? incomeVal = TryParsePositiveAmount(incomeText, out int inc) ? inc : null;

        return new MarketListingSnapshot(
            isSafe, rejectReason, modalTitle, itemTitle, metaText, priceText, incomeText, actionButtonText,
            sellQty, totalQty, willUse, available, incomeVal);
    }

    internal async Task<string> ReadListingItemTitleFromFrameAsync(CapturedFrame frame, NRect titleRoi, CancellationToken ct)
    {
        // 「△ 上架物品」右侧物品名位于品质色条上的浅色抗锯齿字：原图多倍率优先保留完整字形。
        string best = "";
        foreach (double scale in new[] { 2.0, 1.5, 3.0, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            string text = (await _ocr.ReadAsync(frame, titleRoi, scale)).FullText;
            if (Normalize(text).Length > Normalize(best).Length)
                best = text;
            if (Normalize(text).Length >= 2)
                return text;
        }
        return best;
    }

    internal async Task<bool> IsListingTitleMatchAsync(CapturedFrame frame, string displayName, string matchName, CancellationToken ct)
    {
        var roi = Screen(AnchorKeys.MarketListing).Roi(AnchorKeys.RoiDetailTitle);
        foreach (double scale in new[] { 2.0, 1.5, 3.0, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            string text = (await _ocr.ReadAsync(frame, roi, scale)).FullText;
            if (MatchesTargetItemName(text, displayName, matchName))
                return true;
        }
        return false;
    }

    internal static bool MatchesTargetItemName(string observed, string displayName, string matchName) =>
        (displayName.Length > 0 && TextMatch.MatchesRecipeName(observed, displayName)) ||
        (matchName.Length > 0 && TextMatch.MatchesRecipeName(observed, matchName));

    /// <summary>
    /// 阶段一网格候选打分（纯函数）：仅用于决定「点哪个格子打开上架预览」，绝不直接上架！
    /// 兼容仓库/出售页格子对长物品名的截断（如「精密护甲维修包」显示「精密护甲」、「视点3倍瞄准镜」显示「视点3倍」）。
    /// 真正上架前，阶段二会在「△ 上架物品」弹窗用完整名称做严格 TextMatch.MatchesRecipeName 复核。
    /// </summary>
    internal static int ScoreMarketGridCandidate(string tileText, string displayName, string matchName)
    {
        string tileNorm = TextMatch.Canonical(tileText);
        if (tileNorm.Length < 2) return 0;

        int best = 0;
        foreach (string target in new[] { displayName, matchName }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
        {
            if (TextMatch.MatchesRecipeName(tileText, target))
                return 100;

            string targetNorm = TextMatch.Canonical(target);
            if (targetNorm.Length == 0) continue;

            // 1) 截断前缀命中：如 精密护甲 -> 精密护甲维修包、H09防暴 -> H09防暴头盔
            if (tileNorm.Length >= 3 && targetNorm.StartsWith(tileNorm, StringComparison.Ordinal))
            {
                best = Math.Max(best, 80 + Math.Min(15, tileNorm.Length));
                continue;
            }
            if (targetNorm.Length >= 3 && tileNorm.Contains(targetNorm, StringComparison.Ordinal))
            {
                best = Math.Max(best, 90);
                continue;
            }

            // 2) 特征型号/口径后缀命中：先按空白/中文边界拆词，避免 Canonical 去空格把「5.7×28mm SS190」粘成整串。
            var tokens = target
                .Split([' ', '\t', '\r', '\n', '/', '／'], StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(part => AlnumTokenPattern.Matches(TextMatch.Canonical(part)).Select(m => m.Value))
                .Where(t => t.Length >= 3 && t != "MM" && !t.EndsWith("MM", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (tokens.Any(t => tileNorm.Contains(t, StringComparison.Ordinal)))
            {
                best = Math.Max(best, 70);
                continue;
            }

            // 3) 3~4 字中文前缀容错（允许小字 OCR 出现 1 个错别字，如「精密户甲」命中「精密护甲维修包」）
            string cjkPrefix = ExtractLeadingCjk(targetNorm, 4);
            if (cjkPrefix.Length >= 3 && tileNorm.Length >= 3 &&
                TextMatch.SubstringDistance(tileNorm, cjkPrefix) <= 1)
            {
                best = Math.Max(best, 55);
            }
        }
        return best;
    }

    private static string ExtractLeadingCjk(string canonical, int maxChars)
    {
        var sb = new StringBuilder(maxChars);
        foreach (char c in canonical)
        {
            if (c >= '\u4E00' && c <= '\u9FFF')
            {
                sb.Append(c);
                if (sb.Length >= maxChars) break;
            }
            else if (sb.Length > 0)
            {
                break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 按水平词间距把合并的同行 OCR 词框拆分成独立格子的文本簇，
    /// 避免相邻格子的标签（如「精密护甲 | 高级护甲 | 高级护甲」）被合并成一行导致点击点落偏。
    /// </summary>
    internal static IReadOnlyList<(string Text, double Left, double Top, double Right, double Bottom)>
        SplitLineWordsIntoTileClusters(IReadOnlyList<OcrWordBox> words, int frameWidth)
    {
        var clusters = new List<(string Text, double Left, double Top, double Right, double Bottom)>();
        if (words.Count == 0 || frameWidth <= 0) return clusters;

        double maxIntraTileGap = frameWidth * 0.011;
        var ordered = words.OrderBy(w => w.Left).ToList();
        var sb = new StringBuilder();
        double left = ordered[0].Left, top = ordered[0].Top, right = ordered[0].Right, bottom = ordered[0].Bottom;
        sb.Append(ordered[0].Text);

        for (int i = 1; i < ordered.Count; i++)
        {
            var w = ordered[i];
            if (w.Left - right > maxIntraTileGap)
            {
                clusters.Add((sb.ToString(), left, top, right, bottom));
                sb.Clear();
                sb.Append(w.Text);
                left = w.Left; top = w.Top; right = w.Right; bottom = w.Bottom;
            }
            else
            {
                sb.Append(w.Text);
                right = Math.Max(right, w.Right);
                top = Math.Min(top, w.Top);
                bottom = Math.Max(bottom, w.Bottom);
            }
        }
        clusters.Add((sb.ToString(), left, top, right, bottom));
        return clusters;
    }

    /// <summary>
    /// 生成「请选择要上架的道具」网格的窄水平标签条带：
    /// 实机 1440p 原图测试证实，带入下方 3D 物品图标的宽区域会让 Windows OCR 读空或严重乱码，
    /// 而只裁取每行顶部约 0.028H（~40px）窄标签条并以 2.5x 识别即可清晰读出同排几乎全部物品名。
    /// 未滚动首页（scrolled=false）按实机精确格距 0.0595H 切 13 条；滚动后（scrolled=true）用步距 0.019H、
    /// 高 0.032H 重叠扫描，保证任意滚动偏移下的行首标签都完整落入至少一条无图标干扰的窄带。
    /// </summary>
    internal static IReadOnlyList<NRect> BuildMarketGridLabelStrips(NRect grid, bool scrolled)
    {
        var strips = new List<NRect>();
        double gridBottom = grid.Y + grid.H;
        if (!scrolled)
        {
            const double stripH = 0.028;
            for (int row = 0; row < 14; row++)
            {
                double y = grid.Y + row * ItemBindingBadgeDetector.GridCellHeightNorm - 0.002;
                if (y >= gridBottom - 0.010) break;
                double clampedY = Math.Max(0.0, y);
                double h = Math.Min(stripH, gridBottom - clampedY);
                if (h >= 0.014)
                    strips.Add(new NRect { X = grid.X, Y = clampedY, W = grid.W, H = h });
            }
        }
        else
        {
            const double stripH = 0.032;
            const double stride = 0.019;
            for (double y = grid.Y; y < gridBottom - 0.012; y += stride)
            {
                double h = Math.Min(stripH, gridBottom - y);
                if (h >= 0.016)
                    strips.Add(new NRect { X = grid.X, Y = y, W = grid.W, H = h });
            }
        }
        return strips;
    }

    /// <summary>快速跳过无物品的纯色空白行条带（&lt;0.1ms），减少半空扩容箱的无效 OCR 调用。</summary>
    internal static bool HasStripVisualContent(CapturedFrame frame, NRect strip)
    {
        var (left, top, width, height) = PixelMapper.ToPixelRect(strip, frame.Width, frame.Height);
        if (width < 8 || height < 4) return false;
        int minLum = 255, maxLum = 0;
        int stepX = Math.Max(4, width / 60);
        int stepY = Math.Max(2, height / 8);
        for (int y = top; y < top + height; y += stepY)
        {
            int row = y * frame.Width * 4;
            for (int x = left; x < left + width; x += stepX)
            {
                int o = row + x * 4;
                int lum = (frame.Bgra[o] + frame.Bgra[o + 1] + frame.Bgra[o + 2]) / 3;
                if (lum < minLum) minLum = lum;
                if (lum > maxLum) maxLum = lum;
            }
        }
        return (maxLum - minLum) >= 25;
    }

    /// <summary>
    /// 在「请选择要上架的道具」右侧网格按窄行标签条做 2.5x OCR 扫描（空行自动跳过，无词时回退 2.0x），
    /// 输出按「非绑定优先 → 亮格高亮优先 → 匹配分高」排序的候选格子列表、去重页签名与词簇调试样本。
    /// </summary>
    internal async Task<(IReadOnlyList<MarketGridCandidate> Candidates, string PageSignature, int ClusterCount, string SampleLabels)>
        ScanMarketGridCandidatesFromFrameAsync(CapturedFrame frame, string displayName, string matchName, CancellationToken ct, bool scrolled = false)
    {
        var grid = Screen(AnchorKeys.MarketSell).Roi(AnchorKeys.RoiMarketGridArea);
        var strips = BuildMarketGridLabelStrips(grid, scrolled);

        int cellW = Math.Max(20, (int)Math.Round(frame.Width * ItemBindingBadgeDetector.GridCellWidthNorm));
        int cellH = Math.Max(20, (int)Math.Round(frame.Height * ItemBindingBadgeDetector.GridCellHeightNorm));
        var rawCandidates = new List<MarketGridCandidate>();
        var sigBuilder = new StringBuilder();
        var sampleLabels = new List<string>(10);
        int clusterCount = 0;

        void CollectFromDetailed(OcrDetailedReadout detailed, bool recordStats)
        {
            if (recordStats) sigBuilder.Append(Normalize(detailed.Readout.FullText));
            foreach (var words in detailed.LineWords)
            {
                foreach (var cluster in SplitLineWordsIntoTileClusters(words, frame.Width))
                {
                    if (recordStats)
                    {
                        clusterCount++;
                        string clean = Normalize(cluster.Text);
                        if (clean.Length >= 2 && sampleLabels.Count < 10 && !sampleLabels.Contains(clean))
                            sampleLabels.Add(clean);
                    }
                    int score = ScoreMarketGridCandidate(cluster.Text, displayName, matchName);
                    if (score <= 0) continue;

                    double clickX = (cluster.Left + cluster.Right) / 2.0;
                    double clickY = Math.Min(frame.Height - 4, cluster.Top + cellH * 0.45);
                    int tileLeft = Math.Max(0, (int)Math.Round(cluster.Left - cellW * 0.08));
                    int tileTop = Math.Max(0, (int)Math.Round(cluster.Top - cellH * 0.06));
                    int tileW = Math.Max(cellW, (int)Math.Round((cluster.Right - cluster.Left) * 1.15));

                    bool bound = ItemBindingBadgeDetector.HasBoundBadge(frame, tileLeft, tileTop, tileW, cellH);
                    double bright = ItemBindingBadgeDetector.MeasureTileHighlightScore(frame, tileLeft, tileTop, tileW, cellH);
                    rawCandidates.Add(new MarketGridCandidate(cluster.Text, score, clickX, clickY, bright, bound));
                }
            }
        }

        foreach (var strip in strips)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasStripVisualContent(frame, strip)) continue;
            var detailed = await _ocr.ReadDetailedAsync(frame, strip, 2.5);
            CollectFromDetailed(detailed, recordStats: true);
            if (detailed.Readout.Lines.Count == 0)
            {
                ct.ThrowIfCancellationRequested();
                var fallback = await _ocr.ReadDetailedAsync(frame, strip, 2.0);
                CollectFromDetailed(fallback, recordStats: true);
            }
        }

        // 重叠带可能扫到同一格子两次：按半格距离去重，保留高分者。
        var deduped = new List<MarketGridCandidate>();
        double mergeRadiusX = cellW * 0.65;
        double mergeRadiusY = cellH * 0.55;
        foreach (var c in rawCandidates
            .OrderBy(c => c.HasBoundBadge)
            .ThenByDescending(c => c.TileHighlightScore >= 65.0)
            .ThenByDescending(c => c.MatchScore)
            .ThenByDescending(c => c.TileHighlightScore))
        {
            if (deduped.Any(e => Math.Abs(e.ClickFrameX - c.ClickFrameX) < mergeRadiusX
                              && Math.Abs(e.ClickFrameY - c.ClickFrameY) < mergeRadiusY))
                continue;
            deduped.Add(c);
        }

        return (deduped, sigBuilder.ToString(), clusterCount, string.Join("/", sampleLabels));
    }
}
