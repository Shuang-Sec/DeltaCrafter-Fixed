using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>
/// 调度计算与状态持久化(state.json)。时间依据只有一条:游戏内倒计时的 OCR 读数。
/// NeedsManual 的设施不参与触发计算(避免反复空跑),但每轮执行仍会重新观察它,
/// 用户补齐材料后下一轮自动恢复。失败退避闸门由成功一轮清除。
/// </summary>
public sealed class ScheduleEngine
{
    private readonly JsonStoreBrick _store;
    private readonly string _statePath;
    private readonly IClock _clock;
    private readonly ILogger _log;

    public ScheduleState State { get; }

    public ScheduleEngine(JsonStoreBrick store, string statePath, IClock clock, ILogger log)
    {
        _store = store;
        _statePath = statePath;
        _clock = clock;
        _log = log.ForContext<ScheduleEngine>();
        State = store.LoadOrCreate(statePath, ScheduleState.CreateDefault);
    }

    public void RecordObservation(FacilityKey key, FacilityPhase phase, string itemName,
        DateTimeOffset? readyAt, string? manualReason)
    {
        var rt = State.For(key);
        rt.Phase = phase;
        rt.ItemName = itemName;
        rt.ReadyAt = readyAt;
        rt.ManualReason = manualReason;
        rt.ObservedAt = _clock.Now;
        Save();
    }

    /// <summary>
    /// 下次执行时刻。取启用设施中最早的需求点:
    /// 制造中 → 完成时刻 + 随机收取延迟;可领取/空闲/从未观察 → 现在;NeedsManual → 不触发。
    /// 失败退避期内不早于退避截止。没有任何启用设施需要动作时返回 null。
    /// </summary>
    public DateTimeOffset? ComputeNextRunAt(CraftPlanConfig plan, AppSettings settings)
    {
        var now = _clock.Now;
        DateTimeOffset? next = null;
        foreach (var fp in plan.Facilities.Where(f => f.Enabled))
        {
            var rt = State.For(fp.Key);
            DateTimeOffset? candidate = rt.Phase switch
            {
                FacilityPhase.Crafting when rt.ReadyAt is { } ready =>
                    ready + ResolveCollectDelay(fp.Key, ready, settings),
                FacilityPhase.Crafting => now, // 制造中但没有读数,重新观察
                FacilityPhase.ReadyToCollect or FacilityPhase.Idle or FacilityPhase.Unknown => now,
                FacilityPhase.NeedsManual => null,
                _ => null,
            };
            if (candidate is { } c && (next is null || c < next)) next = c;
        }

        if (next is { } n && State.FailureBackoffUntil is { } backoff && backoff > n)
            next = backoff;
        return next;
    }

    /// <summary>倒计时结束后至少等待的秒数：默认 60 秒。</summary>
    internal const int MinCollectDelaySeconds = 60;

    /// <summary>在最小等待之上追加的随机跨度（秒）：默认 240，也就是 1–5 分钟窗口。</summary>
    internal const int CollectDelayRandomSpanSeconds = 240;

    /// <summary>
    /// 倒计时结束后的随机收取延迟。用户要的是“倒计时结束后 1–5 分钟再去特勤处领取”，
    /// 既不要卡在完成瞬间，也不要每天同一时刻；但调度器每 10 秒就要算一次下次执行，
    /// 所以延迟必须由“设施 + 完成时刻所在分钟”稳定推导，不能每次调用重新掷骰子。
    /// 下限取设置里的执行缓冲（不低于 60 秒），上限为下限 + 240 秒。
    /// </summary>
    internal static TimeSpan ResolveCollectDelay(FacilityKey key, DateTimeOffset readyAt, AppSettings settings)
    {
        int floor = Math.Max(MinCollectDelaySeconds, settings.RunBufferSeconds);
        return TimeSpan.FromSeconds(floor + StableOffsetSeconds(key, readyAt));
    }

    /// <summary>FNV-1a 稳定散列，取 0–240 秒偏移；同一单制造重复计算得到同一结果。</summary>
    internal static int StableOffsetSeconds(FacilityKey key, DateTimeOffset readyAt)
    {
        unchecked
        {
            string token = $"{key}|{readyAt.ToUnixTimeSeconds() / 60}";
            uint hash = 2166136261;
            foreach (char character in token)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return (int)(hash % (uint)(CollectDelayRandomSpanSeconds + 1));
        }
    }

    /// <summary>
    /// 启用设施中最早的制造完成时刻；没有可读完成时刻时返回 null。
    /// 调度器用它把“预启动游戏”压到倒计时结束之后，避免还没到点就把游戏拉起来。
    /// </summary>
    internal DateTimeOffset? EarliestCraftCompletionAt(CraftPlanConfig plan)
    {
        DateTimeOffset? earliest = null;
        foreach (var fp in plan.Facilities.Where(f => f.Enabled))
        {
            var rt = State.For(fp.Key);
            if (rt.Phase != FacilityPhase.Crafting || rt.ReadyAt is not { } ready) continue;
            if (earliest is null || ready < earliest) earliest = ready;
        }
        return earliest;
    }

    public void MarkRunStarted()
    {
        State.LastRunAt = _clock.Now;
        Save();
    }

    public void MarkRunFinished(string summary, bool failed, int failureRetryMinutes)
    {
        State.LastRunSummary = summary;
        State.LastRunFailed = failed;
        State.FailureBackoffUntil = failed ? _clock.Now.AddMinutes(failureRetryMinutes) : null;
        if (failed)
            _log.Warning("本轮失败,{Minutes} 分钟后才会再次自动尝试。", failureRetryMinutes);
        Save();
    }

    public void Save() => _store.Save(_statePath, State);
}
