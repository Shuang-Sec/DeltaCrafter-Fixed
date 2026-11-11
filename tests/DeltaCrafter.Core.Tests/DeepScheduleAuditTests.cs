using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>真实执行守卫与调度检查的异常路径；外部窗口、电源和通知全部替换为无副作用边界。</summary>
public sealed class DeepScheduleAuditTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 23, 58, 0, TimeSpan.FromHours(8));
    }

    private sealed class WindowGuard : IAppWindowGuard
    {
        public int Minimized, Restored;
        public bool FailMinimize, FailRestore;
        public void MinimizeForRun()
        {
            Minimized++;
            if (FailMinimize) throw new InvalidOperationException("模拟助手窗口最小化失败");
        }
        public void RestoreAfterRun()
        {
            Restored++;
            if (FailRestore) throw new InvalidOperationException("模拟助手窗口恢复失败");
        }
    }

    private sealed class Notifier : INotifier
    {
        public bool Fails;
        public void Notify(string title, string message)
        {
            if (Fails) throw new InvalidOperationException("模拟系统通知不可用");
        }
    }

    private readonly string _directory = Path.Combine(
        "D:/Desktop/codex文件/2026-10-26-三角洲制造深度测试/scheduler-tests/state", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly WindowGuard _window = new();
    private readonly Notifier _notifier = new();
    private readonly AppSettings _settings = new() { AutoLoopEnabled = true };
    private readonly CraftPlanConfig _plan = CraftPlanConfig.CreateDefault();
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private readonly ScheduleEngine _engine;
    private readonly AutomationCoordinator _coordinator;
    private int _powerCalls;
    private bool _failPower;

    public DeepScheduleAuditTests()
    {
        Directory.CreateDirectory(_directory);
        _plan.For(FacilityKey.Workbench).Enabled = true;
        _engine = new ScheduleEngine(new JsonStoreBrick(), Path.Combine(_directory, "state.json"), _clock, _log);
        var probe = new ScreenProbe(null!, null!, null!, null!, () => new AnchorTable { Calibrated = true }, "", _log);
        _coordinator = new AutomationCoordinator(null!, null!, null!, null!, null!, null!, null!, null!,
            _window, null!, _engine, probe, null!, null!, () => _settings, () => _plan, _notifier, _clock, _log,
            active => { _powerCalls++; if (_failPower) throw new InvalidOperationException("模拟电源边界异常"); });
    }

    private Task<RunReport> Execute(Func<RunReport, CancellationToken, Task>? body = null,
        CancellationToken external = default) =>
        _coordinator.ExecuteGuardedAsync("深度测试", true, false,
            body ?? ((r, _) => { r.Add("实际工作已完成"); return Task.CompletedTask; }), external);

    [Fact]
    public async Task Automatic_recovery_does_not_start_after_the_user_switches_auto_off()
    {
        _settings.AutoLoopEnabled = false;
        int bodyCalls = 0;
        await _coordinator.ExecuteGuardedAsync("断线恢复", false, false,
            (_, _) => { bodyCalls++; return Task.CompletedTask; }, automaticTrigger: true);
        Assert.Equal(0, bodyCalls);
        Assert.Equal(0, _window.Minimized);
        Assert.Equal(0, _powerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_cancelled_public_operation_does_not_touch_window_power_or_state(bool sync)
    {
        var report = await (sync
            ? _coordinator.SyncFacilitiesAsync("已取消识别", new CancellationToken(true))
            : _coordinator.RunOnceAsync("已取消运行", new CancellationToken(true)));
        Assert.Equal(0, _powerCalls);
        Assert.Equal(0, _window.Minimized);
        Assert.Null(_engine.State.LastRunAt);
        Assert.False(_coordinator.IsRunning);
        Assert.True(report.HasFailure);
    }

    [Fact]
    public async Task Stopping_the_current_round_pauses_automatic_runs_without_changing_the_saved_switch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Execute(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await entered.Task;
        _coordinator.RequestStop();
        Assert.True((await running).HasFailure);
        int calls = _window.Minimized;
        _coordinator.TickScheduler();
        Assert.Equal(calls, _window.Minimized);
        Assert.True(_settings.AutoLoopEnabled);
        Assert.Null(_engine.State.LastRunAt);
    }

    [Fact]
    public async Task A_cancelled_resume_request_keeps_automatic_runs_paused()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Execute(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await entered.Task;
        _coordinator.RequestStop();
        await running;
        await _coordinator.RunOnceAsync("已取消恢复", new CancellationToken(true));
        int calls = _window.Minimized;
        _clock.Now = _clock.Now.AddHours(1); // 即使失败退避已过，取消的请求也不能恢复循环。
        _coordinator.TickScheduler();
        Assert.Equal(calls, _window.Minimized);
        Assert.True(_settings.AutoLoopEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_public_operation_resumes_automatic_scheduling_after_a_stop(bool sync)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Execute(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await entered.Task;
        _coordinator.RequestStop();
        await running;
        // 流程依赖为空，显式执行会在启动步骤失败；这里仅验证恢复意图及随后真实tick。
        await (sync
            ? _coordinator.SyncFacilitiesAsync("明确恢复识别", CancellationToken.None)
            : _coordinator.RunOnceAsync("明确恢复执行", CancellationToken.None));
        int calls = _window.Minimized;
        _clock.Now = _clock.Now.AddHours(1);
        _coordinator.TickScheduler();
        Assert.True(_window.Minimized > calls);
    }

    [Fact]
    public async Task Minimize_failure_is_reported_and_does_not_leak_the_run_lock()
    {
        _window.FailMinimize = true;
        var report = await Execute();
        Assert.True(report.HasFailure);
        Assert.False(_coordinator.IsRunning);
        _window.FailMinimize = false;
        Assert.False((await Execute()).HasFailure);
    }

    [Fact]
    public async Task Power_boundary_failure_does_not_leak_the_run_lock()
    {
        _failPower = true;
        Assert.True((await Execute()).HasFailure);
        Assert.False(_coordinator.IsRunning);
        _failPower = false;
        Assert.False((await Execute()).HasFailure);
    }

    [Fact]
    public async Task Window_restore_failure_does_not_erase_success_or_leak_the_run_lock()
    {
        _window.FailRestore = true;
        var report = await Execute();
        Assert.False(report.HasFailure);
        Assert.False(_engine.State.LastRunFailed);
        Assert.False(_coordinator.IsRunning);
        _window.FailRestore = false;
        Assert.False((await Execute()).HasFailure);
    }

    [Fact]
    public async Task A_broken_notifier_cannot_turn_completed_work_into_a_failed_round()
    {
        _notifier.Fails = true;
        var report = await Execute();
        Assert.False(report.HasFailure);
        Assert.False(_engine.State.LastRunFailed);
        Assert.Equal("实际工作已完成", _engine.State.LastRunSummary);
        Assert.Null(_engine.State.FailureBackoffUntil);
        Assert.False(_coordinator.IsRunning);
    }

    [Fact]
    public async Task A_broken_notifier_preserves_the_original_failure_and_releases_the_lock()
    {
        _notifier.Fails = true;
        var report = await Execute((_, _) => throw new InvalidOperationException("原始制造步骤失败"));
        Assert.True(report.HasFailure);
        Assert.Equal("原始制造步骤失败", _engine.State.LastRunSummary);
        Assert.Equal(_clock.Now.AddMinutes(30), _engine.State.FailureBackoffUntil);
        Assert.False(_coordinator.IsRunning);
    }

    [Fact]
    public async Task Cancelling_the_external_token_stops_the_body_and_pauses_the_next_automatic_tick()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Execute(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        RunReport report;
        try { report = await running.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { if (!running.IsCompleted) _coordinator.RequestStop(); }
        Assert.True(report.HasFailure);
        Assert.False(_coordinator.IsRunning);
        int calls = _window.Minimized;
        _coordinator.TickScheduler();
        Assert.Equal(calls, _window.Minimized);
        Assert.True(_settings.AutoLoopEnabled);
    }

    [Fact]
    public async Task A_concurrent_trigger_cannot_enter_the_window_guard_or_the_body()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Execute(async (_, _) => { entered.SetResult(); await release.Task; });
        await entered.Task;
        bool secondBodyEntered = false;
        var second = await Execute((_, _) => { secondBodyEntered = true; return Task.CompletedTask; });
        Assert.False(secondBodyEntered);
        Assert.Equal(1, _window.Minimized);
        Assert.Contains("已有任务在执行", second.Summary());
        release.SetResult();
        Assert.False((await first).HasFailure);
        Assert.False(_coordinator.IsRunning);
    }

    [Fact]
    public async Task Warehouse_failure_keeps_its_backoff_even_when_window_restore_and_notification_both_fail()
    {
        _notifier.Fails = true;
        _window.FailRestore = true;
        var report = await Execute((_, _) => throw new WarehouseFullException("领取产物"));
        Assert.True(report.HasFailure);
        Assert.True(_engine.State.LastRunFailed);
        Assert.Equal(_clock.Now.AddMinutes(30), _engine.State.FailureBackoffUntil);
        Assert.False(_coordinator.IsRunning);
        Assert.Equal(1, _window.Restored);
    }

    [Fact]
    public void Prewarm_never_starts_before_the_craft_finishes()
    {
        var ready = _clock.Now.AddSeconds(600);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "夜间制造", ready, null);

        var next = _engine.ComputeNextRunAt(_plan, _settings);
        Assert.NotNull(next);
        var prewarmAt = _coordinator.PrewarmAt(next.Value);
        Assert.True(prewarmAt >= ready,
            $"预启动不应早于倒计时结束：{prewarmAt:HH:mm:ss} < {ready:HH:mm:ss}");
        Assert.True(prewarmAt <= next.Value);
    }

    [Fact]
    public async Task Unknown_screen_stuck_pauses_automatic_runs_for_two_hours()
    {
        // 对局/邮件页这类认不出的界面：按过一次 ESC 仍回不到大厅时，不能每 30 分钟去拉一次游戏。
        var report = await Execute((_, _) => throw new UnknownScreenStuckException(
            "等待大厅", "第 1 次 ESC 后仍无法识别", null, null));

        Assert.True(report.HasFailure);
        Assert.True(_engine.State.LastRunFailed);
        Assert.Equal(_clock.Now.AddMinutes(AutomationCoordinator.UnknownScreenPauseMinutes),
            _engine.State.FailureBackoffUntil);
    }

    [Fact]
    public async Task Ordinary_failure_still_uses_the_configured_backoff()
    {
        _settings.FailureRetryMinutes = 30;
        await Execute((_, _) => throw new StepFailedException("深度测试步骤", "普通失败"));
        Assert.Equal(_clock.Now.AddMinutes(30), _engine.State.FailureBackoffUntil);
    }

    [Theory]
    [InlineData(true, false)]  // 游戏窗口正常可见
    [InlineData(false, true)]  // 游戏最小化在任务栏
    public void Running_game_is_never_prewarmed(bool visible, bool minimized)
    {
        // 用户要求：领取时间之前不要把正在运行的游戏窗口拉起来，哪怕它是最小化的。
        var now = _clock.Now;
        Assert.False(AutomationCoordinator.ShouldPrewarm(now, now.AddMinutes(-1), false, visible, minimized));
    }

    [Fact]
    public void Closed_game_is_prewarmed_once_when_the_time_comes()
    {
        var now = _clock.Now;
        Assert.True(AutomationCoordinator.ShouldPrewarm(now, now.AddSeconds(-1), false, false, false));
        Assert.False(AutomationCoordinator.ShouldPrewarm(now, now.AddSeconds(1), false, false, false));
        Assert.False(AutomationCoordinator.ShouldPrewarm(now, now.AddSeconds(-1), true, false, false));
    }

    [Fact]
    public void Stopping_while_idle_also_prevents_an_automatic_prewarm()
    {
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "跨日制造", _clock.Now.AddSeconds(30), null);
        _coordinator.RequestStop();
        _coordinator.TickScheduler();
        Assert.Equal(0, _window.Minimized);
        Assert.Null(_engine.State.LastRunAt);
        Assert.Null(_coordinator.Status.NextRunAt);
    }

    [Fact]
    public void A_cancelled_scheduler_tick_has_no_window_or_power_side_effects()
    {
        _coordinator.TickScheduler(new CancellationToken(true));
        Assert.Equal(0, _powerCalls);
        Assert.Equal(0, _window.Minimized);
        Assert.Null(_engine.State.LastRunAt);
    }

    [Fact]
    public async Task A_status_subscriber_failure_cannot_overwrite_completed_work_or_leak_the_lock()
    {
        _coordinator.StatusChanged += _ => throw new InvalidOperationException("模拟界面已释放");
        var report = await Execute();
        Assert.False(report.HasFailure);
        Assert.False(_engine.State.LastRunFailed);
        Assert.Equal("实际工作已完成", _engine.State.LastRunSummary);
        Assert.False(_coordinator.IsRunning);
    }

    [Fact]
    public void A_timer_crossing_midnight_survives_reload_with_its_offset_and_buffer()
    {
        var ready = _clock.Now.AddHours(6);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "原在制品", ready, null);
        var reloaded = new ScheduleEngine(new JsonStoreBrick(), Path.Combine(_directory, "state.json"), _clock, _log);
        var next = reloaded.ComputeNextRunAt(_plan, _settings);
        Assert.NotNull(next);
        Assert.InRange(next.Value, ready.AddSeconds(60), ready.AddSeconds(300));
        Assert.Equal(27, reloaded.State.For(FacilityKey.Workbench).ReadyAt!.Value.Day);
    }

    [Fact]
    public void Unknown_timer_checkpoint_requests_observation_and_obeys_failure_backoff_across_midnight()
    {
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "已开工待读时", null, "待核对");
        Assert.Equal(_clock.Now, _engine.ComputeNextRunAt(_plan, _settings));
        _engine.MarkRunFinished("后续观察失败", true, 30);
        var reloaded = new ScheduleEngine(new JsonStoreBrick(), Path.Combine(_directory, "state.json"), _clock, _log);
        Assert.Equal(_clock.Now.AddMinutes(30), reloaded.ComputeNextRunAt(_plan, _settings));
        Assert.Equal(FacilityPhase.Crafting, reloaded.State.For(FacilityKey.Workbench).Phase);
    }

    [Fact]
    public void Changing_the_next_recipe_does_not_change_the_in_progress_recipe_or_finish_time()
    {
        var ready = _clock.Now.AddHours(6);
        _engine.RecordObservation(FacilityKey.Workbench, FacilityPhase.Crafting, "5.45×39mm BT", ready, null);
        _plan.For(FacilityKey.Workbench).SetCustomSelection("4.6×30mm AP SX", "4.6×30mm AP SX");
        Assert.Equal("5.45×39mm BT", _engine.State.For(FacilityKey.Workbench).ItemName);
        var next = _engine.ComputeNextRunAt(_plan, _settings);
        Assert.NotNull(next);
        Assert.InRange(next.Value, ready.AddSeconds(60), ready.AddSeconds(300));
    }

    public void Dispose() { _coordinator.Dispose(); (_log as IDisposable)?.Dispose(); }
}
