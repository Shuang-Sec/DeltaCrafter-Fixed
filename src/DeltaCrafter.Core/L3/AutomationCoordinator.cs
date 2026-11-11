using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;

namespace DeltaCrafter.Core.L3;

/// <summary>
/// 总编排(L3):定时循环 + 一轮执行 + 单步调试入口(见 partial)。
/// 一轮 = 就绪到大厅 → 进特勤处 → 总览一帧观察四槽位 → 逐设施 领取/开工 →
/// 末尾再观察一帧,以画面为准写调度状态(受阻设施保留「需人工」标记不被覆盖)。
/// 并发约束:同一时刻只允许一轮执行;失败中止整轮、保留游戏现场、通知并退避;
/// 「材料不足/未配置物品」是显式受阻,不算失败、不触发退避。
/// </summary>
public sealed partial class AutomationCoordinator : IDisposable
{
    private readonly LaunchFlow _launch;
    private readonly SpecOpsNavFlow _nav;
    private readonly CollectFlow _collect;
    private readonly CraftStartFlow _craft;
    private readonly AbortFlow _abort;
    private readonly CatalogScanFlow _scan;
    private readonly ICatalogSink _catalogSink;
    private readonly ICatalogLookup _catalog;
    private readonly IAppWindowGuard _windowGuard;
    private readonly ShutdownFlow _shutdown;
    private readonly ScheduleEngine _engine;
    private readonly ScreenProbe _probe;
    private readonly GameWindowBrick _windowBrick;
    private readonly Action<bool> _setSleepActive;
    private readonly Func<AppSettings> _settings;
    private readonly Func<CraftPlanConfig> _plan;
    private readonly INotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _cancellationGate = new();
    private CancellationTokenSource? _runCts;
    // 仅暂停本次应用会话，不改用户保存的自动循环开关；显式执行/识别任务才恢复。
    private volatile bool _autoSchedulePaused;
    private DateTimeOffset? _networkRecoveryBackoffUntil;

    public event Action<CoordinatorStatus>? StatusChanged;
    public CoordinatorStatus Status { get; private set; } = new(EngineMode.Idle, "空闲", null);
    public bool IsRunning => _runLock.CurrentCount == 0;

    public AutomationCoordinator(LaunchFlow launch, SpecOpsNavFlow nav, CollectFlow collect,
        CraftStartFlow craft, AbortFlow abort, CatalogScanFlow scan, ICatalogSink catalogSink,
        ICatalogLookup catalogLookup, IAppWindowGuard windowGuard, ShutdownFlow shutdown,
        ScheduleEngine engine,
        ScreenProbe probe, GameWindowBrick windowBrick, SleepGuardBrick sleepGuard,
        Func<AppSettings> settings, Func<CraftPlanConfig> plan, INotifier notifier,
        IClock clock, ILogger log)
        : this(launch, nav, collect, craft, abort, scan, catalogSink, catalogLookup,
            windowGuard, shutdown, engine, probe, windowBrick, sleepGuard,
            settings, plan, notifier, clock, log, active => sleepGuard.SetActive(active))
    {
    }

    // 把电源动作作为执行边界注入，纯逻辑测试无需创建线程或改变本机电源状态。
    internal AutomationCoordinator(LaunchFlow launch, SpecOpsNavFlow nav, CollectFlow collect,
        CraftStartFlow craft, AbortFlow abort, CatalogScanFlow scan, ICatalogSink catalogSink,
        ICatalogLookup catalogLookup, IAppWindowGuard windowGuard, ShutdownFlow shutdown,
        ScheduleEngine engine, ScreenProbe probe, GameWindowBrick windowBrick, SleepGuardBrick sleepGuard,
        Func<AppSettings> settings, Func<CraftPlanConfig> plan, INotifier notifier,
        IClock clock, ILogger log, Action<bool> setSleepActive)
    {
        _launch = launch; _nav = nav; _collect = collect; _craft = craft; _abort = abort;
        _scan = scan; _catalogSink = catalogSink; _catalog = catalogLookup;
        _windowGuard = windowGuard;
        _shutdown = shutdown;
        _engine = engine; _probe = probe; _windowBrick = windowBrick; _setSleepActive = setSleepActive;
        _settings = settings; _plan = plan; _notifier = notifier; _clock = clock;
        _log = log.ForContext<AutomationCoordinator>();
    }

    /// <summary>UI 读取的设施状态快照(浅拷贝,避免与执行线程共享可变对象)。</summary>
    public IReadOnlyList<FacilityRuntime> FacilitySnapshot() =>
        _engine.State.Facilities.Select(f => new FacilityRuntime
        {
            Key = f.Key, Phase = f.Phase, ItemName = f.ItemName,
            ReadyAt = f.ReadyAt, ManualReason = f.ManualReason, ObservedAt = f.ObservedAt,
        }).ToList();

    public (DateTimeOffset? LastRunAt, string? Summary, bool Failed) LastRunInfo()
    {
        var s = _engine.State;
        return (s.LastRunAt, s.LastRunSummary, s.LastRunFailed);
    }

    /// <summary>提前量:到点前这么多秒检查游戏是否已开,未开则预启动到大厅,
    /// 把启动器/登录/加载耗时消化在等待期里,到点即可直接收取。</summary>
    private const int PrewarmLeadSeconds = 120;
    private DateTimeOffset? _prewarmedFor;

    /// <summary>执行闸门:更新下载/安装期间置位,拒绝一切新触发(自动调度、预启动、
    /// 手动),避免安装程序在半截制造流程中结束游戏进程。可逆:更新失败即解除。
    /// 已在执行的一轮不受影响——调用方应等其自然结束。</summary>
    private volatile bool _runsBlocked;
    public async Task BlockNewRunsAndWaitAsync(CancellationToken ct)
    {
        _runsBlocked = true;
        try
        {
            // 取得并立即释放执行锁，证明在途任务已经结束。ExecuteGuardedAsync
            // 拿锁后会再次检查闸门，因此抢在本方法之前排队的触发也无法穿透。
            await _runLock.WaitAsync(ct);
            _runLock.Release();
        }
        catch
        {
            _runsBlocked = false;
            throw;
        }
    }

    public void UnblockRuns() => _runsBlocked = false;

    /// <summary>
    /// 在两轮制造之间执行短事务。利润推荐用它等待当前轮结束并阻止新轮插入,
    /// 保证四个设施的推荐作为一个完整批次写入。
    /// </summary>
    public async Task RunBetweenRoundsAsync(Func<Task> action, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>常驻调度循环。仅此处依据设置开合防睡眠;执行中强制防睡眠。</summary>
    public async Task RunSchedulerLoopAsync(CancellationToken appStop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(appStop))
            {
                await MonitorIdleNetworkReconnectAsync(appStop);
                TickScheduler(appStop);
            }
        }
        catch (OperationCanceledException) { /* 应用退出 */ }
    }

    /// <summary>一次调度检查；与定时器分离，便于按同一逻辑验证停止及退避。</summary>
    internal void TickScheduler(CancellationToken appStop = default)
    {
        if (appStop.IsCancellationRequested) return;
        var s = _settings();
        bool automaticEnabled = s.AutoLoopEnabled && !_autoSchedulePaused;
        var next = _engine.ComputeNextRunAt(_plan(), s);
        _setSleepActive(IsRunning || (automaticEnabled && s.PreventSleepWhileWaiting));
        if (!IsRunning)
            Publish(_autoSchedulePaused
                ? new CoordinatorStatus(EngineMode.Idle, "自动执行已暂停，手动执行或识别任务后恢复", null)
                : automaticEnabled
                    ? new CoordinatorStatus(EngineMode.WaitingSchedule, "等待下次执行", next)
                    : new CoordinatorStatus(EngineMode.Idle, "自动循环未开启", null));
        if (automaticEnabled && next is { } n && !IsRunning && !_runsBlocked)
        {
            if (_clock.Now >= n)
                _ = ExecuteGuardedAsync("定时触发", true, true, RunRoundAsync, appStop, automaticTrigger: true);
            else if (_clock.Now >= n.AddSeconds(-PrewarmLeadSeconds)
                     && _prewarmedFor != n
                     && _windowBrick.FindGameClient(s.WindowMatch) is null)
            {
                // 每个目标时刻只尝试一次预启动;失败会通知,正式轮到点仍会自行启动。
                _prewarmedFor = n;
                _ = PrewarmAsync(appStop);
            }
        }
    }

    internal static bool ShouldMonitorIdleNetworkReconnect(bool isRunning, bool runsBlocked) =>
        !isRunning && !runsBlocked;

    private async Task MonitorIdleNetworkReconnectAsync(CancellationToken ct)
    {
        if (!ShouldMonitorIdleNetworkReconnect(IsRunning, _runsBlocked)
            || !_settings().AutoLoopEnabled || _autoSchedulePaused) return;
        if (!await _runLock.WaitAsync(0, ct)) return;
        bool disconnected = false;
        try
        {
            if (_runsBlocked || _autoSchedulePaused || !_settings().AutoLoopEnabled) return;
            var game = _windowBrick.FindGameClient(_settings().WindowMatch);
            if (game is null || !_windowBrick.IsForeground(game.Hwnd)) return;
            disconnected = await _probe.IsNetworkReconnectVisibleAsync(game.Hwnd, ct);
            if (!disconnected)
                disconnected = await _probe.WhichScreenAsync(game.Hwnd, LaunchFlow.KnownScreens) == AnchorKeys.NetworkReconnect;
        }
        catch (StepFailedException ex)
        {
            _log.Debug("空闲断线观察中断:{Reason}", ex.Message);
        }
        finally { _runLock.Release(); }
        if (!disconnected || (_networkRecoveryBackoffUntil is { } until && until > _clock.Now)) return;
        _networkRecoveryBackoffUntil = _clock.Now.AddMinutes(Math.Max(1, _settings().FailureRetryMinutes));
        // 恢复连接与制造分开：制造的失败退避和材料购买次数不被后台重连清除。
        var report = await ExecuteGuardedAsync("空闲断线恢复", false, true, async (result, token) =>
        {
            await _launch.EnsureLobbyAsync(token);
            result.Add("游戏连接已恢复，制造继续按原定调度执行");
        }, ct, automaticTrigger: true);
        if (!report.HasFailure) _networkRecoveryBackoffUntil = null;
    }

    /// <summary>预启动:只把游戏带到大厅就收手,不进特勤处、不动任何设施。
    /// 占用与正式轮相同的执行闸门——预启动未完成时到点的正式轮会顺延到它结束后立即开始。</summary>
    private Task<RunReport> PrewarmAsync(CancellationToken external) =>
        ExecuteGuardedAsync("预启动游戏", affectsSchedule: false, requiresCalibration: true,
            async (report, ct) =>
            {
                Publish(new CoordinatorStatus(EngineMode.Running, "预启动游戏(提前上号等待到点)…", null));
                await _launch.EnsureLobbyAsync(ct);
                report.Add("游戏已预启动至大厅,等待到点执行");
            }, external, automaticTrigger: true);

    public void RequestStop()
    {
        CancellationTokenSource? running;
        lock (_cancellationGate)
        {
            _autoSchedulePaused = true;
            running = _runCts;
        }
        try { running?.Cancel(); }
        catch (ObjectDisposedException) { /* 本轮恰好已退出，暂停标记仍有效。 */ }
    }

    /// <summary>中止指定设施的当前制造(总览页「取消」)。成功后把该设施置为空闲,
    /// 下一轮会按(修正后的)计划重新开工。不自动关游戏——留给用户核对/改物品。</summary>
    public Task<RunReport> AbortFacilityAsync(FacilityKey key) =>
        ExecuteGuardedAsync($"中止{FacilityKeys.DisplayName(key)}",
            affectsSchedule: false, requiresCalibration: true, async (report, ct) =>
            {
                var launch = await _launch.EnsureLobbyAsync(ct);
                await _nav.EnterSpecOpsAsync(launch.Hwnd, ct);
                var result = await _abort.AbortAsync(launch.Hwnd, key, ct);
                if (result.Aborted)
                    _engine.RecordObservation(key, FacilityPhase.Idle, "", null, null);
                report.Add(result.Message);
            });

    public Task<RunReport> RunOnceAsync(string trigger, CancellationToken external) =>
        ExecuteGuardedAsync(trigger, affectsSchedule: true, requiresCalibration: true,
            RunRoundAsync, external, resumeAutomatic: true);

    /// <summary>「识别当前任务」:上号观察四个设施,把画面上的状态/物品/剩余时间原样写入调度,
    /// 不领取、不开工。面向首次接管——特勤处里已有制造中的任务时,先同步进度再进入循环;
    /// 画面即事实:之前的「需人工」标记也会被本次观察结果覆盖。结束后按设置处置游戏。</summary>
    public Task<RunReport> SyncFacilitiesAsync(string trigger, CancellationToken external) =>
        ExecuteGuardedAsync(trigger, affectsSchedule: true, requiresCalibration: true,
            SyncRoundAsync, external, resumeAutomatic: true);

    private async Task SyncRoundAsync(RunReport report, CancellationToken ct)
    {
        _engine.MarkRunStarted();
        Publish(new CoordinatorStatus(EngineMode.Running, "启动/定位游戏…", null));
        var launch = await _launch.EnsureLobbyAsync(ct);
        Publish(new CoordinatorStatus(EngineMode.Running, "进入特勤处…", null));
        await _nav.EnterSpecOpsAsync(launch.Hwnd, ct);

        Publish(new CoordinatorStatus(EngineMode.Running, "识别设施状态…", null));
        var batch = await _collect.ObserveAllDetailedAsync(launch.Hwnd, ct);
        if (batch.Agreed.Count == 0)
            throw new StepFailedException("观察设施状态", batch.Message, batch.DiagnosticPng, batch.DiagnosticText);
        foreach (var key in FacilityKeys.All)
        {
            string name = FacilityKeys.DisplayName(key);
            if (!batch.Agreed.TryGetValue(key, out var o))
            {
                // 读不准就保留上次状态：空白或猜测会覆盖真实在制任务。
                _log.Warning("{Facility} 观察未判定，保留上次状态:{Reason}",
                    name, batch.Unresolved[key].Reason);
                report.Add($"{name}读数不确定，已保留上次状态");
                continue;
            }
            string item = DisplayItem(key, o.ItemName);
            _engine.RecordObservation(key, o.Phase, item,
                o.Phase == FacilityPhase.Crafting && o.Remaining is { } r ? _clock.Now + r : null,
                null);
            report.Add(o.Phase switch
            {
                FacilityPhase.Crafting =>
                    $"{name}制造中{(item.Length > 0 ? $"「{item}」" : "")}(剩余 {Fmt(o.Remaining!.Value)})",
                FacilityPhase.ReadyToCollect => $"{name}可领取「{item}」",
                _ => $"{name}空闲",
            });
        }

        await ApplyAfterRunAsync(launch.Hwnd, ct);
        if (!batch.IsComplete)
            throw new StepFailedException("观察设施状态", batch.Message, batch.DiagnosticPng, batch.DiagnosticText);
    }

    /// <summary>一轮结束时的游戏处置。自动循环开启且下一轮已迫在眉睫(≤90 秒,
    /// 如识别发现有设施可领取)时保持游戏打开直接衔接,避免「刚关就重开」的空转;
    /// 其余情况按「收取后行为」设置处置。</summary>
    private async Task ApplyAfterRunAsync(nint hwnd, CancellationToken ct)
    {
        var s = _settings();
        var next = _engine.ComputeNextRunAt(_plan(), s);
        if (s.AutoLoopEnabled && next is { } n && n <= _clock.Now.AddSeconds(90))
        {
            _log.Information("下一轮已在眼前({Next:HH:mm:ss}),保持游戏打开直接衔接。", n);
            return;
        }
        await _shutdown.ApplyAsync(s.AfterRun, hwnd, ct);
    }

    private async Task RunRoundAsync(RunReport report, CancellationToken ct)
    {
        _engine.MarkRunStarted();
        Publish(new CoordinatorStatus(EngineMode.Running, "启动/定位游戏…", null));
        var launch = await _launch.EnsureLobbyAsync(ct);
        Publish(new CoordinatorStatus(EngineMode.Running, "进入特勤处…", null));
        await _nav.EnterSpecOpsAsync(launch.Hwnd, ct);

        var plan = _plan().CreateExecutionSnapshot();
        bool autoReplenishMaterials = _settings().AutoReplenishMaterials;
        var blocked = new HashSet<FacilityKey>();

        Publish(new CoordinatorStatus(EngineMode.Running, "观察设施状态…", null));
        var batch = await _collect.ObserveAllDetailedAsync(launch.Hwnd, ct);
        if (batch.Agreed.Count == 0)
            throw new StepFailedException("观察设施状态", batch.Message, batch.DiagnosticPng, batch.DiagnosticText);

        // 先保存这一帧已确认的事实；后续某个工位受阻时，其他工位也不再显示旧周期。
        // 未判定工位不写盘：保留上次运行态，避免用空白覆盖真实在制任务。
        foreach (var fp in plan.Facilities.Where(f => f.Enabled && batch.Agreed.ContainsKey(f.Key)))
            RecordFinalObservation(fp, batch.Agreed[fp.Key]);
        if (!batch.IsComplete)
        {
            _log.Warning("观察不完整:{Message}", batch.Message);
            foreach (var key in batch.Unresolved.Keys)
                report.Add($"{FacilityKeys.DisplayName(key)}读数不确定，保留上次状态，本轮不动作");
        }

        foreach (var fp in plan.Facilities.Where(f => f.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            if (!batch.Agreed.TryGetValue(fp.Key, out var obs))
                continue; // 未判定：本轮不领取、不开工，绝不按猜测点击
            string name = FacilityKeys.DisplayName(fp.Key);
            Publish(new CoordinatorStatus(EngineMode.Running, $"处理{name}…", null));
            switch (obs.Phase)
            {
                case FacilityPhase.ReadyToCollect:
                    await CompleteCollectionAsync(fp.Key,
                        () => _collect.CollectAsync(launch.Hwnd, fp.Key, ct), async () =>
                        {
                            report.Add($"{name}已领取「{DisplayItem(fp.Key, obs.ItemName)}」");
                            await StartCraftForAsync(launch.Hwnd, fp, autoReplenishMaterials, report, blocked, ct);
                        }, ct);
                    break;
                case FacilityPhase.Idle:
                    await StartCraftForAsync(launch.Hwnd, fp, autoReplenishMaterials, report, blocked, ct);
                    break;
                case FacilityPhase.Crafting:
                    report.Add($"{name}制造中「{DisplayItem(fp.Key, obs.ItemName)}」(剩余 {Fmt(obs.Remaining!.Value)})");
                    break;
            }
        }

        // 末尾统一观察:以画面为准写调度状态;受阻设施跳过,保留 NeedsManual 不被覆盖。
        // 末尾观察不完整的工位同样保留本轮开头的已确认读数之后的运行态，绝不写猜测值。
        var final = await _collect.ObserveAllDetailedAsync(launch.Hwnd, ct);
        foreach (var fp in plan.Facilities.Where(f =>
                     f.Enabled && !blocked.Contains(f.Key) && final.Agreed.ContainsKey(f.Key)))
        {
            RecordFinalObservation(fp, final.Agreed[fp.Key]);
        }

        await ApplyAfterRunAsync(launch.Hwnd, ct);
        // 优先报告本轮开头（执行动作前）的观察缺陷：那时现场证据最完整。
        var incomplete = batch.IsComplete ? (final.IsComplete ? null : final) : batch;
        // 已确认的工位已经领取/开工；整轮仍按失败上报，保留退避与通知语义，
        // 避免"有空闲工位但状态读不准"时陷入密集重试。
        if (incomplete is not null)
            throw new StepFailedException("观察设施状态", incomplete.Message,
                incomplete.DiagnosticPng, incomplete.DiagnosticText);
    }

    /// <summary>将末尾真实观察落盘；未读到名称时保留未知，不能用下次计划冒充在制品。</summary>
    internal void RecordFinalObservation(FacilityPlan plan, FacilityObservation observation)
    {
        string item = DisplayItem(plan.Key, observation.ItemName);
        _engine.RecordObservation(plan.Key, observation.Phase, item,
            observation.Phase == FacilityPhase.Crafting && observation.Remaining is { } remaining
                ? _clock.Now + remaining : null,
            null);
    }

    /// <summary>领取与后续开工之间的实际事务边界，供落盘失败和取消回归验证。</summary>
    internal async Task CompleteCollectionAsync(FacilityKey key, Func<Task> collect,
        Func<Task> continueCraft, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await collect();
        // 领取成功不可回滚：先记录空闲，再做可能因仓库、材料或取消而失败的后续动作。
        _engine.RecordObservation(key, FacilityPhase.Idle, "", null, null);
        _log.Information("{Facility}领取检查点已保存:当前空闲，等待下一次开工。", FacilityKeys.DisplayName(key));
        ct.ThrowIfCancellationRequested();
        await continueCraft();
    }

    private async Task StartCraftForAsync(nint hwnd, FacilityPlan fp, bool autoReplenishMaterials,
        RunReport report, HashSet<FacilityKey> blocked, CancellationToken ct)
    {
        string name = FacilityKeys.DisplayName(fp.Key);
        if (string.IsNullOrWhiteSpace(fp.ItemName))
        {
            _engine.RecordObservation(fp.Key, FacilityPhase.NeedsManual, "", null, "未配置制造物品");
            blocked.Add(fp.Key);
            report.Add($"{name}未配置物品,等待人工设置");
            return;
        }
        // SearchName = 目录里的 OCR 原文(若从下拉选中),显示与匹配分离,抗识别误差。
        var result = await _craft.StartAsync(hwnd, fp.Key, fp.SearchName, fp.ItemName,
            autoReplenishMaterials, ct, CreateCraftStartedCheckpoint(fp));
        if (result.Started)
        {
            report.Add(result.Remaining is { } remaining
                ? $"{name}已开始「{fp.ItemName}」(剩余 {Fmt(remaining)})"
                : $"{name}已确认开始「{fp.ItemName}」，计时待总览校正");
        }
        else
        {
            _engine.RecordObservation(fp.Key, FacilityPhase.NeedsManual, fp.ItemName, null, result.BlockReason);
            blocked.Add(fp.Key);
            report.Add($"{name}受阻:{result.BlockReason},等待人工处理");
        }
    }

    /// <summary>闭包固定本轮实际配方；开工及准确读时各自立即写盘，不等整轮完成。</summary>
    internal Action<TimeSpan?> CreateCraftStartedCheckpoint(FacilityPlan plan)
    {
        var key = plan.Key;
        string item = plan.ItemName;
        return remaining =>
        {
            _engine.RecordObservation(key, FacilityPhase.Crafting, item,
                remaining is { } r ? _clock.Now + r : null,
                remaining is null ? "已确认开工，剩余时间待核对" : null);
            _log.Information("{Facility}开工检查点已保存:{Item}，计时{Timer}",
                FacilityKeys.DisplayName(key), item, remaining is { } t ? Fmt(t) : "待核对");
        };
    }

    internal async Task<RunReport> ExecuteGuardedAsync(string trigger, bool affectsSchedule,
        bool requiresCalibration, Func<RunReport, CancellationToken, Task> body,
        CancellationToken external = default, bool resumeAutomatic = false, bool automaticTrigger = false)
    {
        var report = new RunReport(trigger);
        if (external.IsCancellationRequested)
        {
            report.AddFailure("执行请求已取消");
            return report;
        }
        if (_runsBlocked)
        {
            _log.Warning("触发[{Trigger}]被忽略:更新安装进行中。", trigger);
            report.Add("更新安装进行中,本次触发被忽略");
            return report;
        }
        if (!await _runLock.WaitAsync(0, CancellationToken.None))
        {
            _log.Warning("触发[{Trigger}]被忽略:已有任务在执行。", trigger);
            report.Add("已有任务在执行,本次触发被忽略");
            return report;
        }
        CancellationTokenSource? running = null;
        bool executionStarted = false, windowTouched = false, powerTouched = false, resumedSchedule = false;
        try
        {
            if (_runsBlocked || (automaticTrigger && (_autoSchedulePaused || !_settings().AutoLoopEnabled)))
            {
                report.Add("自动执行已暂停或更新安装进行中,本次触发被忽略");
                return report;
            }
            // 取得执行锁后再次检查；预取消不触碰窗口、电源，也不覆盖既有调度记录。
            external.ThrowIfCancellationRequested();
            running = CancellationTokenSource.CreateLinkedTokenSource(external);
            lock (_cancellationGate)
            {
                // 停止可能发生在调度检查之后，拿到取消源发布锁时再次拦住自动触发。
                if (automaticTrigger && (_autoSchedulePaused || !_settings().AutoLoopEnabled))
                {
                    report.Add("自动执行已暂停,本次触发被忽略");
                    return report;
                }
                running.Token.ThrowIfCancellationRequested();
                _runCts = running;
                if (resumeAutomatic)
                {
                    resumedSchedule = _autoSchedulePaused;
                    _autoSchedulePaused = false;
                }
            }
            running.Token.ThrowIfCancellationRequested();
            executionStarted = true;
            powerTouched = true;
            _setSleepActive(true);
            windowTouched = true;
            _windowGuard.MinimizeForRun(); // 防止助手窗口盖住游戏,污染屏幕拷贝识别
            _log.Information("开始执行[{Trigger}]。", trigger);
            if (requiresCalibration && !_probe.Anchors.Calibrated)
                throw new StepFailedException("前置检查",
                    "锚点尚未校准(anchors.json calibrated=false)。请先按《构建与校准指南》完成校准。");
            await body(report, running.Token);

            if (affectsSchedule)
                _engine.MarkRunFinished(report.Summary(), failed: false, _settings().FailureRetryMinutes);
            var next = _engine.ComputeNextRunAt(_plan(), _settings());
            _log.Information("[{Trigger}]完成:{Summary}", trigger, report.Summary());
            if (affectsSchedule)
                NotifySafely("特勤处执行完成",
                    report.Summary() + (next is { } n ? $"。下次执行 {n:HH:mm}" : ""));
            Publish(new CoordinatorStatus(
                _settings().AutoLoopEnabled ? EngineMode.WaitingSchedule : EngineMode.Idle,
                "上次执行成功", next));
        }
        catch (OperationCanceledException)
        {
            report.AddFailure(executionStarted ? "执行已停止，自动循环已暂停" : "执行请求已取消");
            // 恢复意图与实际执行之间若被取消，必须还原先前暂停，不能被取消请求偷偷恢复。
            if (executionStarted || resumedSchedule) _autoSchedulePaused = true;
            _log.Information("[{Trigger}]已取消。", trigger);
            if (affectsSchedule && executionStarted) // 不进入失败退避，保留会话暂停直到显式恢复。
                _engine.MarkRunFinished(report.Summary(), failed: false, _settings().FailureRetryMinutes);
            if (executionStarted) Publish(new CoordinatorStatus(EngineMode.Idle, "自动执行已暂停", null));
        }
        catch (WarehouseFullException ex)
        {
            report.AddFailure(ex.Message);
            _log.Error(ex, "[{Trigger}]因仓库空间不足中止。", trigger);
            if (affectsSchedule)
                _engine.MarkRunFinished(report.Summary(), failed: true, _settings().FailureRetryMinutes);
            NotifySafely("仓库空间不足",
                "补齐材料或领取制造物品失败，请及时清理游戏仓库后再运行。");
            Publish(new CoordinatorStatus(EngineMode.Faulted, ex.Message,
                _engine.ComputeNextRunAt(_plan(), _settings())));
        }
        catch (Exception ex)
        {
            report.AddFailure(ex.Message);
            _log.Error(ex, "[{Trigger}]执行失败。", trigger);
            if (affectsSchedule)
                _engine.MarkRunFinished(report.Summary(), failed: true, _settings().FailureRetryMinutes);
            NotifySafely("特勤处执行失败", ex.Message);
            // 故意不关游戏:保留失败现场供人工检查。
            Publish(new CoordinatorStatus(EngineMode.Faulted, ex.Message,
                _engine.ComputeNextRunAt(_plan(), _settings())));
        }
        finally
        {
            // 恢复窗口/电源属于外围清理；任何一个失败都不能跳过取消源释放和执行锁释放。
            try
            {
                if (windowTouched)
                    try { _windowGuard.RestoreAfterRun(); }
                    catch (Exception ex) { _log.Warning(ex, "恢复助手窗口失败，执行结果保留。"); }
                if (powerTouched)
                    try
                    {
                        var s = _settings();
                        _setSleepActive(s.AutoLoopEnabled && !_autoSchedulePaused && s.PreventSleepWhileWaiting);
                    }
                    catch (Exception ex) { _log.Warning(ex, "恢复等待期电源状态失败。"); }
            }
            finally
            {
                lock (_cancellationGate)
                {
                    if (ReferenceEquals(_runCts, running)) _runCts = null;
                    running?.Dispose();
                }
                _runLock.Release();
            }
        }
        return report;
    }

    /// <summary>槽位 OCR 物品名 → 目录规范显示名;解析不出保留原文(可能是目录外物品,不猜)。
    /// 总览卡片与通知里呈现的是数据库写法,而非「7毛2×39mmAP」这类识别原文。</summary>
    private string DisplayItem(FacilityKey key, string ocrName) =>
        ocrName.Length == 0 ? "" : _catalog.ResolveDisplayName(key, ocrName) ?? ocrName;

    private void Publish(CoordinatorStatus status)
    {
        Status = status;
        if (StatusChanged is not { } listeners) return;
        foreach (Action<CoordinatorStatus> listener in listeners.GetInvocationList())
        {
            try { listener(status); }
            catch (Exception ex) { _log.Warning(ex, "界面状态通知失败，保留实际执行结果。"); }
        }
    }

    private void NotifySafely(string title, string message)
    {
        try { _notifier.Notify(title, message); }
        catch (Exception ex) { _log.Warning(ex, "发送通知失败，保留实际执行结果:{Title}", title); }
    }

    private static string Fmt(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public void Dispose() => _runCts?.Dispose();
}
