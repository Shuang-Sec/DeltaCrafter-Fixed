using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

public sealed record LaunchOutcome(nint Hwnd, bool LaunchedByUs);

/// <summary>
/// 「确保游戏就绪并到达大厅」流程。游戏必须经启动器启动,完整链:
/// 启动器(OCR 找「开始游戏」点击)→ 游戏客户端(16:9 大窗,与启动器同名以比例区分)→
/// 模式选择(点烽火地带)→ 3D 基地(Tab)→ 大厅。仅识别到公告或生产页时发送 ESC；未知画面只等待。
/// </summary>
public sealed class LaunchFlow
{
    private const int MaxNetworkReconnectAttempts = 5;
    private static readonly TimeSpan NetworkReconnectWait = TimeSpan.FromSeconds(8);
    internal const string CollectionMenuScreen = "collection-menu";
    internal static readonly string[] KnownScreens =
        [AnchorKeys.NetworkReconnect, AnchorKeys.Lobby, AnchorKeys.SpecOpsHome, AnchorKeys.ModeSelect, AnchorKeys.Safehouse,
         AnchorKeys.Production, AnchorKeys.PromoAnnounce, CollectionMenuScreen];
    private static readonly NRect FullFrame = new() { X = 0, Y = 0, W = 1, H = 1 };

    private readonly GameProcessBrick _process;
    private readonly GameWindowBrick _window;
    private readonly ScreenProbe _probe;
    private readonly InputBrick _input;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private long? _lastNetworkRestartAt;

    internal enum LobbyAction { Arrived, Wait, SelectMode, Tab, EscapePromo, EscapeProduction,
        ConfirmNetworkReconnect, ReconnectLimit, PromoLimit, EscapeCollection }
    internal sealed class LobbyNavigationState
    {
        internal int PromoEscs;
        internal bool ProductionEscUsed;
        // 收藏入口可能先落在「增加容量」再落在「收藏室」；
        // 只对每个已确认的安全返回页最多发送两次 Esc，未知画面仍不输入。
        internal int CollectionEscs;
        internal int ReconnectClicks;
        internal bool NetworkRestartUsed;
    }

    // 同一生产等待循环可替换系统边界，离线回归无需启动游戏或等待真实超时。
    internal sealed record LauncherRuntime(
        Func<GameWindowInfo?> FindLauncher,
        Func<GameWindowInfo?> FindClient,
        Func<nint, bool> IsAlive,
        Action Launch,
        Func<GameWindowInfo, Func<bool>, CancellationToken, Task<bool>> TryStartAsync,
        Func<GameWindowInfo, Task<string?>> DumpAsync,
        Func<long> Now,
        Func<int, CancellationToken, Task> DelayAsync);

    internal sealed class NetworkReconnectRestartRequiredException(nint hwnd)
        : Exception("网络重连确认连续失败，已确认当前画面可安全重启游戏。")
    {
        internal nint Hwnd { get; } = hwnd;
    }

    internal static bool ConsumeNetworkRestartBudget(LobbyNavigationState state)
    {
        if (state.NetworkRestartUsed) return false;
        state.NetworkRestartUsed = true;
        return true;
    }

    internal static async Task<GameWindowInfo> RestartNetworkOnceAsync(LobbyNavigationState state,
        Func<Task<bool>> canRestart, Func<CancellationToken, Task> close,
        Func<CancellationToken, Task<GameWindowInfo>> launch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.NetworkRestartUsed || !await canRestart())
            throw new StepFailedException("断线重启", "本轮重启次数已用完，或未取得近期非对局场景的明确证据，已停止恢复。");
        ct.ThrowIfCancellationRequested();
        ConsumeNetworkRestartBudget(state);
        await close(ct);
        ct.ThrowIfCancellationRequested();
        return await launch(ct);
    }

    // 真实输入事务边界：测试可注入焦点与输入动作，不需要调用Win32或游戏。
    internal static void RunFocusedAction(Func<bool> ensureForeground, Action input, CancellationToken ct)
    {
        EnsureForegroundOrThrow(ensureForeground, ct);
        input();
    }

    private static void EnsureForegroundOrThrow(Func<bool> ensureForeground, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ensureForeground())
            throw new StepFailedException("窗口前台化", "无法确认目标窗口位于前台，已停止执行，不发送鼠标或键盘输入。");
        ct.ThrowIfCancellationRequested();
    }

    internal static LobbyAction DispatchLobbyScreen(string? screen, LobbyNavigationState state,
        Func<bool> ensureForeground, Action<LobbyAction> input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LobbyAction action;
        switch (screen)
        {
            case AnchorKeys.Lobby:
            case AnchorKeys.SpecOpsHome:
                return LobbyAction.Arrived;
            case AnchorKeys.ModeSelect:
                action = LobbyAction.SelectMode;
                break;
            case AnchorKeys.Safehouse:
                action = LobbyAction.Tab;
                break;
            case AnchorKeys.PromoAnnounce:
                if (++state.PromoEscs > 8) return LobbyAction.PromoLimit;
                action = LobbyAction.EscapePromo;
                break;
            case AnchorKeys.Production:
                if (state.ProductionEscUsed) return LobbyAction.Wait;
                state.ProductionEscUsed = true;
                action = LobbyAction.EscapeProduction;
                break;
            case CollectionMenuScreen:
                if (++state.CollectionEscs > 2) return LobbyAction.Wait;
                action = LobbyAction.EscapeCollection;
                break;
            case AnchorKeys.NetworkReconnect:
                if (++state.ReconnectClicks > MaxNetworkReconnectAttempts) return LobbyAction.ReconnectLimit;
                action = LobbyAction.ConfirmNetworkReconnect;
                break;
            default:
                // 无法区分加载、断线弹窗或正在进行的对局，不猜测任何全局输入。
                return LobbyAction.Wait;
        }
        RunFocusedAction(ensureForeground, () => input(action), ct);
        return action;
    }

    public LaunchFlow(GameProcessBrick process, GameWindowBrick window, ScreenProbe probe,
        InputBrick input, Func<AppSettings> settings, ILogger log)
    {
        _process = process;
        _window = window;
        _probe = probe;
        _input = input;
        _settings = settings;
        _log = log.ForContext<LaunchFlow>();
    }

    public async Task<LaunchOutcome> EnsureLobbyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = _settings();
        var game = _window.FindGameClient(s.WindowMatch);
        bool launched = false;
        var navigation = new LobbyNavigationState();
        while (true)
        {
            game = await FindOrStartClientAsync(game,
                () => _window.TryRestoreMinimizedCandidate(s.WindowMatch),
                () =>
                {
                    var restored = _window.FindGameClient(s.WindowMatch);
                    if (restored is not null) _log.Information("游戏窗口处于最小化,已还原。");
                    return restored;
                },
                () => _window.HasMinimizedCandidate(s.WindowMatch),
                async token =>
                {
                    var started = await StartViaLauncherAsync(s, token);
                    launched = true;
                    return started;
                }, (milliseconds, token) => Task.Delay(milliseconds, token), ct);
            if (!_window.TryEnsureForeground(game.Hwnd, TimeSpan.FromSeconds(3)))
                throw new StepFailedException("窗口前台化",
                    "无法将游戏窗口置于前台。请检查是否有其他置顶/管理员窗口阻挡。");
            _probe.BeginNetworkRecovery(game.Hwnd);
            navigation.ReconnectClicks = _probe.NetworkReconnectAttemptCount;
            try
            {
                await NavigateToLobbyAsync(game.Hwnd, s, navigation, ct);
                return new LaunchOutcome(game.Hwnd, launched);
            }
            catch (NetworkReconnectRestartRequiredException)
            {
                GameProcessBrick.ValidateLaunchPath(s.GamePath); // 关闭前先确认能重新拉起。
                var oldGame = game;
                game = await RestartNetworkOnceAsync(navigation, async () =>
                {
                    if (!s.AutoRestartOnNetworkReconnect || (_lastNetworkRestartAt is { } at && Environment.TickCount64 - at < 1_800_000)) return false;
                    return await _probe.IsSafeToRestartAfterNetworkReconnectAsync(oldGame.Hwnd, ct);
                }, async token =>
                {
                    _lastNetworkRestartAt = Environment.TickCount64;
                    _log.Warning("断线确认仍失败，将关闭已确认非对局的游戏并重新拉起一次。");
                    await _process.CloseForNetworkRecoveryAsync(oldGame.Hwnd, oldGame.ProcessId, token);
                    _probe.ForgetRestartSceneEvidence();
                }, token => StartViaLauncherAsync(s, token), ct);
                navigation.ReconnectClicks = 0;
                navigation.ProductionEscUsed = false;
                navigation.CollectionEscs = 0;
                navigation.PromoEscs = 0;
                launched = true;
            }
        }
    }

    // 与生产共用恢复/启动决策，测试可验证失败或取消后不会进入启动器。
    internal static async Task<GameWindowInfo> FindOrStartClientAsync(GameWindowInfo? game,
        Func<bool> tryRestoreMinimized, Func<GameWindowInfo?> findClient, Func<bool> hasMinimizedCandidate,
        Func<CancellationToken, Task<GameWindowInfo>> startClient,
        Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (game is not null) return game;
        bool attempted = tryRestoreMinimized();
        ct.ThrowIfCancellationRequested();
        if (attempted)
        {
            await delay(1200, ct);
            ct.ThrowIfCancellationRequested();
            game = findClient();
            ct.ThrowIfCancellationRequested();
            if (game is not null) return game;
        }
        // 只阻断仍最小化的候选；同名启动器已恢复时继续原有启动路径。
        bool stillMinimized = hasMinimizedCandidate();
        ct.ThrowIfCancellationRequested();
        if (stillMinimized)
            throw new StepFailedException("恢复游戏窗口",
                "匹配窗口仍处于最小化状态，无法确认游戏窗口可用。已停止本轮执行，未重复启动游戏；请先还原窗口并处理系统确认后重试。");
        return await startClient(ct);
    }

    /// <summary>经启动器把游戏客户端拉起来。「开始游戏」允许一次显式重试点击(30s 后仍无客户端)。</summary>
    private async Task<GameWindowInfo> StartViaLauncherAsync(AppSettings s, CancellationToken ct)
    {
        bool useWeGame = string.Equals(Path.GetFileNameWithoutExtension(s.GamePath), "wegame", StringComparison.OrdinalIgnoreCase);
        var runtime = new LauncherRuntime(
            () => useWeGame ? _window.FindWeGameLauncher(s.GamePath) : _window.FindLauncher(s.WindowMatch.TitleContains),
            () => _window.FindGameClient(s.WindowMatch),
            _window.IsAlive,
            () => _process.Launch(s.GamePath),
            (launcher, canStart, token) => TryStartLauncherAsync(launcher, useWeGame, s.GamePath, canStart, token),
            async launcher => (await _probe.DumpAsync(launcher.Hwnd, "fail-启动器")).PngPath,
            () => Environment.TickCount64,
            (milliseconds, token) => Task.Delay(milliseconds, token));
        return await WaitForLauncherClientAsync(s, useWeGame, runtime, _log, ct);
    }

    private async Task<bool> TryStartLauncherAsync(GameWindowInfo launcher, bool useWeGame,
        string configuredPath, Func<bool> canStart, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!canStart()) return false;
        if (useWeGame)
        {
            CapturedFrame? frame = null;
            return await ReadAndClickLauncherStartAsync(async token =>
                {
                    frame = _probe.Capture(launcher.Hwnd);
                    return await _probe.ReadWeGameStartButtonFromFrameAsync(frame, token);
                },
                () =>
                {
                    if (!_window.IsAlive(launcher.Hwnd)
                        || _window.FindWeGameLauncher(configuredPath, restore: false)?.Hwnd != launcher.Hwnd) return false;
                    if (frame is null) return true;
                    try
                    {
                        var rect = _window.ClientRectOnScreen(launcher.Hwnd);
                        return rect.Width == frame.Width && rect.Height == frame.Height;
                    }
                    catch (InvalidOperationException) { return false; }
                },
                () => _window.TryEnsureForeground(launcher.Hwnd, TimeSpan.FromSeconds(1)),
                line =>
                {
                    _log.Information("已确认WeGame三角洲详情页，点击实际启动按钮:({X},{Y})。", line.CenterX, line.CenterY);
                    _probe.ClickFramePoint(launcher.Hwnd, line.CenterX, line.CenterY);
                }, ct, canStart);
        }
        EnsureForegroundOrThrow(() => _window.TryEnsureForeground(launcher.Hwnd, TimeSpan.FromSeconds(1)), ct);
        foreach (var word in _probe.Anchors.Keywords.LauncherStart)
        {
            if (!canStart()) return false;
            var line = await _probe.FindLineAsync(launcher.Hwnd, FullFrame, word);
            if (line is null) continue;
            EnsureForegroundOrThrow(() => _window.TryEnsureForeground(launcher.Hwnd, TimeSpan.FromSeconds(1)), ct);
            if (!canStart()) return false;
            _log.Information("点击启动器「{Word}」。", word);
            _probe.ClickFramePoint(launcher.Hwnd, line.CenterX, line.CenterY);
            return true;
        }
        return false;
    }

    internal static async Task<bool> ReadAndClickLauncherStartAsync(
        Func<CancellationToken, Task<OcrLine?>> readStart, Func<bool> isCurrent,
        Func<bool> ensureForeground, Action<OcrLine> click, CancellationToken ct, Func<bool>? canStart = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!BeforeDeadline() || !isCurrent()) return false;
        try
        {
            EnsureForegroundOrThrow(ensureForeground, ct);
            var line = await readStart(ct);
            ct.ThrowIfCancellationRequested();
            if (line is null || !BeforeDeadline() || !isCurrent()) return false;
            EnsureForegroundOrThrow(ensureForeground, ct);
            // OCR之后及前台化之后均重核窗口；句柄或尺寸变更就丢弃旧帧坐标。
            if (!isCurrent()) return false;
            ct.ThrowIfCancellationRequested();
            // 与外层共用期限，OCR或前台化跨过期限后不再发送启动点击。
            if (!BeforeDeadline()) return false;
            click(line);
            return true;
        }
        catch (StepFailedException) when (!isCurrent())
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        bool BeforeDeadline() => canStart?.Invoke() ?? true;
    }

    internal static async Task<GameWindowInfo> WaitForLauncherClientAsync(AppSettings s, bool useWeGame,
        LauncherRuntime runtime, ILogger log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long deadline = runtime.Now() + s.LaunchTimeoutSeconds * 1000L;
        var existing = runtime.FindClient();
        if (existing is not null) return existing;
        var launcher = runtime.FindLauncher();
        if (launcher is null)
        {
            ct.ThrowIfCancellationRequested();
            runtime.Launch();
            while (launcher is null && runtime.Now() < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await WaitAsync(2000);
                var game = runtime.FindClient();
                if (game is not null) return game;
                launcher = runtime.FindLauncher();
            }
            ct.ThrowIfCancellationRequested();
            if (launcher is null)
                throw new StepFailedException("等待启动器窗口",
                    $"启动后 {s.LaunchTimeoutSeconds}s 内未发现{(useWeGame ? "WeGame" : $"标题含「{s.WindowMatch.TitleContains}」的")}启动器窗口。" +
                    "请确认游戏路径指向启动器/游戏可执行文件。");
            log.Information("启动器已打开:{Title}", launcher.Title);
        }
        else
        {
            log.Information("发现已打开的启动器:{Title}", launcher.Title);
        }

        int clicks = 0;
        long lastClickAt = 0;
        var lastLauncher = launcher;
        while (runtime.Now() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var game = runtime.FindClient();
            if (game is not null && game.Hwnd != launcher?.Hwnd)
            {
                log.Information("游戏客户端窗口已出现:{Title}", game.Title);
                await runtime.DelayAsync(2000, ct); // 等渲染器就绪,后续交给画面判定
                return game;
            }

            if (useWeGame)
            {
                var fresh = runtime.FindLauncher();
                if (fresh?.Hwnd != launcher?.Hwnd)
                    log.Information("WeGame启动窗口已切换:{OldHwnd}->{NewHwnd}，继续等待原定启动期限。",
                        launcher?.Hwnd, fresh?.Hwnd);
                launcher = fresh;
                if (fresh is not null) lastLauncher = fresh;
            }
            bool launcherAlive = launcher is not null && runtime.IsAlive(launcher.Hwnd);
            if (!useWeGame && !launcherAlive && clicks == 0)
                throw new StepFailedException("启动游戏客户端", "启动器窗口已消失且游戏客户端未出现。");

            // 点击「开始游戏」:最多 2 次(首点 + 30s 后一次显式重试),不盲目连点。
            if (launcherAlive && clicks < 2 && (clicks == 0 || runtime.Now() - lastClickAt > 30_000))
            {
                try
                {
                    if (await runtime.TryStartAsync(launcher!, () => runtime.Now() < deadline, ct))
                    {
                        clicks++;
                        lastClickAt = runtime.Now();
                        if (clicks > 1) log.Warning("客户端仍未出现,已重试点击启动器。");
                    }
                }
                catch (StepFailedException) when (useWeGame && !runtime.IsAlive(launcher!.Hwnd))
                {
                    // 登录窗可能在前台化或OCR期间销毁，仅此已确认的交接继续等待。
                    ct.ThrowIfCancellationRequested();
                    log.Information("WeGame窗口在识别期间关闭，等待后继窗口。");
                }
            }
            await WaitAsync(3000);
        }

        ct.ThrowIfCancellationRequested();
        string shot = "";
        if (runtime.IsAlive(lastLauncher.Hwnd))
            try { shot = await runtime.DumpAsync(lastLauncher) ?? ""; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Error(ex, "保存启动器现场失败。"); }
        ct.ThrowIfCancellationRequested();
        throw new StepFailedException("启动游戏客户端",
            $"{s.LaunchTimeoutSeconds}s 内游戏客户端窗口未出现(启动器可能在更新或等待登录)。" +
            (shot.Length > 0 ? $"诊断截图:{shot}" : ""), shot.Length > 0 ? shot : null);

        Task WaitAsync(int milliseconds) => runtime.DelayAsync(
            (int)Math.Max(0, Math.Min(milliseconds, deadline - runtime.Now())), ct);
    }

    private async Task NavigateToLobbyAsync(nint hwnd, AppSettings s, LobbyNavigationState navigation, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + s.LobbyTimeoutSeconds * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!_window.IsAlive(hwnd))
                throw new StepFailedException("等待大厅", "游戏窗口中途消失(游戏崩溃或被手动关闭)。");

            EnsureForegroundOrThrow(() => _window.TryEnsureForeground(hwnd, TimeSpan.FromSeconds(1)), ct);
            string? screen = await _probe.WhichScreenAsync(hwnd, KnownScreens);
            var action = DispatchLobbyScreen(screen, navigation,
                () => _window.TryEnsureForeground(hwnd, TimeSpan.FromSeconds(1)), navigationInput =>
                {
                    switch (navigationInput)
                    {
                        case LobbyAction.SelectMode:
                            // 下一段异步读取新鲜卡片坐标后才发送输入。
                            break;
                        case LobbyAction.Tab:
                            _probe.InvalidateRestartSceneForInput();
                            _input.PressTab();
                            break;
                        case LobbyAction.EscapePromo:
                            // 下一段根据当前公告提示选择空格或ESC，先不发送输入。
                            break;
                        case LobbyAction.EscapeProduction:
                        case LobbyAction.EscapeCollection:
                            _probe.InvalidateRestartSceneForInput();
                            _input.PressEscape();
                            break;
                        case LobbyAction.ConfirmNetworkReconnect:
                            // 实际按钮定位与点击在下方异步读取后完成。
                            break;
                    }
                }, ct);
            switch (action)
            {
                case LobbyAction.Arrived:
                    _log.Information("已到达{Screen}。", screen == AnchorKeys.Lobby ? "大厅" : "特勤处");
                    return;
                case LobbyAction.SelectMode:
                    if (await _probe.ClickModeEntryAsync(hwnd, ct))
                        _log.Information("模式选择界面,已按当前卡片文字位置点击「烽火地带」。");
                    await Task.Delay(2500, ct);
                    break;
                case LobbyAction.Tab:
                    _log.Information("特勤基地界面,按 Tab 进入大厅。");
                    await Task.Delay(2500, ct);
                    break;
                case LobbyAction.EscapePromo:
                    if(await _probe.TryDismissStartupPromoAsync(hwnd,ct))
                        _log.Information("已确认启动广告，按空格继续({N}/8)。",navigation.PromoEscs);
                    else if(await _probe.IsOnAsync(hwnd,AnchorKeys.PromoAnnounce))
                        RunFocusedAction(()=>_window.TryEnsureForeground(hwnd,TimeSpan.FromSeconds(1)),()=>
                        {
                            _probe.InvalidateRestartSceneForInput();_input.PressEscape();
                            _log.Information("活动公告页,按 ESC 跳过({N}/8)。", navigation.PromoEscs);
                        },ct);
                    await Task.Delay(1500, ct);
                    break;
                case LobbyAction.EscapeProduction:
                    _log.Information("已确认生产界面，按一次 ESC 返回特勤处总览。");
                    await Task.Delay(1500, ct);
                    break;
                case LobbyAction.EscapeCollection:
                    _log.Information("已确认收藏/容量页面及ESC返回提示，按第{N}/2次 ESC 返回大厅。",
                        navigation.CollectionEscs);
                    await Task.Delay(1500, ct);
                    break;
                case LobbyAction.ConfirmNetworkReconnect:
                    if (await _probe.TryHandleNetworkReconnectAsync(hwnd, ct))
                        _log.Information("已向确认重连按钮发送第{N}/{Max}次点击，等待画面响应。", navigation.ReconnectClicks, MaxNetworkReconnectAttempts);
                    navigation.ReconnectClicks = _probe.NetworkReconnectAttemptCount;
                    await Task.Delay(NetworkReconnectWait, ct);
                    break;
                case LobbyAction.ReconnectLimit:
                    var (reconnectPng, reconnectDump) = await _probe.DumpAsync(hwnd, "fail-网络重连");
                    if (s.AutoRestartOnNetworkReconnect && !navigation.NetworkRestartUsed
                        && await _probe.IsSafeToRestartAfterNetworkReconnectAsync(hwnd, ct))
                        throw new NetworkReconnectRestartRequiredException(hwnd);
                    throw new StepFailedException("确认网络重连",
                        $"网络重连提示连续出现超过{MaxNetworkReconnectAttempts}次，未点击返回登录。诊断截图:" + reconnectPng,
                        reconnectPng, reconnectDump);
                case LobbyAction.PromoLimit:
                    var (promoPng, promoDump) = await _probe.DumpAsync(hwnd, "fail-活动公告");
                    throw new StepFailedException("跳过活动公告",
                        $"按 ESC 8 次后活动公告仍未关闭。诊断截图:{promoPng}", promoPng, promoDump);
                default:
                    await _probe.StopOnUnknownConfirmationAsync(_probe.Capture(hwnd), hwnd, "等待大厅", ct);
                    await Task.Delay(3000, ct);
                    break;
            }
        }

        var (png, dump) = await _probe.DumpAsync(hwnd, "fail-等待大厅");
        throw new StepFailedException("等待大厅",
            $"{s.LobbyTimeoutSeconds}s 内未到达大厅；未识别画面只等待、不发送按键，请确认游戏当前界面后再执行。诊断截图:{png}", png, dump);
    }
}
