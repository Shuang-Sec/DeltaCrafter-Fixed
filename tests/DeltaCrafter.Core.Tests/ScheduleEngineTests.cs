using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class ScheduleEngineTests : IDisposable
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 26, 12, 0, 0, TimeSpan.FromHours(8));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deltacrafter-tests-" + Guid.NewGuid());
    private readonly FakeClock _clock = new();
    private readonly ScheduleEngine _engine;
    private readonly AppSettings _settings = new() { RunBufferSeconds = 60, FailureRetryMinutes = 30 };
    private readonly CraftPlanConfig _plan = CraftPlanConfig.CreateDefault();

    public ScheduleEngineTests()
    {
        Directory.CreateDirectory(_dir);
        // 无 sink 的配置即静默 logger,不依赖具体 Serilog 版本是否提供 Logger.None。
        ILogger silent = new LoggerConfiguration().CreateLogger();
        _engine = new ScheduleEngine(new JsonStoreBrick(),
            Path.Combine(_dir, "state.json"), _clock, silent);
    }

    [Fact]
    public void Crafting_facility_schedules_ready_plus_between_one_and_five_minutes()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true;
        var ready = _clock.Now.AddHours(2);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "物品A", ready, null);

        var next = _engine.ComputeNextRunAt(_plan, _settings);
        Assert.NotNull(next);
        Assert.InRange(next.Value, ready.AddSeconds(60), ready.AddSeconds(300));
    }

    [Fact]
    public void Collect_delay_is_stable_across_repeated_scheduler_queries()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true;
        var ready = _clock.Now.AddHours(2);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "物品A", ready, null);

        // 调度循环每 10 秒问一次；随机延迟必须由完成时刻稳定推导，否则时间会来回抖动。
        var first = _engine.ComputeNextRunAt(_plan, _settings);
        for (int i = 0; i < 10; i++)
            Assert.Equal(first, _engine.ComputeNextRunAt(_plan, _settings));
    }

    [Fact]
    public void Collect_delay_varies_between_crafts_but_stays_in_the_window()
    {
        var delays = new HashSet<double>();
        for (int minute = 0; minute < 24; minute++)
        {
            var ready = new DateTimeOffset(2026, 11, 11, 18, minute, 0, TimeSpan.FromHours(8));
            var delay = ScheduleEngine.ResolveCollectDelay(FacilityKey.Workbench, ready, _settings).TotalSeconds;
            Assert.InRange(delay, 60, 300);
            delays.Add(delay);
        }
        Assert.True(delays.Count >= 5, $"随机延迟退化成了固定值：{string.Join(",", delays)}");
    }

    [Fact]
    public void Run_buffer_is_the_floor_of_the_random_collect_window()
    {
        var ready = _clock.Now.AddHours(2);
        var settings = new AppSettings { RunBufferSeconds = 120 };
        for (int minute = 0; minute < 12; minute++)
        {
            var delay = ScheduleEngine.ResolveCollectDelay(FacilityKey.ArmorStation,
                ready.AddMinutes(minute), settings).TotalSeconds;
            Assert.InRange(delay, 120, 360);
        }
    }

    [Fact]
    public void Earliest_craft_completion_ignores_non_crafting_facilities()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true;
        _plan.For(FacilityKey.TechCenter).Enabled = true;
        var ready = _clock.Now.AddHours(3);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.ReadyToCollect, "A", null, null);
        _engine.RecordObservation(FacilityKey.TechCenter, FacilityPhase.Crafting, "B", ready, null);

        Assert.Equal(ready, _engine.EarliestCraftCompletionAt(_plan));
    }

    [Fact]
    public void Ready_or_unknown_facility_triggers_now()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true; // 从未观察 → Unknown
        Assert.Equal(_clock.Now, _engine.ComputeNextRunAt(_plan, _settings));

        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.ReadyToCollect, "物品A", null, null);
        Assert.Equal(_clock.Now, _engine.ComputeNextRunAt(_plan, _settings));
    }

    [Fact]
    public void Earliest_enabled_facility_wins()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true;
        _plan.For(FacilityKey.TechCenter).Enabled = true;
        var techReady = _clock.Now.AddHours(1);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "A", _clock.Now.AddHours(5), null);
        _engine.RecordObservation(FacilityKey.TechCenter, FacilityPhase.Crafting, "B", techReady, null);

        var next = _engine.ComputeNextRunAt(_plan, _settings);
        Assert.NotNull(next);
        Assert.InRange(next.Value, techReady.AddSeconds(60), techReady.AddSeconds(300));
    }

    [Fact]
    public void Disabled_and_needs_manual_do_not_trigger()
    {
        // 全部未启用 → 无计划
        Assert.Null(_engine.ComputeNextRunAt(_plan, _settings));

        // 仅一个启用且 NeedsManual → 不因它反复空跑
        _plan.For(FacilityKey.PharmacyLab).Enabled = true;
        _engine.RecordObservation(FacilityKey.PharmacyLab, FacilityPhase.NeedsManual, "C", null, "材料不足");
        Assert.Null(_engine.ComputeNextRunAt(_plan, _settings));
    }

    [Fact]
    public void Failure_backoff_delays_next_run_until_gate()
    {
        _plan.For(FacilityKey.Workbench).Enabled = true;
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.ReadyToCollect, "A", null, null);
        _engine.MarkRunFinished("失败", failed: true, failureRetryMinutes: 30);

        Assert.Equal(_clock.Now.AddMinutes(30), _engine.ComputeNextRunAt(_plan, _settings));

        _engine.MarkRunFinished("成功", failed: false, failureRetryMinutes: 30);
        Assert.Equal(_clock.Now, _engine.ComputeNextRunAt(_plan, _settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败不影响断言 */ }
    }
}
