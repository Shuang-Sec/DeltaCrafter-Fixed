using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>单件待上架请求（来自本轮刚领取的设施产物）。
/// 当 ExpectedPresetName 非空时，L2 流程会在搜索前与上架弹窗确认前双重强制核验其与当前制造计划预设完全一致，
/// 防止用户手动制造的贵重物品被自动上架。</summary>
public sealed record MarketSellRequest(
    FacilityKey Facility,
    string DisplayName,
    string MatchName,
    string? ExpectedPresetName = null,
    string? ExpectedPresetMatchName = null);

public enum MarketSellStatus
{
    /// <summary>已安全上架 1 份/1 组。</summary>
    Listed,
    /// <summary>只读验收模式：已定位并打开「△ 上架物品」核对全名与收益，未点击上架并已 ESC 退出。</summary>
    DryRunVerified,
    /// <summary>刚领取物品与当前制造计划预设不一致（可能为手动制造贵重物品），已保护留仓拒卖。</summary>
    SkippedPresetMismatch,
    /// <summary>交易行上架栏位已满，自动跳过。</summary>
    SkippedSlotsFull,
    /// <summary>仅命中带左下角绑定角标的格子，拒点并跳过。</summary>
    SkippedBound,
    /// <summary>遍历主仓库与全部已装备扩容箱后未找到匹配的可上架非绑定物品。</summary>
    SkippedNotFound,
    /// <summary>命中安全闸门（如弹窗校验异常、出现禁售关键词或切页失败），已安全跳过。</summary>
    SkippedSafetyGuard,
}

public sealed record MarketSellItemOutcome(MarketSellRequest Request, MarketSellStatus Status, string Detail);

public sealed record MarketSellBatchResult(
    IReadOnlyList<MarketSellItemOutcome> Outcomes,
    (int Used, int Max)? InitialSlotUsage = null)
{
    public int ListedCount => Outcomes.Count(o => o.Status == MarketSellStatus.Listed);
    public int VerifiedCount => Outcomes.Count(o => o.Status == MarketSellStatus.DryRunVerified);
}

/// <summary>
/// 交易行自动上架流程（L2）。
/// 严格从顶栏「交易行 → 出售（请选择要上架的道具）」进入：
/// 1) 彻底绕开仓库「军需处回收」弹窗，零误卖军需处风险；
/// 2) 引擎级高亮过滤 + 左下角白色斜线绑定角标像素检测双保险；
/// 3) 选中后在「△ 上架物品」核验完整物品名（TextMatch.MatchesRecipeName）、售位与预期收入；
/// 4) 按配置点击价格「-」按钮 0..3 次后，执行单次不可重复的「上架」点击；
/// 5) 每件刚领取的物品最多上架 1 份（装备/医疗/配件 1 件、子弹默认 1 组 60 发），绝不连卖自用库存；
/// 6) 栏位满/未找到/绑定等受阻均优雅跳过并记录，绝不拖垮已完成的制造续造调度。
/// </summary>
public sealed class MarketSellFlow
{
    private const int MaxNavAttempts = 3;
    /// <summary>主仓库行数多（可达 60+ 行），必须滚到真正见底才停；每页滚 10 档留足重叠防漏行。</summary>
    internal const int MaxScrollPagesMainWarehouse = 25;
    internal const int MaxScrollPagesExpansionBox = 12;
    internal const int ScrollNotchesPerPage = -10;
    private const int MaxCandidatesPerPage = 6;
    private readonly ScreenProbe _probe;
    private readonly InputBrick _input;
    private readonly ILogger _log;

    public MarketSellFlow(ScreenProbe probe, InputBrick input, ILogger log)
    {
        _probe = probe;
        _input = input;
        _log = log.ForContext<MarketSellFlow>();
    }

    /// <summary>
    /// 批量处理本轮刚领取的待上架物品。
    /// priceAdjustSteps 会被夹取到 [0, 3]；dryRun=true 时仅验证定位与弹窗核验，绝不点击降价或上架。
    /// </summary>
    public async Task<MarketSellBatchResult> SellBatchAsync(
        nint hwnd,
        IReadOnlyList<MarketSellRequest> items,
        int priceAdjustSteps,
        bool dryRun,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return new MarketSellBatchResult([]);

        // L2 纵深防御：在进入交易行之前先过滤任何与预设不一致的请求，绝不把手动制造的贵重物品带入上架流程。
        var preOutcomes = new List<MarketSellItemOutcome>();
        var allowedItems = new List<MarketSellRequest>(items.Count);
        foreach (var req in items)
        {
            if (IsRequestAllowedByPreset(req, out string mismatchReason))
                allowedItems.Add(req);
            else
            {
                _log.Warning("{Facility}「{Item}」未通过预设一致校验:{Reason}，已保护留仓、绝不上架。",
                    FacilityKeys.DisplayName(req.Facility), req.DisplayName, mismatchReason);
                preOutcomes.Add(new MarketSellItemOutcome(req, MarketSellStatus.SkippedPresetMismatch, mismatchReason));
            }
        }
        if (allowedItems.Count == 0)
            return new MarketSellBatchResult(preOutcomes);

        int adjustSteps = Math.Clamp(priceAdjustSteps, 0, 3);
        if (!await EnsureOnMarketSellAsync(hwnd, ct))
        {
            preOutcomes.AddRange(allowedItems.Select(req => new MarketSellItemOutcome(
                req, MarketSellStatus.SkippedSafetyGuard, "未能确认进入交易行「出售」页面，已跳过自动上架")));
            return new MarketSellBatchResult(preOutcomes);
        }

        var initialFrame = _probe.Capture(hwnd);
        var initialSlots = await _probe.ReadMarketSlotUsageFromFrameAsync(initialFrame, ct);
        if (initialSlots is null)
        {
            // 刚切到「出售」页时左侧上架列表数字可能延迟半秒渲染，停顿复读一次。
            await Task.Delay(500, ct);
            initialFrame = _probe.Capture(hwnd);
            initialSlots = await _probe.ReadMarketSlotUsageFromFrameAsync(initialFrame, ct);
        }
        if (initialSlots is { } slots)
        {
            _log.Information("交易行当前上架栏位:{Used}/{Max}。", slots.Used, slots.Max);
            if (!dryRun && slots.Used >= slots.Max)
            {
                string reason = $"交易行上架栏位已满({slots.Used}/{slots.Max})";
                _log.Information("{Reason}，本轮跳过自动上架。", reason);
                preOutcomes.AddRange(allowedItems.Select(req =>
                    new MarketSellItemOutcome(req, MarketSellStatus.SkippedSlotsFull, reason)));
                return new MarketSellBatchResult(preOutcomes, initialSlots);
            }
        }

        var outcomes = new List<MarketSellItemOutcome>(preOutcomes);
        int remainingSlots = initialSlots is { } s ? Math.Max(0, s.Max - s.Used) : int.MaxValue;

        foreach (var req in allowedItems)
        {
            ct.ThrowIfCancellationRequested();
            if (!dryRun && remainingSlots <= 0)
            {
                outcomes.Add(new MarketSellItemOutcome(req, MarketSellStatus.SkippedSlotsFull, "交易行剩余上架栏位已满"));
                continue;
            }

            var outcome = await SellSingleItemAsync(hwnd, req, adjustSteps, dryRun, ct);
            outcomes.Add(outcome);
            if (outcome.Status == MarketSellStatus.Listed && remainingSlots < int.MaxValue)
                remainingSlots--;
            else if (outcome.Status == MarketSellStatus.SkippedSlotsFull)
                remainingSlots = 0;
        }

        return new MarketSellBatchResult(outcomes, initialSlots);
    }

    /// <summary>
    /// L2 预设一致性校验（纯函数）：若请求携带 ExpectedPresetName，要求待售物与预设严格全名一致，
    /// 彻底杜绝手动制造的贵重物品或目录外未知物品被误卖。
    /// </summary>
    internal static bool IsRequestAllowedByPreset(MarketSellRequest req, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(req.DisplayName) && string.IsNullOrWhiteSpace(req.MatchName))
        {
            reason = "待上架物品名称为空，拒绝上架";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.ExpectedPresetName) && string.IsNullOrWhiteSpace(req.ExpectedPresetMatchName))
            return true;

        string presetDisplay = req.ExpectedPresetName ?? "";
        string presetMatch = req.ExpectedPresetMatchName ?? "";
        bool displayOk = req.DisplayName.Length > 0 &&
            ScreenProbe.MatchesTargetItemName(req.DisplayName, presetDisplay, presetMatch);
        bool rawOk = req.MatchName.Length > 0 &&
            ScreenProbe.MatchesTargetItemName(req.MatchName, presetDisplay, presetMatch);
        if (displayOk || rawOk)
            return true;

        reason = $"物品「{req.DisplayName}」与计划预设「{presetDisplay}」不一致，已保护留仓";
        return false;
    }

    private async Task<bool> IsModalTitleAllowedForRequestAsync(CapturedFrame frame, MarketSellRequest req, CancellationToken ct)
    {
        if (!await _probe.IsListingTitleMatchAsync(frame, req.DisplayName, req.MatchName, ct))
            return false;
        if (!string.IsNullOrWhiteSpace(req.ExpectedPresetName) || !string.IsNullOrWhiteSpace(req.ExpectedPresetMatchName))
            return await _probe.IsListingTitleMatchAsync(
                frame, req.ExpectedPresetName ?? "", req.ExpectedPresetMatchName ?? "", ct);
        return true;
    }

    private async Task<bool> EnsureOnMarketSellAsync(nint hwnd, CancellationToken ct)
    {
        if (await _probe.IsOnMarketSellAsync(hwnd, ct))
            return true;

        bool clickedMarket = await TryEnterMarketWithRetryAsync(
            () => _probe.IsOnMarketSellAsync(hwnd, ct),
            () => _probe.ClickMarketEntryAsync(hwnd, ct),
            (ms, token) => Task.Delay(ms, token),
            ct);

        if (!clickedMarket)
            return true; // 已在交易行出售页

        // 等待进入交易行页面（可能停留在默认「购买」页，也可能是「出售」页）。
        long deadline = Environment.TickCount64 + 12_000;
        bool sellTabClicked = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsOnMarketSellAsync(hwnd, ct))
                return true;

            if (!sellTabClicked && await _probe.ClickMarketSellSubTabAsync(hwnd, ct))
            {
                sellTabClicked = true;
                await Task.Delay(700, ct);
                continue;
            }
            await Task.Delay(450, ct);
        }

        var (png, _) = await _probe.DumpAsync(hwnd, "fail-进入交易行出售页");
        _log.Warning("进入交易行「出售」页超时，已安全跳过自动上架。诊断截图:{Png}", png);
        return false;
    }

    internal static async Task<bool> TryEnterMarketWithRetryAsync(
        Func<Task<bool>> isAlreadyMarketSell,
        Func<Task<bool>> tryClickMarketEntry,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxNavAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await isAlreadyMarketSell()) return false;
            if (await tryClickMarketEntry()) return true;
            if (attempt < MaxNavAttempts)
                await delay(400, ct);
        }
        return false;
    }

    private async Task<MarketSellItemOutcome> SellSingleItemAsync(
        nint hwnd,
        MarketSellRequest req,
        int adjustSteps,
        bool dryRun,
        CancellationToken ct)
    {
        string facilityName = FacilityKeys.DisplayName(req.Facility);
        var frame = _probe.Capture(hwnd);
        var tabs = ItemBindingBadgeDetector.DetectEquippedBoxTabs(frame);
        if (tabs.Count == 0)
            tabs = [new MarketBoxTab(0, ItemBindingBadgeDetector.BoxStripCenterXNorm, ItemBindingBadgeDetector.BoxSlot0CenterYNorm, true)];

        // 始终从主仓库(槽位 0)开始，顺序遍历全部已装备扩容箱；切回前显式点击容器标签确保滚动条复位到顶部。
        var orderedTabs = tabs
            .OrderBy(t => t.Index)
            .ToList();

        bool sawBoundCandidate = false;
        var gridArea = _probe.Screen(AnchorKeys.MarketSell).Roi(AnchorKeys.RoiMarketGridArea);

        foreach (var tab in orderedTabs)
        {
            ct.ThrowIfCancellationRequested();
            // 无论该容器是否已高亮，都点击一次（或先切回顶部）避免上一件物品把主仓库留在页面底部。
            if (!tab.IsHighlighted || tab.Index == 0)
            {
                _probe.ClickNormalizedPoint(hwnd, new NPoint { X = tab.CenterX, Y = tab.CenterY });
                await Task.Delay(450, ct);
            }

            int maxPages = MaxScrollPagesForContainer(tab.Index);
            // 主仓库要求连续 2 次下滚无变化才判定真正见底（防止偶发丢滚轮事件导致半途停扫）；
            // 扩容箱单次无变化即判定见底，减少小箱子无效等待。
            int requiredStagnantScrolls = tab.Index == 0 ? 2 : 1;
            int stagnantScrolls = 0;
            CapturedFrame? previousFrame = null;

            for (int page = 0; page < maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var scanFrame = _probe.Capture(hwnd);
                if (previousFrame is not null)
                {
                    if (ScreenProbe.IsGridRegionUnchanged(previousFrame, scanFrame, gridArea))
                    {
                        stagnantScrolls++;
                        if (stagnantScrolls >= requiredStagnantScrolls)
                        {
                            _log.Debug("容器#{Box} 第{Page}页前连续 {Stagnant} 次下滚画面未变化，确认已到达底部。",
                                tab.Index, page + 1, stagnantScrolls);
                            break;
                        }
                        _log.Debug("容器#{Box} 第{Page}页下滚未见变化，再补拉一次确认是否真正见底({Stagnant}/{Req})。",
                            tab.Index, page + 1, stagnantScrolls, requiredStagnantScrolls);
                        _probe.ScrollRoi(hwnd, gridArea, ScrollNotchesPerPage - 2);
                        await Task.Delay(550, ct);
                        continue;
                    }
                    stagnantScrolls = 0;
                }

                var (candidates, _, clusterCount, sampleLabels) = await _probe.ScanMarketGridCandidatesFromFrameAsync(
                    scanFrame, req.DisplayName, req.MatchName, ct, scrolled: page > 0);
                _log.Debug("容器#{Box} 第{Page}/{Max}页扫描({Item}): 词簇={Clusters}, 候选={Hits}, 样例=[{Samples}]",
                    tab.Index, page + 1, maxPages, req.DisplayName, clusterCount, candidates.Count, sampleLabels);

                previousFrame = scanFrame;

                int triedOnPage = 0;
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.HasBoundBadge)
                    {
                        sawBoundCandidate = true;
                        _log.Debug("{Facility}「{Item}」跳过带左下角绑定标志的候选格({Label})。",
                            facilityName, req.DisplayName, candidate.LabelText);
                        continue;
                    }
                    if (triedOnPage >= MaxCandidatesPerPage) break;
                    triedOnPage++;

                    _log.Debug("{Facility}「{Item}」尝试容器#{Box} 第{Page}页候选「{Label}」(分={Score},亮={Bright:0.0})。",
                        facilityName, req.DisplayName, tab.Index, page + 1, candidate.LabelText,
                        candidate.MatchScore, candidate.TileHighlightScore);

                    _probe.ClickFramePoint(hwnd, candidate.ClickFrameX, candidate.ClickFrameY);

                    var listingFrame = await WaitForListingModalAsync(hwnd, 1600, ct);
                    if (listingFrame is null)
                    {
                        // 引擎对绑定/损毁不可售格子会直接拒弹上架窗，安全忽略继续下一候选。
                        continue;
                    }

                    var modalOutcome = await HandleOpenedListingModalAsync(
                        hwnd, listingFrame, req, adjustSteps, dryRun, tab.Index, ct);
                    if (modalOutcome is not null)
                        return modalOutcome;
                }

                if (page + 1 < maxPages)
                {
                    _probe.ScrollRoi(hwnd, gridArea, ScrollNotchesPerPage);
                    await Task.Delay(550, ct);
                }
            }
        }

        if (sawBoundCandidate)
        {
            _log.Information("{Facility}「{Item}」仅检测到绑定不可售物品，已跳过上架。", facilityName, req.DisplayName);
            return new MarketSellItemOutcome(req, MarketSellStatus.SkippedBound, "仅检测到绑定不可售物品，已跳过");
        }

        _log.Information("{Facility}「{Item}」在主仓库及 {Count} 个扩容箱中未找到可上架的非绑定物品，已跳过。",
            facilityName, req.DisplayName, Math.Max(0, orderedTabs.Count - 1));
        return new MarketSellItemOutcome(req, MarketSellStatus.SkippedNotFound, "未找到可上架的非绑定物品，已跳过");
    }

    internal static int MaxScrollPagesForContainer(int containerIndex) =>
        containerIndex == 0 ? MaxScrollPagesMainWarehouse : MaxScrollPagesExpansionBox;

    private async Task<CapturedFrame?> WaitForListingModalAsync(nint hwnd, int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var frame = _probe.Capture(hwnd);
            var probe = _probe.Screen(AnchorKeys.MarketListing).Probe;
            string title = (await _probe.ReadAreaLinesFromFrameAsync(frame, probe.Roi, 2.0))
                .Aggregate("", (acc, line) => acc + line.Text);
            if (ScreenProbe.Normalize(title).Contains("上架", StringComparison.Ordinal))
                return frame;
            await Task.Delay(250, ct);
        }
        return null;
    }

    /// <summary>
    /// 校验已打开的「△ 上架物品」弹窗：
    /// - 若不是目标物品（网格截断重名候选），ESC 关窗并返回 null 继续寻找下一个候选；
    /// - 若命中售位满/安全红线，ESC 关窗并返回显式跳过结果；
    /// - 若全名与安全检查通过，dryRun 则 ESC 汇报，正式模式则按配置降价并单次点击上架。
    /// </summary>
    private async Task<MarketSellItemOutcome?> HandleOpenedListingModalAsync(
        nint hwnd,
        CapturedFrame openedFrame,
        MarketSellRequest req,
        int adjustSteps,
        bool dryRun,
        int containerIndex,
        CancellationToken ct)
    {
        var snapshot = await _probe.ReadListingModalFromFrameAsync(openedFrame, ct);
        bool titleMatched = await IsModalTitleAllowedForRequestAsync(openedFrame, req, ct);
        _log.Debug("已打开「△ 上架物品」核验: 标题=「{Title}」, 匹配={Match}, 安全={Safe}, 数量={Qty}/{Total}, 售位={WillUse}/{Avail}, 价格={Price}, 收入={Income}, 按钮=「{Btn}」, 拒绝={Reject}",
            ScreenProbe.Normalize(snapshot.ItemTitle), titleMatched, snapshot.IsSafe,
            snapshot.SellQuantity, snapshot.TotalQuantity, snapshot.SlotsWillUse, snapshot.SlotsAvailable,
            ScreenProbe.Normalize(snapshot.PriceText), snapshot.ExpectedIncome,
            ScreenProbe.Normalize(snapshot.ActionButtonText), snapshot.RejectReason);

        if (!titleMatched)
        {
            _log.Debug("上架弹窗完整名称「{Actual}」与目标「{Expected}」不符，按 ESC 关闭并继续查找。",
                ScreenProbe.Normalize(snapshot.ItemTitle), req.DisplayName);
            await DismissListingModalAsync(hwnd, ct);
            return null;
        }

        if (!snapshot.IsSafe)
        {
            _log.Warning("「{Item}」上架弹窗安全校验未通过:{Reason}，已按 ESC 取消。",
                req.DisplayName, snapshot.RejectReason);
            await DismissListingModalAsync(hwnd, ct);
            var status = snapshot.RejectReason.Contains("售位", StringComparison.Ordinal) ||
                         snapshot.RejectReason.Contains("栏位", StringComparison.Ordinal)
                ? MarketSellStatus.SkippedSlotsFull
                : MarketSellStatus.SkippedSafetyGuard;
            return new MarketSellItemOutcome(req, status, snapshot.RejectReason);
        }

        string qtyLabel = snapshot.SellQuantity is { } sq
            ? (snapshot.TotalQuantity is { } tq ? $"{sq}/{tq}" : $"{sq}")
            : "默认数量";
        string incomeLabel = snapshot.ExpectedIncome is { } inc ? $"{inc:N0}" : ScreenProbe.Normalize(snapshot.IncomeText);

        if (dryRun)
        {
            await DismissListingModalAsync(hwnd, ct);
            string detail = $"容器#{containerIndex} 核验通过(名称:{ScreenProbe.Normalize(snapshot.ItemTitle)},数量:{qtyLabel},预期收入:{incomeLabel},未上架)";
            _log.Information("[只看不卖]「{Item}」{Detail}", req.DisplayName, detail);
            return new MarketSellItemOutcome(req, MarketSellStatus.DryRunVerified, detail);
        }

        // 按配置点击价格「-」按钮 0..3 次（默认 0 次即保持系统推荐在售最低价）。
        for (int step = 0; step < adjustSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            _probe.ClickPoint(hwnd, AnchorKeys.MarketListing, AnchorKeys.PointPriceMinus);
            await Task.Delay(200, ct);
        }

        // 上架前最后一次同帧安全复核 + 单次不可重复点击事务。
        bool confirmed = await ConfirmListingOnceAsync(
            async () =>
            {
                var preClickFrame = _probe.Capture(hwnd);
                var preSnap = await _probe.ReadListingModalFromFrameAsync(preClickFrame, ct);
                bool preTitleOk = await IsModalTitleAllowedForRequestAsync(preClickFrame, req, ct);
                return preSnap.IsSafe && preTitleOk;
            },
            () => _probe.ClickPoint(hwnd, AnchorKeys.MarketListing, AnchorKeys.PointConfirmList),
            () => WaitForListingModalClosedAsync(hwnd, 6_000, ct),
            ct);

        if (!confirmed)
        {
            await DismissListingModalAsync(hwnd, ct);
            return new MarketSellItemOutcome(req, MarketSellStatus.SkippedSafetyGuard,
                "上架前二次校验未通过或上架后弹窗未及时关闭，已取消且未重复点击");
        }

        string listedDetail = adjustSteps > 0
            ? $"已上架(数量:{qtyLabel},降价{adjustSteps}档,预期收入:{incomeLabel})"
            : $"已上架(数量:{qtyLabel},推荐最低价,预期收入:{incomeLabel})";
        _log.Information("「{Item}」{Detail}。", req.DisplayName, listedDetail);
        return new MarketSellItemOutcome(req, MarketSellStatus.Listed, listedDetail);
    }

    /// <summary>
    /// 不可重复单次上架事务（纯逻辑边界，供单元测试直接验证）：
    /// 仅当点击前复核通过且未取消时发送一次点击，绝不重试二次上架。
    /// </summary>
    internal static async Task<bool> ConfirmListingOnceAsync(
        Func<Task<bool>> verifyBeforeClick,
        Action clickConfirmList,
        Func<Task<bool>> waitClosed,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await verifyBeforeClick())
            return false;
        ct.ThrowIfCancellationRequested();
        clickConfirmList();
        return await waitClosed();
    }

    private async Task<bool> WaitForListingModalClosedAsync(nint hwnd, int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsOnMarketSellAsync(hwnd, ct))
            {
                // 确认「上架物品」弹窗标题已消失（背景可能部分透出出售页文字）。
                var frame = _probe.Capture(hwnd);
                var probe = _probe.Screen(AnchorKeys.MarketListing).Probe;
                string title = (await _probe.ReadAreaLinesFromFrameAsync(frame, probe.Roi, 2.0))
                    .Aggregate("", (acc, line) => acc + line.Text);
                if (!ScreenProbe.Normalize(title).Contains("上架物品", StringComparison.Ordinal))
                    return true;
            }
            await Task.Delay(350, ct);
        }
        return false;
    }

    private async Task DismissListingModalAsync(nint hwnd, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _input.PressEscape();
            if (await WaitForListingModalClosedAsync(hwnd, 2_500, ct))
                return;
        }
    }
}
