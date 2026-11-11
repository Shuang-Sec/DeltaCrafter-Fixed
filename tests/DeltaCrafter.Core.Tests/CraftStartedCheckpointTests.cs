using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class CraftStartedCheckpointTests : IDisposable
{
    private sealed class Clock : IClock { public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 13, 16, 17, TimeSpan.FromHours(8)); }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "craft-checkpoint-" + Guid.NewGuid());
    private readonly Clock _clock = new();
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private readonly ScheduleEngine _engine;
    private readonly AutomationCoordinator _coordinator;
    private readonly FacilityPlan _plan;

    public CraftStartedCheckpointTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new ScheduleEngine(new JsonStoreBrick(), Path.Combine(_dir, "state.json"), _clock, _log);
        var plan = CraftPlanConfig.CreateDefault();
        _plan = plan.For(FacilityKey.TechCenter); _plan.ItemName = "视点3倍瞄准镜"; _plan.Enabled = true;
        _engine.RecordObservation(FacilityKey.TechCenter, FacilityPhase.ReadyToCollect, "旧手电", _clock.Now.AddHours(-1), null);
        _coordinator = new AutomationCoordinator(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            _engine, null!, null!, null!, () => new AppSettings(), () => plan, null!, _clock, _log);
    }

    [Fact]
    public async Task Confirmed_start_survives_detail_failure_and_clears_the_old_completed_cycle()
    {
        int home = 0;
        var result = await Complete(() =>
        {
            var checkpoint = Reload();
            Assert.Equal(FacilityPhase.Crafting, checkpoint.Phase);
            Assert.Equal("视点3倍瞄准镜", checkpoint.ItemName);
            Assert.Null(checkpoint.ReadyAt);
            throw new StepFailedException("读取技术中心剩余时间", "真实OCR无法解析");
        }, () => { home++; return Task.CompletedTask; }, Checkpoint());
        Assert.True(result.Started); Assert.Null(result.Remaining); Assert.Equal(1, home);
        Assert.Equal(FacilityPhase.Crafting, Reload().Phase); Assert.Null(Reload().ReadyAt);
    }

    [Fact]
    public async Task Known_timer_is_saved_before_return_home_failure()
    {
        var time = new TimeSpan(5, 59, 50);
        await Assert.ThrowsAsync<StepFailedException>(() => Complete(() => Task.FromResult(time),
            () => throw new StepFailedException("返回总览", "页面校验失败"), Checkpoint()));
        Assert.Equal(_clock.Now + time, Reload().ReadyAt);
    }

    [Fact]
    public async Task Later_facility_failure_and_backoff_do_not_erase_the_started_facility()
    {
        var time = TimeSpan.FromHours(6);
        await Complete(() => Task.FromResult(time), () => Task.CompletedTask, Checkpoint());
        _engine.MarkRunFinished("后续设施仓库不足", true, 30);
        Assert.Equal(_clock.Now + time, Reload().ReadyAt);
        Assert.Equal(FacilityPhase.Crafting, Reload().Phase);
    }

    [Fact]
    public async Task Cancellation_is_not_downgraded_or_followed_by_an_escape()
    {
        int home = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Complete(
            () => throw new OperationCanceledException(), () => { home++; return Task.CompletedTask; }, Checkpoint()));
        Assert.Equal(0, home); Assert.Equal(FacilityPhase.Crafting, Reload().Phase); Assert.Null(Reload().ReadyAt);
    }

    [Fact]
    public async Task Checkpoint_write_failure_prevents_any_further_read_or_escape()
    {
        int reads = 0, home = 0;
        await Assert.ThrowsAsync<IOException>(() => Complete(() => { reads++; return Task.FromResult(TimeSpan.FromHours(1)); },
            () => { home++; return Task.CompletedTask; }, _ => throw new IOException("模拟磁盘写失败")));
        Assert.Equal(0, reads); Assert.Equal(0, home);
    }

    [Fact]
    public async Task Checkpoint_uses_the_execution_item_snapshot_even_if_plan_changes()
    {
        var callback = Checkpoint(); _plan.ItemName = "用户随后编辑的新物品";
        await Complete(() => Task.FromResult(TimeSpan.FromHours(1)), () => Task.CompletedTask, callback);
        Assert.Equal("视点3倍瞄准镜", Reload().ItemName);
    }

    [Fact]
    public async Task Production_click_is_not_repeated_when_confirmation_times_out()
    {
        int clicks = 0;
        var method = typeof(CraftStartFlow).GetMethod("ClickStartOnceAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(null, new object[] { (Action)(() => clicks++),
            (Func<Task>)(() => throw new StepFailedException("确认开工", "中止按钮未确认")), CancellationToken.None })!;
        await Assert.ThrowsAsync<StepFailedException>(() => task);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task Read_failure_does_not_retry_an_escape_that_also_fails()
    {
        int home = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => Complete(
            () => throw new StepFailedException("读取时间", "未知"),
            () => { home++; throw new StepFailedException("返页", "页面已变化"); }, Checkpoint()));
        Assert.Equal(1, home);
        Assert.Equal(FacilityPhase.Crafting, Reload().Phase);
    }

    [Fact]
    public async Task Known_checkpoint_uses_the_latest_clock_instead_of_the_start_time()
    {
        var original = _clock.Now;
        await Complete(() => { _clock.Now = original.AddSeconds(10); return Task.FromResult(TimeSpan.FromHours(6)); },
            () => Task.CompletedTask, Checkpoint());
        Assert.Equal(original.AddSeconds(10).AddHours(6), Reload().ReadyAt);
    }

    [Fact]
    public async Task Cancellation_before_click_does_not_start_production()
    {
        int clicks = 0;
        var method = typeof(CraftStartFlow).GetMethod("ClickStartOnceAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(null, new object[] { (Action)(() => clicks++),
            (Func<Task>)(() => Task.CompletedTask), new CancellationToken(true) })!;
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, clicks);
    }

    private Action<TimeSpan?> Checkpoint()
    {
        var method = typeof(AutomationCoordinator).GetMethod("CreateCraftStartedCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Action<TimeSpan?>)method.Invoke(_coordinator, new object[] { _plan })!;
    }

    private static async Task<CraftStartResult> Complete(Func<Task<TimeSpan>> read, Func<Task> home, Action<TimeSpan?> checkpoint)
    {
        var method = typeof(CraftStartFlow).GetMethod("CompleteConfirmedStartAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<CraftStartResult>)method.Invoke(null, new object[] { read, home, checkpoint })!;
    }

    private FacilityRuntime Reload() => new JsonStoreBrick().Load<ScheduleState>(Path.Combine(_dir, "state.json")).For(FacilityKey.TechCenter);
    public void Dispose() { _coordinator.Dispose(); (_log as IDisposable)?.Dispose(); try { Directory.Delete(_dir, true); } catch { } }
}
