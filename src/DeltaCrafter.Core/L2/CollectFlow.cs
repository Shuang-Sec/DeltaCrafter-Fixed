using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>单个设施槽位的一次观察。ItemName 为槽位上显示的物品名(空闲时为空)。</summary>
public sealed record FacilityObservation(FacilityPhase Phase, TimeSpan? Remaining, string ItemName, string RawText);

/// <summary>
/// 特勤处总览页的观察与领取。单槽位判定规则:
/// 含「空闲中」→ 空闲;能解析出倒计时 → 制造中;否则有物品名 → 可领取。
/// 整体观察采用多遍共识(见 ObserveAllAsync):同槽位两票相位一致才采信。
/// 领取路径实测可能有两种(点槽位直达结算 / 先进生产页再点「领取」),两分支都显式校验。
/// </summary>
public sealed class CollectFlow
{
    private readonly ScreenProbe _probe;
    private readonly StepRunner _runner;
    private readonly InputBrick _input;
    private readonly IAppWindowGuard _windowGuard;
    private readonly ILogger _log;
    private const string CompactedSlotCountdownPattern =
        @"^(?:(?<hours>[0-9]{2})(?<minutes>[0-9]{2})[:：．，](?<seconds>[0-9]{2})|" +
        @"(?<hours>[0-9]{2})[:：．，](?<minutes>[0-9]{2})(?<seconds>[0-9]{2}))$";

    public CollectFlow(ScreenProbe probe, StepRunner runner, InputBrick input,
        IAppWindowGuard windowGuard, ILogger log)
    {
        _probe = probe;
        _runner = runner;
        _input = input;
        _windowGuard = windowGuard;
        _log = log.ForContext<CollectFlow>();
    }

    private const int MaxObservationPasses = 5;
    private const int ObservationPassDelayMs = 700;

    /// <summary>连续多遍观察全部设施,同一槽位在两遍读取中判定一致才采信。
    /// 为什么不是"一帧定案":① 进入特勤处的滑入动画期间,槽位区域会读到邻卡串染文本,
    /// 单帧曾把制造中误判为空闲(2026-07-26 实机日志);② 「制造完成」态的物品标题对比度低,
    /// 2x 最近邻放大后 OCR 稳定读空,而 1x 原始尺寸可读(同日失败转储证实)——因此各遍交替
    /// 2x/1x 倍率,读空(Unknown)不投票也不清掉已有一票。制造中槽位还要求两票的倒计时按
    /// 实际流逝单调递减(±容差),单次误读的数字进不了调度。
    /// 本入口保留"未判定即抛错"的原语义（既有调用方兼容）；协调器改用 Detailed 版，
    /// 先处理已确认的工位，避免一个槽位读数失败就把整轮领取/开工全部作废。</summary>
    public async Task<Dictionary<FacilityKey, FacilityObservation>> ObserveAllAsync(nint hwnd, CancellationToken ct)
    {
        var batch = await ObserveAllDetailedAsync(hwnd, ct);
        if (batch.IsComplete) return batch.Agreed;
        throw new StepFailedException("观察设施状态", batch.Message, batch.DiagnosticPng, batch.DiagnosticText);
    }

    /// <summary>
    /// 采集与判定入口：与公开入口同一套读取逻辑，但把未判定工位作为数据返回。
    /// 每遍只解析一次整帧，票按槽位累积；未判定工位的原因（真读空 / 时间形但解析不出 /
    /// 仅一票）随批次返回，失败信息不再一律写成"始终读空"。
    /// </summary>
    internal async Task<ObservationBatch> ObserveAllDetailedAsync(nint hwnd, CancellationToken ct)
    {
        var spec = _probe.Screen(AnchorKeys.SpecOpsHome);
        var rois = FacilityKeys.All.Select(k => spec.Roi(AnchorKeys.FacilitySlot(k))).ToArray();
        var timerRois = rois.Select(roi => new NRect
        {
            X = roi.X + roi.W * 0.18,
            Y = roi.Y + roi.H * 0.68,
            W = roi.W * 0.82,
            H = roi.H * 0.28,
        }).ToArray();

        var votes = FacilityKeys.All.ToDictionary(k => k, _ => new List<SlotVote>());
        var agreed = new Dictionary<FacilityKey, (FacilityObservation Obs, long CapMs)>();

        for (int pass = 1; pass <= MaxObservationPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();
            // 执行中若助手窗口被手动唤起,会盖住游戏污染屏幕拷贝(实测发生过):
            // 每遍观察前重新压下去;窗口已最小化时此调用为无操作。
            _windowGuard.MinimizeForRun();
            double upscale = pass % 2 == 1 ? 2.0 : 1.0;
            long capMs = Environment.TickCount64; // 截帧时刻:剩余时间读数对应的基准点
            string[] texts = await _probe.ReadSlotRoisAsync(hwnd, rois, timerRois, upscale);
            for (int i = 0; i < FacilityKeys.All.Length; i++)
            {
                var key = FacilityKeys.All[i];
                if (agreed.ContainsKey(key)) continue;
                string timer = ScreenProbe.Normalize(texts[i + rois.Length]);
                string raw = timer + "\n" + texts[i];
                var obs = Classify(raw);
                // 相位和时间仍由原始卡片及计时双遍验证；独立标题只改善显示名称。
                string title = texts[i + rois.Length * 2];
                if (title.Length > 0 && obs.Phase is FacilityPhase.Crafting or FacilityPhase.ReadyToCollect)
                    obs = obs with { ItemName = title };
                _log.Debug("{Facility} 槽位(第{Pass}遍 {Scale:0.#}x):{Phase} {Raw}",
                    FacilityKeys.DisplayName(key), pass, upscale, obs.Phase,
                    ScreenProbe.Normalize(texts[i]) + " [计时区:" + timer + "]");
                if (title.Length > 0) _log.Debug("{Facility} 独立标题多候选共识:{Title}",
                    FacilityKeys.DisplayName(key), title);

                votes[key].Add(new SlotVote(obs, capMs, title, raw));
                var resolution = ResolveSlot(votes[key]);
                if (resolution.Agreed is { } settled)
                {
                    agreed[key] = (settled, resolution.CapturedAtMs);
                    _log.Debug("{Facility} 已取得一致判定:{Phase}", FacilityKeys.DisplayName(key), settled.Phase);
                }
            }
            if (agreed.Count == FacilityKeys.All.Length) break;
            if (pass < MaxObservationPasses) await Task.Delay(ObservationPassDelayMs, ct);
        }

        // 读数补偿:剩余时间对应各自截帧那一刻,返回前减去识别已耗掉的时间,
        // 消除「游戏里少一两秒」的系统性滞后。
        long now = Environment.TickCount64;
        var finalAgreed = new Dictionary<FacilityKey, FacilityObservation>();
        foreach (var entry in agreed)
        {
            var observation = entry.Value.Obs;
            if (observation.Phase == FacilityPhase.Crafting && observation.Remaining is { } remaining)
            {
                var left = remaining - TimeSpan.FromMilliseconds(now - entry.Value.CapMs);
                observation = observation with { Remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero };
            }
            finalAgreed[entry.Key] = observation;
        }

        var unresolved = new Dictionary<FacilityKey, SlotUnresolved>();
        foreach (var key in FacilityKeys.All)
        {
            if (finalAgreed.ContainsKey(key)) continue;
            var resolution = ResolveSlot(votes[key]);
            unresolved[key] = new SlotUnresolved(resolution.Reason, resolution.Votes, resolution.LastRaw);
        }

        // 观察不完整时立刻留存现场：后续领取/开工会让画面继续前进，事后再截图就失去证据。
        string png = "", dumpText = "";
        if (unresolved.Count > 0) (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-观察设施槽位");
        return new ObservationBatch(finalAgreed, unresolved, png, dumpText);
    }

    /// <summary>
    /// 把一个槽位的全部票归约成结论。逐票语义必须与旧实现完全一致：
    /// Unknown 不投票也不清除已有票；同相位 + 倒计时单调才合并；Crafting 票不被后续
    /// ReadyToCollect 负向证据推翻（1x 常把冒号读丢，伪造可领取）；相位冲突时以最新票
    /// 为基准继续观察。抽成纯函数是为了用真实五遍序列做回归而无需真实屏幕。
    /// </summary>
    internal static SlotResolution ResolveSlot(IReadOnlyList<SlotVote> votes)
    {
        FacilityObservation? claim = null;
        long claimCapturedAt = 0;
        int parsed = 0;
        string lastRaw = "";
        foreach (var vote in votes)
        {
            if (ScreenProbe.Normalize(vote.Raw).Length > 0) lastRaw = vote.Raw;
            var observation = vote.Observation;
            if (observation.Phase == FacilityPhase.Unknown) continue;
            parsed++;
            if (claim is null)
            {
                claim = observation; // 首票
                claimCapturedAt = vote.CapturedAtMs;
                continue;
            }
            if (claim.Phase == observation.Phase &&
                CountdownConsistent(claim, claimCapturedAt, observation, vote.CapturedAtMs))
                return new SlotResolution(Merge(claim, observation), vote.CapturedAtMs,
                    SlotUnresolvedReason.None, parsed, lastRaw);
            if (claim.Phase == FacilityPhase.Crafting && observation.Phase == FacilityPhase.ReadyToCollect)
                continue; // 负向证据不得推翻正向证据——丢弃此票,保留制造中票
            claim = observation; // 与前票冲突:以最新一票为基准继续观察
            claimCapturedAt = vote.CapturedAtMs;
        }

        bool anyText = votes.Any(v => ScreenProbe.Normalize(v.Raw).Length > 0);
        SlotUnresolvedReason reason;
        if (!anyText) reason = SlotUnresolvedReason.NoReads;
        else if (parsed > 0) reason = SlotUnresolvedReason.NoConsensus;
        else if (votes.Any(v => RawIsTimeLike(v.Raw))) reason = SlotUnresolvedReason.TimeLikeUnparsed;
        else reason = SlotUnresolvedReason.TextWithoutPhase;
        // 票数按“与最后基准同相位”统计：被丢弃的 ReadyToCollect 负向票不应把
        // “仅一票”说成“多票冲突”，失败信息才能对准真实证据。
        int samePhase = claim is null ? 0
            : votes.Count(v => v.Observation.Phase == claim.Phase);
        return new SlotResolution(null, 0, reason, samePhase, lastRaw);
    }

    private static bool RawIsTimeLike(string raw) => raw.Split('\n').Any(IsTimeLike);

    /// <summary>两票间倒计时一致性:剩余时间必须随实际流逝单调递减(允许秒级抖动)。
    /// 非制造中或缺读数时不设此约束。</summary>
    private static bool CountdownConsistent(FacilityObservation prev, long prevMs,
        FacilityObservation cur, long curMs)
    {
        if (prev.Phase != FacilityPhase.Crafting ||
            prev.Remaining is not { } r1 || cur.Remaining is not { } r2) return true;
        return ScreenProbe.CountdownReadingsConsistent(r1, prevMs, r2, curMs);
    }

    /// <summary>两票一致后的合并:取最新一票(倒计时更准),物品名取非空者。</summary>
    private static FacilityObservation Merge(FacilityObservation prev, FacilityObservation cur) =>
        cur with { ItemName = cur.ItemName.Length > 0 ? cur.ItemName : prev.ItemName };

    private FacilityObservation Classify(string raw)
    {
        string norm = ScreenProbe.Normalize(raw);
        var kw = _probe.Anchors.Keywords;
        string itemName = ExtractItemName(raw, kw);

        if (kw.Idle.Any(k => norm.Contains(ScreenProbe.Normalize(k), StringComparison.Ordinal)))
            return new FacilityObservation(FacilityPhase.Idle, null, "", raw);
        // 独立计时行按原始顺序优先于整槽原文;完整格式也不能被后面的紧凑格式覆盖。
        // 避免标题旁的时钟图标粘成「902:10:24」抢先命中。
        TimeSpan remaining;
        foreach (string line in raw.Split('\n'))
        {
            string compact = ScreenProbe.Normalize(line);
            if (System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[\d℃：:．，]+$") &&
                CountdownParser.TryParse(compact, out remaining))
                return new FacilityObservation(FacilityPhase.Crafting, remaining, itemName, raw);
            if (System.Text.RegularExpressions.Regex.IsMatch(compact,
                    @"^\d{1,3}[．：:，]\d{2}[．：:，]\d{2}$") &&
                CountdownParser.TryParse("剩余时间：" + compact, out remaining))
                return new FacilityObservation(FacilityPhase.Crafting, remaining, itemName, raw);
            if (TryParseCompactedSlotCountdown(compact, out remaining))
                return new FacilityObservation(FacilityPhase.Crafting, remaining, itemName, raw);
        }
        if (CountdownParser.TryParse(raw, out remaining))
            return new FacilityObservation(FacilityPhase.Crafting, remaining, itemName, raw);
        // 存在「时间形」行(数字+冒号)却解析不出 → 槽位在制造中,只是读数被误读
        // (实测 0 被读成 ℃/口)。这是一次失败的测量,判 Unknown 等下一遍重读;
        // 绝不能因为解析不出时间就滑落成「可领取」。
        if (raw.Split('\n').Any(IsTimeLike))
            return new FacilityObservation(FacilityPhase.Unknown, null, "", raw);
        if (itemName.Length >= 2)
            return new FacilityObservation(FacilityPhase.ReadyToCollect, null, itemName, raw);
        return new FacilityObservation(FacilityPhase.Unknown, null, "", raw);
    }

    /// <summary>仅在槽位上下文接受丢失一个分隔符的独立计时行。
    /// 必须恰好六个ASCII数字且保留一个已实测分隔符,只允许HHmm:ss或HH:mmss;
    /// 不折叠字母/口形字、不接受纯六位数字,分秒仍需合法,后续双票校验保持不变。</summary>
    private static bool TryParseCompactedSlotCountdown(string compact, out TimeSpan remaining)
    {
        remaining = default;
        var match = System.Text.RegularExpressions.Regex.Match(compact, CompactedSlotCountdownPattern);
        if (!match.Success) return false;
        int hours = int.Parse(match.Groups["hours"].Value);
        int minutes = int.Parse(match.Groups["minutes"].Value);
        int seconds = int.Parse(match.Groups["seconds"].Value);
        if (minutes >= 60 || seconds >= 60) return false;
        remaining = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    /// <summary>时间形判定:冒号与数字提供计时证据。℃ 仅在数字形片段后或行首
    /// 作为「:0」误读处理,避免把 WARR℃R3S 等物品型号当作损坏倒计时。</summary>
    private static bool IsTimeLike(string line) =>
        line.Count(char.IsDigit) >= 2 &&
        (line.Any(character => character is ':' or '：') ||
         // 损坏计时依然是计时证据；这里只排除名称/可领取判断，不推算任何数字。
         // 兼容真实残文「乁 11 ． 57 ． 25」「08．57 37」「（9 011919」。
         System.Text.RegularExpressions.Regex.IsMatch(line,
             @"\d{1,3}[ \t:：、,.;·．，]+\d{1,3}[ \t:：、,.;·．，]+\d{1,3}") ||
         System.Text.RegularExpressions.Regex.IsMatch(line,
             @"^\s*[^\p{L}\p{Nd}]*\d[\d\s:：、,.;·．，℃]*$") ||
         System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*\d{4,}\s*$") ||
         System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*\d+[,，．]\d+\s*$") ||
         System.Text.RegularExpressions.Regex.IsMatch(line,
             @"^\s*[0-9]{2}(?:[0-9A-Za-z口囗〇○]{2}[:：．，,][0-9A-Za-z口囗〇○]{2}|[:：．，,][0-9A-Za-z口囗〇○]{4})\s*$") ||
         System.Text.RegularExpressions.Regex.IsMatch(line,
             @"(?<![A-Za-z])[\dOoОо口囗〇○lI|SsBZz]+\s*℃|^\s*℃"));

    /// <summary>槽位文本里最长的非倒计时行即物品名(制造中与完成态槽位都显示物品名)。
    /// 时间形行(见 IsTimeLike)一律排除——哪怕数字被误读成解析不出的字符
    /// (实测「11:05:04」读作「11：05：口4」「10:50:47」读作「10：54℃4」),
    /// 也绝不能当成物品名。</summary>
    private static string ExtractItemName(string raw, StateKeywords kw)
    {
        return raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 2)
            .Where(l => !IsTimeLike(l))
            .Where(l => !CountdownParser.TryParse(l, out _))
            .Where(l => !kw.Idle.Any(k => l.Contains(k, StringComparison.Ordinal)))
            .OrderByDescending(l => l.Length)
            .FirstOrDefault() ?? "";
    }

    /// <summary>领取产物并回到特勤处总览。</summary>
    public async Task CollectAsync(nint hwnd, FacilityKey key, CancellationToken ct)
    {
        string name = FacilityKeys.DisplayName(key);
        _probe.ClickPoint(hwnd, AnchorKeys.SpecOpsHome, AnchorKeys.FacilitySlot(key));

        // 分支一:直达「获得奖励」;分支二:进入生产页,需再点「领取」按钮。
        string? screen = await WaitForAsync(hwnd,
            [AnchorKeys.CollectResult, AnchorKeys.Production], 12_000, $"领取{name}产物", ct);
        if (screen is null)
        {
            var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-领取入口");
            throw new StepFailedException($"领取{name}产物",
                $"点击槽位后未出现结算或生产界面。诊断截图:{png}", png, dumpText);
        }
        if (screen == AnchorKeys.Production)
        {
            // 生产、补齐、中止和领取共用同一坐标。到达生产页后必须重新确认按钮，
            // 防止总览误判可领取时把仍制造中的「中止」当成领取点击。
            await ClickCollectOnceAsync(
                () => _probe.IsOnAsync(hwnd, AnchorKeys.Production),
                () => _probe.ReadRoiAsync(hwnd, _probe.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiActionButton)),
                _probe.Anchors.Keywords,
                () => _probe.ClickPoint(hwnd, AnchorKeys.Production, AnchorKeys.PointActionButton),
                async reason =>
                {
                    var (png, dump) = await _probe.DumpAsync(hwnd, "fail-领取前检查");
                    return new StepFailedException($"领取{name}前检查", reason + $"诊断截图:{png}", png, dump);
                }, ct);
            if (await WaitForAsync(hwnd, [AnchorKeys.CollectResult], 12_000,
                    $"领取{name}产物", ct) is null)
            {
                var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-领取结算");
                throw new StepFailedException($"领取{name}产物",
                    $"点击领取后未出现结算界面。诊断截图:{png}", png, dumpText);
            }
        }

        await _runner.RunAsync(hwnd, new Step(
            $"关闭{name}领取结算",
            () => _probe.ClickPoint(hwnd, AnchorKeys.CollectResult, AnchorKeys.PointDismiss),
            async () => await _probe.WhichScreenAsync(hwnd,
                [AnchorKeys.SpecOpsHome, AnchorKeys.Production]) is not null,
            TimeSpan.FromSeconds(12)), ct);

        // 若结算后回到的是生产页,再 ESC 返回总览。
        if (await _probe.IsOnAsync(hwnd, AnchorKeys.Production))
        {
            await _runner.RunAsync(hwnd, new Step(
                "返回特勤处总览",
                () => _input.PressEscape(),
                () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
                TimeSpan.FromSeconds(8)), ct);
        }
        _log.Information("{Facility} 已领取。", name);
    }

    /// <summary>领取事务入口：画面及明确按钮均确认后才执行一次不可重复的点击。</summary>
    internal static async Task ClickCollectOnceAsync(Func<Task<bool>> isProduction,
        Func<Task<string>> readActionLabel, StateKeywords keywords, Action click,
        Func<string, Task<StepFailedException>> failure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await isProduction())
            throw await failure("当前已不在生产页，未点击同位置按钮。");
        ct.ThrowIfCancellationRequested();
        string label = ScreenProbe.Normalize(await readActionLabel());
        ct.ThrowIfCancellationRequested();
        bool collect = keywords.ButtonCollect.Select(ScreenProbe.Normalize)
            .Any(k => k.Length > 0 && label.Equals(k, StringComparison.Ordinal));
        bool conflicting = keywords.ButtonAbort.Concat(keywords.ButtonProduce).Concat(keywords.ButtonReplenish)
            .Select(ScreenProbe.Normalize).Any(k => k.Length > 0 && label.Contains(k, StringComparison.Ordinal));
        if (!collect || conflicting)
            throw await failure($"操作按钮「{label}」未明确处于领取态，未点击。");
        click();
    }

    private async Task<string?> WaitForAsync(nint hwnd, string[] screens, int timeoutMs,
        string operation, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsOnAsync(hwnd, AnchorKeys.WarehouseFull))
                throw new WarehouseFullException(operation);
            var hit = await _probe.WhichScreenAsync(hwnd, screens);
            if (hit is not null) return hit;
            await Task.Delay(400, ct);
        }
        return null;
    }
}
