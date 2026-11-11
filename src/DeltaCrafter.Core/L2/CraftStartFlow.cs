using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>开工结果。Blocked(材料不足/需兑换)是显式业务结果,与步骤失败(异常)分开。</summary>
public sealed record CraftStartResult(bool Started, TimeSpan? Remaining, string? BlockReason);

/// <summary>
/// 「选择配方并开始生产」流程。生产界面右下角为同位置三态按钮,其文字即状态:
/// 「一键补齐」=缺料 →(可选)自动购买;「生产」=可开工;「中止」=已在生产(异常)。
/// 开工成功的判据 = 按钮变为「中止」;随后读「剩余时间」作为唯一调度依据。
/// 配方列表因玩家置顶而顺序各异,按完整名称 OCR 定位(已知同形误读单独折叠,
/// 保留型号后缀);滚到列表不再变化为止,并设安全页数上限。
/// </summary>
public sealed class CraftStartFlow
{
    private const int MaxScrollPages = 20; // 安全上限;正常在此之前就会因「见底」停止
    private readonly ScreenProbe _probe;
    private readonly StepRunner _runner;
    private readonly InputBrick _input;
    private readonly ILogger _log;

    public CraftStartFlow(ScreenProbe probe, StepRunner runner, InputBrick input, ILogger log)
    {
        _probe = probe;
        _runner = runner;
        _input = input;
        _log = log.ForContext<CraftStartFlow>();
    }

    /// <param name="searchName">运行期匹配名(通常为 OCR 原文),用于列表定位与选中校验。</param>
    /// <param name="displayName">显示名,用于日志/报告/失败信息。</param>
    public Task<CraftStartResult> StartAsync(nint hwnd, FacilityKey key, string searchName,
        string displayName, bool autoReplenish, CancellationToken ct) =>
        StartAsync(hwnd, key, searchName, displayName, autoReplenish, ct, null);

    internal async Task<CraftStartResult> StartAsync(nint hwnd, FacilityKey key, string searchName,
        string displayName, bool autoReplenish, CancellationToken ct, Action<TimeSpan?>? startedCheckpoint)
    {
        string facility = FacilityKeys.DisplayName(key);
        var prodSpec = _probe.Screen(AnchorKeys.Production);
        var kw = _probe.Anchors.Keywords;

        await _runner.RunAsync(hwnd, new Step(
            $"打开{facility}生产界面",
            () => _probe.ClickPoint(hwnd, AnchorKeys.SpecOpsHome, AnchorKeys.FacilitySlot(key)),
            () => _probe.IsOnAsync(hwnd, AnchorKeys.Production),
            TimeSpan.FromSeconds(12)), ct);

        await FindAndSelectItemAsync(hwnd, facility, searchName, displayName, prodSpec, ct);

        string label = await ReadActionLabelAsync(hwnd, prodSpec, ct);
        if (LabelHits(label, kw.ButtonAbort))
            throw new StepFailedException($"{facility}开工前检查",
                "生产界面显示「中止」,槽位并非空闲——与总览观察不一致,中止本轮待人工确认。");

        if (LabelHits(label, kw.ButtonReplenish))
        {
            if (!autoReplenish)
            {
                await EscBackToHomeAsync(hwnd, ct);
                return new CraftStartResult(false, null, "材料不足(自动补齐已关闭)");
            }
            // 购买是有副作用的动作，只允许点击一次；并行等待成功按钮或仓库已满 Toast，
            // 打开清单的 OCR 返回成功后也必须重新检查停止请求，避免停止后继续花钱。
            await BuyMaterialsOnceAsync(
                () => _runner.RunAsync(hwnd, new Step(
                    "打开一键补齐清单",
                    () => _probe.ClickPoint(hwnd, AnchorKeys.Production, AnchorKeys.PointActionButton),
                    () => _probe.IsOnAsync(hwnd, AnchorKeys.ReplenishPopup),
                    TimeSpan.FromSeconds(10)), ct),
                () =>
                {
                    _log.Information("{Facility}「{Item}」缺料,自动购买(金额随交易行波动,见游戏账单)。", facility, displayName);
                    _probe.ClickPoint(hwnd, AnchorKeys.ReplenishPopup, AnchorKeys.PointBuy);
                }, ct);
            label = await WaitForReplenishOutcomeAsync(hwnd, facility, displayName, prodSpec, ct);
            if (!LabelHits(label, kw.ButtonProduce))
            {
                // 兑换类材料交易行买不到:显式受阻,绝不循环烧钱重试。
                await EscBackToHomeAsync(hwnd, ct);
                return new CraftStartResult(false, null, "一键补齐后材料仍不足(可能含需兑换材料)");
            }
        }

        if (!LabelHits(label, kw.ButtonProduce))
        {
            var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-识别操作按钮");
            throw new StepFailedException($"{facility}识别操作按钮",
                $"按钮文字「{label}」无法归类为 生产/一键补齐/中止。诊断截图:{png}", png, dumpText);
        }

        // 开工点击不可用通用步骤重试；按钮若已变成中止，再点会取消真实制造。
        await ClickStartOnceAsync(
            () => _probe.ClickPoint(hwnd, AnchorKeys.Production, AnchorKeys.PointActionButton),
            () => WaitForStartedAsync(hwnd, facility, prodSpec, ct), ct);
        var result = await CompleteConfirmedStartAsync(
            () => ReadRemainingTimeAsync(hwnd, facility, prodSpec, ct),
            () => ReturnHomeAfterConfirmedStartAsync(hwnd, facility, prodSpec, ct), startedCheckpoint);
        if (result.Remaining is { } remaining)
            _log.Information("{Facility}「{Item}」已开工,剩余 {Remaining}。", facility, displayName, remaining);
        else
            _log.Warning("{Facility}「{Item}」已确认开工，详情计时未读到，交由总览校正。{Reason}", facility, displayName, result.BlockReason);
        return result;
    }

    internal static async Task ClickStartOnceAsync(Action click, Func<Task> confirm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        click();
        await confirm();
    }

    /// <summary>打开补齐页与购买之间明确检查取消；购买点击失败也不自动重复。</summary>
    internal static async Task BuyMaterialsOnceAsync(Func<Task> openReplenish, Action buy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await openReplenish();
        ct.ThrowIfCancellationRequested();
        buy();
    }

    /// <summary>确认开工后的事务缝：每个已知事实先保存，再执行可能失败的下一步。</summary>
    internal static async Task<CraftStartResult> CompleteConfirmedStartAsync(Func<Task<TimeSpan>> readRemaining,
        Func<Task> backToHome, Action<TimeSpan?>? checkpoint)
    {
        checkpoint?.Invoke(null);
        TimeSpan? remaining;
        string? reason = null;
        try { remaining = await readRemaining(); }
        catch (StepFailedException ex) { remaining = null; reason = ex.Message; }
        if (remaining is { } known) checkpoint?.Invoke(known);
        // 返页异常不能被误当成读时异常，否则可能重复发送ESC。
        await backToHome();
        return new CraftStartResult(true, remaining, reason);
    }

    private async Task WaitForStartedAsync(nint hwnd, string facility, ScreenSpec prodSpec, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (LabelHits(await ReadLabelOnceAsync(hwnd, prodSpec), _probe.Anchors.Keywords.ButtonAbort)) return;
            await Task.Delay(700, ct);
        }
        var (png, dump) = await _probe.DumpAsync(hwnd, "fail-确认开工");
        throw new StepFailedException($"确认{facility}开工", "仅点击了一次生产，但尚未确认中止按钮；未重复点击。诊断截图:" + png, png, dump);
    }

    private async Task ReturnHomeAfterConfirmedStartAsync(nint hwnd, string facility, ScreenSpec prodSpec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await _probe.IsOnAsync(hwnd, AnchorKeys.Production) ||
            !LabelHits(await ReadLabelOnceAsync(hwnd, prodSpec), _probe.Anchors.Keywords.ButtonAbort))
            throw new StepFailedException($"{facility}开工后返页检查", "已保存开工事实，但当前不是已确认中止态的生产页；未向未知页面发送ESC。");
        await EscBackToHomeAsync(hwnd, ct, retryOnce: false);
    }

    private async Task FindAndSelectItemAsync(nint hwnd, string facility, string searchName,
        string displayName, ScreenSpec prodSpec, CancellationToken ct)
    {
        var listArea = prodSpec.Roi(AnchorKeys.RoiListArea);
        OcrLine? line = null;
        string previousView = "";
        bool reachedBottom = false;
        // 滚动直到找到 / 列表内容不再变化(到底或滚动无效) / 安全上限。每页只读一次。
        for (int page = 0; page < MaxScrollPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var lines = await _probe.ReadAreaLinesAsync(hwnd, listArea);
            line = PickLine(lines, searchName, page);
            if (line is not null) break;

            string currentView = ScreenProbe.Normalize(string.Concat(lines.Select(l => l.Text)));
            if (currentView.Length > 0 && currentView == previousView)
            {
                reachedBottom = true;
                break;
            }
            previousView = currentView;
            _probe.ScrollRoi(hwnd, listArea, -5);
            await Task.Delay(700, ct);
        }
        if (line is null)
        {
            var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-查找物品");
            throw new StepFailedException($"查找物品「{displayName}」",
                (reachedBottom
                    ? $"{facility}列表已滚到底仍未找到(匹配键「{searchName}」)。可用「扫描配方目录」重建候选。"
                    : $"{facility}列表滚动 {MaxScrollPages} 页未找到(或滚轮未生效)。") +
                $"诊断截图:{png}", png, dumpText);
        }

        _probe.ClickFramePoint(hwnd, line.CenterX, line.CenterY);
        long deadline = Environment.TickCount64 + 8000;
        CapturedFrame? lastTitleFrame = null;
        string lastTitle = "";
        // 列表定位与详情标题必须使用相同的完整配方身份规则，不能把短型号
        // 包含在长型号中的现象当成匹配(如 BS / BS ST+、DVC12 / DVC12+P)。
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(800, ct);
            try { lastTitleFrame = _probe.Capture(hwnd); }
            catch (StepFailedException ex)
            {
                throw new StepFailedException($"核对物品「{displayName}」",
                    ex.Message + (lastTitle.Length > 0 ? $" 上次标题读数「{ScreenProbe.Normalize(lastTitle)}」。" : ""), inner: ex);
            }
            var title = await _probe.ReadRecipeTitleFromFrameAsync(lastTitleFrame,
                prodSpec.Roi(AnchorKeys.RoiDetailTitle), searchName, ct);
            lastTitle = title.Text;
            if (title.Matched)
            {
                _log.Debug("详情标题已确认:{Item}，读到:{Text}", displayName, ScreenProbe.Normalize(title.Text));
                return;
            }
            _log.Debug("详情标题校验未通过:期望 {Expected},读到 {Actual}", searchName, ScreenProbe.Normalize(title.Text));
        }
        var (png2, dump2) = lastTitleFrame is not null
            ? await _probe.DumpFrameAsync(lastTitleFrame, "fail-选中物品")
            : await _probe.DumpAsync(hwnd, "fail-选中物品");
        throw new StepFailedException($"选中物品「{displayName}」",
            $"点击后详情标题未出现该物品。诊断截图:{png2}", png2, dump2);
    }

    /// <summary>页内只选择完整配方身份匹配的行；型号字符和后缀不使用编辑距离猜测。</summary>
    private OcrLine? PickLine(IReadOnlyList<OcrLine> lines, string searchName, int page)
    {
        var selected = lines.FirstOrDefault(l => TextMatch.MatchesRecipeName(l.Text, searchName));
        _log.Debug("查找第{Page}页:{Count} 行,完整配方匹配:{Text}", page + 1, lines.Count,
            selected is null ? "无" : ScreenProbe.Normalize(selected.Text));
        return selected;
    }

    private async Task<string> ReadActionLabelAsync(nint hwnd, ScreenSpec prodSpec, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string label = await ReadLabelOnceAsync(hwnd, prodSpec);
            if (label.Length > 0) return label;
            await Task.Delay(1200, ct);
        }
        return "";
    }

    /// <summary>
    /// 补齐后的两个明确结果:按钮变为「生产」即成功；仍为「一键补齐」即材料未补齐。
    /// 顶部 Toast 存在时间很短，因此每轮先读仓库容量状态，再读常驻按钮状态。
    /// </summary>
    private async Task<string> WaitForReplenishOutcomeAsync(nint hwnd, string facility,
        string displayName, ScreenSpec prodSpec, CancellationToken ct)
    {
        long started = Environment.TickCount64;
        long deadline = started + 15_000;
        bool purchaseConfirmationClicked = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.TryConfirmBusinessDialogAsync(hwnd, ConfirmationKind.Purchase, ct, !purchaseConfirmationClicked))
            {
                ct.ThrowIfCancellationRequested();
                purchaseConfirmationClicked = true;
                _log.Information("检测到价格变动购买确认页，已点击一次确认金额按钮。");
                await Task.Delay(900, ct);
                continue;
            }
            if (await _probe.IsOnAsync(hwnd, AnchorKeys.WarehouseFull))
                throw new WarehouseFullException($"{facility}补齐「{displayName}」材料");

            if (await _probe.IsOnAsync(hwnd, AnchorKeys.Production))
            {
                string label = await ReadLabelOnceAsync(hwnd, prodSpec);
                if (LabelHits(label, _probe.Anchors.Keywords.ButtonProduce)) return label;
                // 等足 Toast 的主要显示窗口后，仍为补齐态才判定普通缺料。
                if (Environment.TickCount64 - started >= 3_000 &&
                    LabelHits(label, _probe.Anchors.Keywords.ButtonReplenish))
                    return label;
            }
            await Task.Delay(400, ct);
        }

        var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-确认购买缺料");
        throw new StepFailedException("确认购买缺料",
            $"15s 内未识别到购买结果。诊断截图:{png}", png, dumpText);
    }

    private async Task<string> ReadLabelOnceAsync(nint hwnd, ScreenSpec prodSpec)
    {
        string label = "";
        var keywords = _probe.Anchors.Keywords;
        foreach (double scale in new[] { 2.0, 1.0, 1.5 })
        {
            var texts = await _probe.ReadRoisAsync(hwnd,
                [prodSpec.Roi(AnchorKeys.RoiActionButton)], scale);
            label = ScreenProbe.Normalize(texts[0]);
            if (LabelHits(label, keywords.ButtonProduce) ||
                LabelHits(label, keywords.ButtonReplenish) ||
                LabelHits(label, keywords.ButtonAbort))
            {
                _log.Debug("操作按钮({Scale}x):{Label}", scale, label);
                return label;
            }
            _log.Debug("操作按钮({Scale}x)未识别:{Label}", scale, label);
        }
        return label;
    }

    private static bool LabelHits(string normalizedLabel, IEnumerable<string> keywords) =>
        keywords.Any(k => k.Length > 0 &&
            normalizedLabel.Contains(ScreenProbe.Normalize(k), StringComparison.Ordinal));

    private async Task<TimeSpan> ReadRemainingTimeAsync(nint hwnd, string facility,
        ScreenSpec prodSpec, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 12_000;
        (TimeSpan Remaining, long CapturedAt)? previous = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            long capturedAt = Environment.TickCount64;
            var frame = _probe.Capture(hwnd);
            var readout = await _probe.ReadCountdownFromFrameAsync(frame,
                prodSpec.Roi(AnchorKeys.RoiRemainingTime), labelledDetail: true);
            _log.Debug("{Facility}详情计时读数:{Text}", facility, ScreenProbe.Normalize(readout.Text));
            if (readout.Remaining is { } remaining)
            {
                if (previous is { } prev && ScreenProbe.CountdownReadingsConsistent(prev.Remaining, prev.CapturedAt, remaining, capturedAt))
                {
                    var adjusted = remaining - TimeSpan.FromMilliseconds(Environment.TickCount64 - capturedAt);
                    return adjusted > TimeSpan.Zero ? adjusted : TimeSpan.Zero;
                }
                previous = (remaining, capturedAt);
            }
            await Task.Delay(700, ct);
        }
        var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-读取剩余时间");
        throw new StepFailedException($"读取{facility}剩余时间",
            $"开工后读不到剩余时间,无法调度下一轮。诊断截图:{png}", png, dumpText);
    }

    private Task EscBackToHomeAsync(nint hwnd, CancellationToken ct, bool retryOnce = true) =>
        _runner.RunAsync(hwnd, new Step(
            "返回特勤处总览",
            () => _input.PressEscape(),
            () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
            TimeSpan.FromSeconds(8), RetryOnce: retryOnce), ct);
}
