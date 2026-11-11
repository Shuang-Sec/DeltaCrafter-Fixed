using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>单帧计时读取结果；失败保留原文，不猜测时长。</summary>
internal sealed record CountdownOcrReadout(string Text, TimeSpan? Remaining);

/// <summary>详情标题读取结果；匹配始终使用完整配方身份。</summary>
internal sealed record RecipeTitleOcrReadout(string Text, bool Matched);

/// <summary>同一帧中的断线文案、两个按钮及确认按钮实际像素位置。</summary>
internal sealed record NetworkReconnectReadout(bool Detected, string Message, string Confirm,
    string ReturnLogin, OcrLine? ConfirmButton);
internal sealed record ModeSelectionReadout(bool Detected, OcrLine? Entry);

/// <summary>
/// 画面探针:把「窗口几何 + 截图 + OCR + 点击」组合成流程层可用的原语。
/// 所有读取都基于"捕获当下一帧",不缓存旧帧——过期画面比没有画面更危险。
/// </summary>
public sealed partial class ScreenProbe
{
    // 依据原始2560x1440像素标定；2048x1152是显示预览，不能当作原图尺寸。
    internal static readonly NRect NetworkReconnectDialogRoi = new() { X = 0.25, Y = 0.30, W = 0.50, H = 0.30 };
    internal static readonly NRect NetworkReconnectButtonRoi = new() { X = 0.50, Y = 0.59, W = 0.18, H = 0.075 };
    internal static readonly NRect NetworkReturnLoginButtonRoi = new() { X = 0.33, Y = 0.59, W = 0.18, H = 0.075 };
    internal static readonly NPoint NetworkReconnectConfirmPoint = new() { X = 0.583, Y = 0.625 };
    // “价格变动提醒”确认页与补齐弹窗共用底部金额按钮位置，必须二次确认一次。
    // 标题在上半部，购买成功清单在下半部；高度覆盖两处，避免只读到标题而漏判确认页。
    internal static readonly NRect PurchaseConfirmationDialogRoi = new() { X = 0.15, Y = 0.13, W = 0.55, H = 0.55 };
    // 2026-11-09 实测：旧按钮 ROI（0.49/0.68/0.06/0.04）太窄，把首位数字截掉（「、25，23B」）。
    // 绿色金额按钮实际横跨 x 0.43~0.57、y 0.665~0.719；这里覆盖整个按钮，
    // 点击点仍必须是 OCR 行中心且与该帧窗口尺寸一致。
    internal static readonly NRect PurchaseConfirmationButtonRoi = new() { X = 0.42, Y = 0.65, W = 0.16, H = 0.07 };
    internal static readonly NPoint PurchaseConfirmationPoint = new() { X = 0.500, Y = 0.699 };
    private readonly GameWindowBrick _window;
    private readonly ScreenCaptureBrick _capture;
    private readonly OcrBrick _ocr;
    private readonly InputBrick _input;
    private readonly Func<AnchorTable> _anchors;
    private readonly string _shotsDir;
    private readonly ILogger _log;
    private nint _networkReconnectMonitorHwnd;
    private long _networkReconnectLastClickAt;
    private int _networkReconnectMonitorClicks;
    private nint _lastCapturedHwnd;
    private long _lastCapturedAt;
    private nint _lastKnownSceneHwnd;
    private string? _lastKnownScene;
    private long _lastKnownSceneAt;
    private bool? _networkEpisodeSafeForRestart;
    private CapturedFrame? _networkReadFrame;
    private NetworkReconnectReadout? _networkReadout;
    private static readonly (int Threshold, double Scale)[] TimerFallbacks = [(80, 3.0), (130, 2.0)];

    public ScreenProbe(GameWindowBrick window, ScreenCaptureBrick capture, OcrBrick ocr,
        InputBrick input, Func<AnchorTable> anchors, string shotsDir, ILogger log)
    {
        _window = window;
        _capture = capture;
        _ocr = ocr;
        _input = input;
        _anchors = anchors;
        _shotsDir = shotsDir;
        _log = log.ForContext<ScreenProbe>();
    }

    public AnchorTable Anchors => _anchors();

    public ScreenSpec Screen(string name) => Anchors.Screen(name);

    internal static bool IsStartupPromoText(string continuation,string acquire) =>
        Normalize(continuation).Contains("继续",StringComparison.Ordinal)
        && Normalize(acquire).Contains("前往获取",StringComparison.Ordinal);

    internal async Task<bool> ReadStartupPromoFromFrameAsync(CapturedFrame frame,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 暗色小字与空格图标分开读取，避免图标和“继”粘连；原图验证需4倍字号。
        string continuation=(await _ocr.ReadAsync(frame,new NRect{X=.50,Y=.94,W=.03,H=.04},4.0)).FullText;
        foreach(double scale in new[]{1.5,1.0,2.0})
        {
            ct.ThrowIfCancellationRequested();
            string acquire=(await _ocr.ReadAsync(frame,new NRect{X=.72,Y=.78,W=.23,H=.10},scale)).FullText;
            if(IsStartupPromoText(continuation,acquire))return true;
        }
        return false;
    }

    internal async Task<bool> TryDismissStartupPromoAsync(nint hwnd,CancellationToken ct)
    {
        var frame=Capture(hwnd);
        if(!await ReadStartupPromoFromFrameAsync(frame,ct))return false;
        ct.ThrowIfCancellationRequested();
        RequireFocusedTarget(()=>_window.IsForeground(hwnd));
        InvalidateRestartSceneForInput();
        _input.PressSpace();
        return true;
    }

    internal static bool IsWeGameDeltaPageText(string text)
    {
        string normalized = Normalize(text);
        return normalized.Contains("三角洲行动", StringComparison.Ordinal)
            && normalized.Contains("烽火地带", StringComparison.Ordinal)
            && normalized.Contains("全面战场", StringComparison.Ordinal);
    }

    internal static OcrLine? SelectWeGameStartButton(IReadOnlyList<OcrLine> lines)
    {
        var matches = lines.Where(line => Normalize(line.Text) is "启动" or "开始游戏").ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>确认同一帧为三角洲详情页，仅返回右下角已就绪的启动按钮实际像素位置。</summary>
    internal async Task<OcrLine?> ReadWeGameStartButtonFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        var buttonRoi = new NRect { X = .77, Y = .90, W = .18, H = .08 };
        // 三处小字分别提对比度，避免全屏缩采样丢字；始终来自同一张原始帧。
        var titleRoi = new NRect { X = .032, Y = .312, W = .07, H = .04 };
        var modeRoi = new NRect { X = .24, Y = .49, W = .22, H = .055 };
        var secondRoi = new NRect { X = .375, Y = .50, W = .075, H = .04 };
        async Task<string> ReadLabel(NRect roi, int threshold)
        {
            ct.ThrowIfCancellationRequested();
            var enhanced = EnhanceTimerContrast(frame, new[] { roi }, threshold);
            return (await _ocr.ReadAsync(enhanced, roi, 2.0)).FullText;
        }
        string page = await ReadLabel(titleRoi, 170) + "\n" + await ReadLabel(modeRoi, 200)
            + "\n" + await ReadLabel(secondRoi, 90);
        if (!IsWeGameDeltaPageText(page)) return null;
        foreach (double scale in new[] { 2.0, 1.0, 1.5 })
        {
            ct.ThrowIfCancellationRequested();
            var read = await _ocr.ReadAsync(frame, buttonRoi, scale);
            var button = SelectWeGameStartButton(read.Lines);
            if (button is not null) return button;
        }
        return null;
    }

    internal static bool IsNetworkReconnectText(string text)
    {
        string normalized = Normalize(text);
        return normalized.Contains("网络断开", StringComparison.Ordinal)
            && normalized.Contains("再次连接", StringComparison.Ordinal)
            && normalized.Contains("确认重连", StringComparison.Ordinal)
            && normalized.Contains("返回登录", StringComparison.Ordinal);
    }

    internal static bool IsNetworkReconnectFrameText(string text) => IsNetworkReconnectText(text);

    internal static bool IsSafeToRestartAfterNetworkReconnectText(string text)
    {
        string normalized = Normalize(text);
        if (!IsNetworkReconnectText(normalized)) return false;
        string[] matchHudMarkers = ["生命值", "撤离", "击杀", "队友", "对局", "倒计时", "安全区", "武器", "弹药", "背包", "任务", "剩余时间", "价格变动", "补齐清单"];
        return !matchHudMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    internal async Task<bool> IsSafeToRestartAfterNetworkReconnectAsync(nint hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frame = Capture(hwnd);
        var dialog = await ReadNetworkReconnectFromFrameAsync(frame, ct);
        string full = (await _ocr.ReadAsync(frame, roi: null, upscale: 1.0)).FullText;
        ct.ThrowIfCancellationRequested();
        return dialog.Detected && _networkEpisodeSafeForRestart == true
            && IsSafeToRestartAfterNetworkReconnectText(dialog.Message + dialog.Confirm + dialog.ReturnLogin + "\n" + full);
    }

    internal static bool IsRecentRestartSafeScene(string? scene, long ageMs) =>
        ageMs >= 0 && ageMs <= 30_000 && scene is AnchorKeys.Lobby or AnchorKeys.SpecOpsHome
            or AnchorKeys.ModeSelect or AnchorKeys.Safehouse;

    private void RecordKnownScene(string? scene)
    {
        if (_lastCapturedHwnd == 0 || _window is null) return; // 离线图片不提供关闭真实游戏的依据。
        RecordRestartSceneEvidence(_lastCapturedHwnd, scene, _lastCapturedAt);
    }

    internal void RecordRestartSceneEvidence(nint hwnd, string? scene, long capturedAt)
    {
        _lastKnownSceneHwnd = hwnd;
        _lastKnownScene = scene;
        _lastKnownSceneAt = capturedAt;
        if (scene is not null)
        {
            _networkEpisodeSafeForRestart = null;
            _networkReconnectMonitorClicks = 0;
        }
        else if (_networkEpisodeSafeForRestart.HasValue)
            _networkEpisodeSafeForRestart = false;
    }

    internal bool HasRecentRestartSceneEvidence(nint hwnd, long now) =>
        hwnd == _lastKnownSceneHwnd && IsRecentRestartSafeScene(_lastKnownScene, now - _lastKnownSceneAt);

    /// <summary>改变页面或交易状态的输入撤销旧证据，直到新画面明确确认安全场景。</summary>
    internal void InvalidateRestartSceneForInput()
    {
        _lastKnownScene = null;
        _lastKnownSceneHwnd = 0;
        if (_networkEpisodeSafeForRestart.HasValue) _networkEpisodeSafeForRestart = false;
    }

    internal void BeginNetworkRecovery(nint hwnd)
    {
        if (_networkReconnectMonitorHwnd == hwnd) return;
        _networkReconnectMonitorHwnd = hwnd;
        _networkReconnectMonitorClicks = 0;
        _networkReconnectLastClickAt = 0;
    }

    internal int NetworkReconnectAttemptCount => _networkReconnectMonitorClicks;

    internal void ForgetRestartSceneEvidence()
    {
        _lastKnownScene = null;
        _lastKnownSceneHwnd = 0;
        _networkEpisodeSafeForRestart = null;
        _networkReconnectMonitorHwnd = 0;
        _networkReconnectMonitorClicks = 0;
    }

    internal static bool IsPurchaseConfirmationText(string text)
    {
        string normalized = Normalize(text);
        return normalized.Contains("价格变动提醒", StringComparison.Ordinal)
            && normalized.Contains("购买成功", StringComparison.Ordinal);
    }

    internal async Task<bool> IsPurchaseConfirmationAsync(nint hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frame = Capture(hwnd);
        string text = Normalize((await _ocr.ReadAsync(frame, PurchaseConfirmationDialogRoi, 1.5)).FullText);
        ct.ThrowIfCancellationRequested();
        return IsPurchaseConfirmationText(text);
    }

    internal async Task<string> ReadNetworkReconnectTextAsync(CapturedFrame frame, double upscale = 1.5)
        => (await _ocr.ReadAsync(frame, NetworkReconnectDialogRoi, upscale)).FullText;

    internal async Task<NetworkReconnectReadout> ReadNetworkReconnectFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ReferenceEquals(frame, _networkReadFrame) && _networkReadout is not null) return _networkReadout;
        string message = "", confirm = "", login = "";
        foreach (double scale in new[] { 1.5, 1.0, 2.0 })
        {
            ct.ThrowIfCancellationRequested();
            message = (await _ocr.ReadAsync(frame, NetworkReconnectDialogRoi, scale)).FullText;
            string compact = Normalize(message);
            if (!compact.Contains("网络断开", StringComparison.Ordinal) || !compact.Contains("再次连接", StringComparison.Ordinal)) continue;
            var button = await _ocr.ReadAsync(frame, NetworkReconnectButtonRoi, scale);
            confirm = button.FullText;
            login = (await _ocr.ReadAsync(frame, NetworkReturnLoginButtonRoi, scale)).FullText;
            ct.ThrowIfCancellationRequested();
            var targets = button.Lines.Where(line => Normalize(line.Text) == "确认重连").ToArray();
            if (targets.Length != 1 || !IsNetworkReconnectText(message + confirm + login)) continue;
            if (_lastCapturedHwnd != 0 && _window is not null && _networkEpisodeSafeForRestart is null)
                _networkEpisodeSafeForRestart = HasRecentRestartSceneEvidence(_lastCapturedHwnd, _lastCapturedAt);
            _networkReadFrame = frame;
            return _networkReadout = new NetworkReconnectReadout(true, message, confirm, login, targets[0]);
        }
        _networkReadFrame = frame;
        return _networkReadout = new NetworkReconnectReadout(false, message, confirm, login, null);
    }

    internal async Task<bool> IsNetworkReconnectVisibleAsync(nint hwnd, CancellationToken ct) =>
        (await ReadNetworkReconnectFromFrameAsync(Capture(hwnd), ct)).Detected;

    internal static bool IsModeSelectionMenuText(string text)
    {
        string normalized = Normalize(text);
        return normalized.Contains("烽火地带", StringComparison.Ordinal)
            && normalized.Contains("全面战场", StringComparison.Ordinal);
    }

    internal async Task<ModeSelectionReadout> ReadModeSelectionFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        // 当前客户端将卡片排在底部。分卡读取避免两张卡片同行被OCR合成一行。
        var firstCard = new NRect { X = .04, Y = .70, W = .28, H = .25 };
        var secondCard = new NRect { X = .32, Y = .70, W = .28, H = .25 };
        foreach (double scale in new[] { 1.5, 1.0, 2.0 })
        {
            ct.ThrowIfCancellationRequested();
            var first = await _ocr.ReadAsync(frame, firstCard, scale);
            var second = await _ocr.ReadAsync(frame, secondCard, scale);
            if (!IsModeSelectionMenuText(first.FullText + "\n" + second.FullText)) continue;
            var entries = first.Lines.Where(line => Normalize(line.Text).Contains("烽火地带",StringComparison.Ordinal)
                && !Normalize(line.Text).Contains("全面战场",StringComparison.Ordinal)).ToArray();
            if (entries.Length == 1) return new ModeSelectionReadout(true,entries[0]);
        }
        var roi = new NRect { X = .02, Y = .16, W = .26, H = .64 };
        foreach (double scale in new[] { 1.5, 1.0, 2.0 })
        {
            ct.ThrowIfCancellationRequested();
            var readout = await _ocr.ReadAsync(frame, roi, scale);
            if (!IsModeSelectionMenuText(readout.FullText)) continue;
            var targets = readout.Lines.Where(line => Normalize(line.Text).Contains("烽火地带", StringComparison.Ordinal)
                && !Normalize(line.Text).Contains("全面战场", StringComparison.Ordinal)).ToArray();
            if (targets.Length == 1) return new ModeSelectionReadout(true, targets[0]);
        }
        return new ModeSelectionReadout(false, null);
    }

    internal async Task<bool> ClickModeEntryAsync(nint hwnd, CancellationToken ct)
    {
        var frame = Capture(hwnd);
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        var current = await ReadModeSelectionFromFrameAsync(frame, ct);
        ct.ThrowIfCancellationRequested();
        if (current.Detected && current.Entry is { } entry)
        {
            ClickFramePoint(hwnd, entry.CenterX, entry.CenterY);
            return true;
        }
        // 兼容旧卡片布局：原探针必须在新鲜画面实际命中，才使用用户标定的旧点位。
        var spec = Screen(AnchorKeys.ModeSelect);
        string original = (await _ocr.ReadAsync(frame, spec.Probe.Roi, spec.Probe.Upscale)).FullText;
        ct.ThrowIfCancellationRequested();
        if (!Normalize(original).Contains(Normalize(spec.Probe.MustContain), StringComparison.Ordinal)) return false;
        ClickPoint(hwnd, AnchorKeys.ModeSelect, AnchorKeys.PointModeEntry);
        return true;
    }

    /// <summary>识别断线框并点击唯一安全动作“确认重连”。检测不到或文案不明确时不输入。</summary>
    internal async Task<bool> TryHandleNetworkReconnectAsync(nint hwnd, CancellationToken ct)
    {
        var bounds = _window.ClientRectOnScreen(hwnd);
        var frame = Capture(hwnd);
        long capturedAt = _lastCapturedAt;
        var dialog = await ReadNetworkReconnectFromFrameAsync(frame, ct);
        if (!dialog.Detected || dialog.ConfirmButton is null) return false;
        if (_networkReconnectMonitorHwnd != hwnd)
        {
            _networkReconnectMonitorHwnd = hwnd;
            _networkReconnectMonitorClicks = 0;
            _networkReconnectLastClickAt = 0;
        }
        if (Environment.TickCount64 - _networkReconnectLastClickAt < 7_000) return false;
        if (_networkReconnectMonitorClicks >= 5) return false;
        ct.ThrowIfCancellationRequested();
        // OcrLine坐标已经换算回原始帧像素，实际点击不使用预览图量取的坐标。
        ClickConfirmation(new ConfirmationReadout(ConfirmationKind.NetworkReconnect,
            dialog.Message + dialog.Confirm + dialog.ReturnLogin, dialog.ConfirmButton,
            frame, hwnd, capturedAt, bounds, NetworkReconnectButtonRoi, 1.5), ct);
        _networkReconnectMonitorClicks++;
        _networkReconnectLastClickAt = Environment.TickCount64;
        _log.Information("已向识别到的确认重连按钮发送点击({X:0},{Y:0})，等待游戏场景恢复。", dialog.ConfirmButton.CenterX, dialog.ConfirmButton.CenterY);
        return true;
    }

    public CapturedFrame Capture(nint hwnd)
    {
        long capturedAt = Environment.TickCount64;
        var frame = ReadFromFocusedTarget(() => _window.IsForeground(hwnd),
            () => _capture.CaptureClient(_window.ClientRectOnScreen(hwnd)));
        _lastCapturedHwnd = hwnd;
        _lastCapturedAt = capturedAt;
        return frame;
    }

    /// <summary>捕获前后均核对窗口；失焦时不把其他应用画面交给识别流程。</summary>
    internal static T ReadFromFocusedTarget<T>(Func<bool> isFocused, Func<T> read)
    {
        RequireFocusedTarget(isFocused);
        T result;
        try { result = read(); }
        catch (InvalidOperationException ex)
        {
            throw new StepFailedException("读取游戏画面",
                "窗口状态在读取期间发生变化，本轮已停止。" + ex.Message, inner: ex);
        }
        RequireFocusedTarget(isFocused);
        return result;
    }

    internal static void RequireFocusedTarget(Func<bool> isFocused)
    {
        if (!isFocused())
            throw new StepFailedException("窗口状态",
                "游戏窗口已切到后台、最小化或关闭，本轮已停止。请将游戏恢复到前台后重新执行。");
    }

    /// <summary>从同一原始帧读取详情标题，供实际流程和原图回放共同调用。</summary>
    internal Task<RecipeTitleOcrReadout> ReadRecipeTitleFromFrameAsync(CapturedFrame frame,
        NRect roi, string expected, CancellationToken ct) =>
        ReadRecipeTitleCandidatesAsync(expected, async (inner, scale) =>
        {
            // 实图中品质图标会与口径首位数字粘连。仅改变取样区域，不补字、不改型号。
            var titleRoi = inner ? new NRect
            {
                // 2560原图验证：.345仍残留图标，.350会裁掉“.300”的小数点；
                // .3475保留完整名称。按原ROI比例换算，不依赖窗口绝对像素。
                X = roi.X + roi.W * .075, Y = roi.Y + roi.H / 28,
                W = roi.W * .925, H = roi.H * 5 / 7,
            } : roi;
            string text = (await _ocr.ReadAsync(frame, titleRoi, scale)).FullText;
            _log.Debug("详情标题候选({Inner},{Scale:0.#}x):{Text}", inner ? "去图标" : "原区域", scale, Normalize(text));
            return text;
        }, ct);

    internal static async Task<RecipeTitleOcrReadout> ReadRecipeTitleCandidatesAsync(string expected,
        Func<bool, double, Task<string>> read, CancellationToken ct)
    {
        string primary = "";
        foreach (var (inner, scale) in new[] { (false, 2.0), (true, 2.0), (true, 1.0), (false, 1.5) })
        {
            ct.ThrowIfCancellationRequested();
            string text = await read(inner, scale);
            ct.ThrowIfCancellationRequested();
            if (!inner && scale == 2) primary = text;
            if (TextMatch.MatchesRecipeName(text, expected)) return new RecipeTitleOcrReadout(text, true);
        }
        return new RecipeTitleOcrReadout(primary, false);
    }

    private PixelRect FocusedClientRect(nint hwnd) => ReadFromFocusedTarget(
        () => _window.IsForeground(hwnd), () => _window.ClientRectOnScreen(hwnd));

    internal static bool IsLobbyMenuHeader(string text)
    {
        string header = Normalize(text);
        return header.Contains("开始游戏", StringComparison.Ordinal)
            && header.Contains("仓库", StringComparison.Ordinal)
            && header.Contains("特勤处", StringComparison.Ordinal);
    }

    /// <summary>从当前大厅顶栏选择唯一的“特勤处”标签，返回同一帧的原始像素中心。</summary>
    internal static OcrLine? SelectSpecOpsEntry(IReadOnlyList<OcrLine> lines)
    {
        var matches = lines.Where(line => Normalize(line.Text) == "特勤处").ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal async Task<bool> ClickSpecOpsEntryAsync(nint hwnd, CancellationToken ct)
    {
        var frame = Capture(hwnd);
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        var nav = new NRect { X = .06, Y = .025, W = .51, H = .05 };
        foreach (double scale in new[] { 1.5, 2.0, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            var read = await _ocr.ReadAsync(frame, nav, scale);
            var entry = SelectSpecOpsEntry(read.Lines);
            if (entry is null) continue;
            ClickFramePoint(hwnd, entry.CenterX, entry.CenterY);
            _log.Information("已按当前大厅帧中的“特勤处”标签点击导航，原图坐标({X:0},{Y:0})。",
                entry.CenterX, entry.CenterY);
            return true;
        }
        return false;
    }

    internal static async Task<bool> ReadLobbyMenuHeaderAsync(Func<double, Task<string>> read, CancellationToken ct)
    {
        foreach (double scale in new[] { 1.5, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            string text = await read(scale);
            ct.ThrowIfCancellationRequested();
            if (IsLobbyMenuHeader(text)) return true;
        }
        return false;
    }

    /// <summary>单帧界面匹配入口；导航与只读原图验证共用。</summary>
    internal async Task<bool> MatchScreenFromFrameAsync(CapturedFrame frame, string screenName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (screenName == AnchorKeys.NetworkReconnect)
            return (await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected;
        // 断线框优先于背景大厅/生产文字，不能把遮挡后的旧页面认作安全场景。
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected) return false;
        if (screenName == LaunchFlow.CollectionMenuScreen)
            return await ReadCollectionMenuFromFrameAsync(frame, ct);
        var spec = Screen(screenName);
        string text = (await _ocr.ReadAsync(frame, spec.Probe.Roi, spec.Probe.Upscale)).FullText;
        bool on = Normalize(text).Contains(Normalize(spec.Probe.MustContain), StringComparison.Ordinal);
        if (!on && screenName == AnchorKeys.ModeSelect)
            on = (await ReadModeSelectionFromFrameAsync(frame, ct)).Detected;
        if (!on && screenName == AnchorKeys.PromoAnnounce)
            on = await ReadStartupPromoFromFrameAsync(frame,ct);
        if (!on && screenName == AnchorKeys.Lobby)
            on = await ReadLobbyMenuHeaderAsync(async scale =>
                (await _ocr.ReadAsync(frame, new NRect { X = .06, Y = .025, W = .51, H = .05 }, scale)).FullText, ct);
        ct.ThrowIfCancellationRequested();
        if (on) RecordKnownScene(screenName);
        _log.Debug("界面判定 {Screen}:{Result}(读到:{Text})", screenName, on, Compact(text));
        return on;
    }

    public async Task<string> ReadRoiAsync(nint hwnd, NRect roi) =>
        (await _ocr.ReadAsync(Capture(hwnd), roi)).FullText;

    /// <summary>一次捕获、多区域识别,保证多个读数来自同一帧。
    /// upscale 可指定识别倍率:2x 适合小字号;1x 适合低对比大字(见 CollectFlow 交替倍率观察)。</summary>
    public async Task<string[]> ReadRoisAsync(nint hwnd, IReadOnlyList<NRect> rois, double upscale = 2.0)
    {
        var frame = Capture(hwnd);
        var result = new string[rois.Count];
        for (int i = 0; i < rois.Count; i++)
            result[i] = (await _ocr.ReadAsync(frame, rois[i], upscale)).FullText;
        return result;
    }

    internal async Task<string[]> ReadSlotRoisAsync(nint hwnd, IReadOnlyList<NRect> slots,
        IReadOnlyList<NRect> timers, double upscale)
        => await ReadSlotRoisFromFrameAsync(Capture(hwnd), slots, timers, upscale);

    /// <summary>生产读取和原图回放共用同一入口，避免只验证独立OCR却漏过槽位消费者。</summary>
    internal async Task<string[]> ReadSlotRoisFromFrameAsync(CapturedFrame frame,
        IReadOnlyList<NRect> slots, IReadOnlyList<NRect> timers, double upscale)
    {
        var result = new string[slots.Count + timers.Count + slots.Count];
        for (int index = 0; index < slots.Count; index++)
            result[index] = (await _ocr.ReadAsync(frame, slots[index], upscale)).FullText;
        var readings = await ReadCountdownRoisFromFrameAsync(frame, timers, labelledDetail: false);
        for (int index = 0; index < timers.Count; index++)
        {
            result[slots.Count + index] = readings[index].Text;
            // 独立计时 ROI 是唯一允许形近字归一化的上下文：该区域只可能出现倒计时。
            // 已有候选解析成功时保留原读数，避免二次改写；只有读到了字却解析不出
            // （实测「0J58：09」，仅差一个 J→0）才折成规范形交给严格分类器。
            if (readings[index].Remaining is null &&
                SlotTimerCanonicalizer.TryCanonicalize(readings[index].Text, out string canonical))
            {
                result[slots.Count + index] = canonical;
                _log.Debug("计时区{Index}归一化:{Raw}→{Canonical}", index,
                    Normalize(readings[index].Text), canonical);
            }
        }
        var names = await ReadSlotNamesFromFrameAsync(frame, slots);
        for (int index = 0; index < slots.Count; index++)
        {
            result[slots.Count + timers.Count + index] = names[index];
            // 完成态的低对比度标题会使整卡 OCR 读空（实测 .300 BLK），但同帧独立
            // 标题已通过至少三个候选共识。仅补齐空白卡片，不覆盖损坏计时等原始证据；
            // 独立计时文本仍单独传给分类器，后续两帧一致判定保持原样。
            if (string.IsNullOrWhiteSpace(result[index]) && names[index].Length >= 2)
                result[index] = names[index];
        }
        return result;
    }

    /// <summary>总览及详情共用的单帧计时入口，供实际截图回放验证同一生产路径。</summary>
    internal async Task<CountdownOcrReadout> ReadCountdownFromFrameAsync(CapturedFrame frame,
        NRect roi, bool labelledDetail) =>
        (await ReadCountdownRoisFromFrameAsync(frame, [roi], labelledDetail))[0];

    internal async Task<CountdownOcrReadout[]> ReadCountdownRoisFromFrameAsync(CapturedFrame frame,
        IReadOnlyList<NRect> rois, bool labelledDetail)
    {
        var enhancedFrames = new Dictionary<int, CapturedFrame>();
        var result = new CountdownOcrReadout[rois.Count];
        for (int index = 0; index < rois.Count; index++)
        {
            int? lastThreshold = null;
            double lastScale = 2;
            result[index] = await ReadCountdownCandidatesAsync(labelledDetail, async (threshold, scale) =>
            {
                lastThreshold = threshold; lastScale = scale;
                var source = frame;
                if (threshold is { } t)
                {
                    if (!enhancedFrames.TryGetValue(t, out source!))
                    {
                        source = EnhanceTimerContrast(frame, rois, t);
                        enhancedFrames.Add(t, source);
                    }
                }
                return (await _ocr.ReadAsync(source, rois[index], scale)).FullText;
            });
            if (labelledDetail)
            {
                result[index] = await ReadDetailDigitsFallbackAsync(result[index], async () =>
                {
                    // 实测制药详情的标签和时钟会把 08:00:47 连成「& 剩余时间：08℃0：47」。
                    // 保留原候选优先，只在全部失败后读同帧右侧数字区；不按 ℃/& 猜数字。
                    const int threshold = 80;
                    if (!enhancedFrames.TryGetValue(threshold, out var source))
                    {
                        source = EnhanceTimerContrast(frame, rois, threshold);
                        enhancedFrames.Add(threshold, source);
                    }
                    lastThreshold = threshold; lastScale = 1.5;
                    return (await _ocr.ReadAsync(source, DetailDigitsRoi(rois[index]), 1.5)).FullText;
                });
            }
            if (result[index].Remaining is not null &&
                (labelledDetail ? lastThreshold is not null : lastThreshold != 150))
                _log.Debug("计时区{Index}采用备用OCR(阈值{Threshold},{Scale:0.#}x):{Timer}",
                    index, lastThreshold, lastScale, Normalize(result[index].Text));
        }
        return result;
    }

    /// <summary>详情数字区相对既有标定 ROI 裁切，适用于不同分辨率，不引入屏幕绝对坐标。</summary>
    internal static NRect DetailDigitsRoi(NRect roi) => new()
    {
        X = roi.X + roi.W * (0.085 / 0.175),
        Y = roi.Y + roi.H * (0.007 / 0.062),
        W = roi.W * (0.09 / 0.175),
        H = roi.H * (0.04 / 0.062),
    };

    internal static async Task<CountdownOcrReadout> ReadDetailDigitsFallbackAsync(CountdownOcrReadout primary,
        Func<Task<string>> readDigits)
    {
        if (primary.Remaining is not null) return primary;
        string raw = await readDigits();
        return System.Text.RegularExpressions.Regex.IsMatch(Normalize(raw),
                @"^[0-9]{2}[:：．，][0-5][0-9][:：．，][0-5][0-9]$") &&
            TryParseCountdownReadout(raw, labelledDetail: true, out var remaining)
            ? new CountdownOcrReadout(raw, remaining) : primary;
    }

    /// <summary>真正消费者共用的有界候选流程；详情先保留原图读取，再用同一组增强方案。</summary>
    internal static async Task<CountdownOcrReadout> ReadCountdownCandidatesAsync(bool labelledDetail,
        Func<int?, double, Task<string>> readCandidate)
    {
        var variants = new List<(int? Threshold, double Scale)>();
        if (labelledDetail) variants.Add((null, 2));
        variants.Add((150, 2));
        variants.AddRange(TimerFallbacks.Select(v => ((int?)v.Threshold, v.Scale)));
        string primary = "";
        for (int index = 0; index < variants.Count; index++)
        {
            var (threshold, scale) = variants[index];
            string raw = await readCandidate(threshold, scale);
            if (index == 0) primary = raw;
            if (!TryParseCountdownReadout(raw, labelledDetail, out var remaining)) continue;
            // 总览备用仍只提升两位小时的完整数字，不把时钟图标噪声提升为长订单。
            if (!labelledDetail && index > 0 && !System.Text.RegularExpressions.Regex.IsMatch(
                    Normalize(raw), @"^[0-9]{2}[:：．，][0-5][0-9][:：．，][0-5][0-9]$")) continue;
            return new CountdownOcrReadout(raw, remaining);
        }
        return new CountdownOcrReadout(primary, null);
    }

    /// <summary>整字段验证后才调用真实解析器，拒绝截断、价格、坏字符和缺失数字。</summary>
    internal static bool TryParseCountdownReadout(string text, bool labelledDetail, out TimeSpan remaining)
    {
        remaining = default;
        string value = text.Trim();
        bool hasLabel = false;
        if (labelledDetail)
        {
            var label = System.Text.RegularExpressions.Regex.Match(value,
                @"^剩\s*余\s*时\s*间\s*[:：、]?\s*(?<value>.*?)\s*$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (label.Success) { hasLabel = true; value = label.Groups["value"].Value; }
        }
        string compact = Normalize(value);
        string hours = labelledDetail && !hasLabel ? "[0-9]{2}" : "[0-9]{1,3}";
        string separator = hasLabel ? @"[ \t:：、,.;·．，]+" : @"[:：．，]";
        var numeric = System.Text.RegularExpressions.Regex.Match(value,
            @"^\s*(?<h>" + hours + ")" + separator +
            @"(?<m>[0-5]?[0-9])" + separator + @"(?<s>[0-5]?[0-9])\s*$");
        if (!hasLabel)
            numeric = System.Text.RegularExpressions.Regex.Match(compact,
                @"^(?<h>" + hours + @")[:：．，](?<m>[0-5][0-9])[:：．，](?<s>[0-5][0-9])$");
        if (numeric.Success)
        {
            string canonical = numeric.Groups["h"].Value + ":" + numeric.Groups["m"].Value + ":" + numeric.Groups["s"].Value;
            return CountdownParser.TryParse(canonical, out remaining);
        }
        // 保留明确中文单位的旧格式；必须整个字段都是时长，不从杂文中找一小段。
        if (hasLabel && compact.Length > 0 &&
            System.Text.RegularExpressions.Regex.IsMatch(compact,
                @"^(?:[0-9]{1,3}天)?(?:[0-9]{1,3}(?:小时|时))?(?:[0-9]{1,3}分(?:钟)?)?(?:[0-9]{1,3}秒)?$") &&
            System.Text.RegularExpressions.Regex.IsMatch(compact, @"[天时分秒]"))
            return CountdownParser.TryParse(compact, out remaining);
        return false;
    }

    internal static bool CountdownReadingsConsistent(TimeSpan previous, long previousMs, TimeSpan current, long currentMs)
    {
        double drop = (previous - current).TotalSeconds;
        return drop >= -2 && drop <= (currentMs - previousMs) / 1000.0 + 8;
    }

    /// <summary>仅将完整、全数字且分秒合法的备用读数提升；保留主读数，不猜测缺失分隔或字符。</summary>
    internal static string SelectTimerReadout(string primary, string fallback) =>
        HasCompleteTimerReadout(primary) ||
        !System.Text.RegularExpressions.Regex.IsMatch(Normalize(fallback),
            @"^[0-9]{2}[:：．，][0-5][0-9][:：．，][0-5][0-9]$")
            ? primary : fallback;

    private static bool HasCompleteTimerReadout(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(Normalize(text),
            @"^[0-9]{1,3}[:：．，][0-5][0-9][:：．，][0-5][0-9]$");

    internal static CapturedFrame EnhanceTimerContrast(CapturedFrame frame, IReadOnlyList<NRect> timers,
        int threshold = 150)
    {
        var pixels = (byte[])frame.Bgra.Clone();
        foreach (var timer in timers)
        {
            var (left, top, width, height) = PixelMapper.ToPixelRect(timer, frame.Width, frame.Height);
            for (int row = top; row < top + height; row++)
            for (int column = left; column < left + width; column++)
            {
                int offset = (row * frame.Width + column) * 4;
                byte value = (byte)((pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3 >= threshold ? 255 : 0);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }
        return new CapturedFrame(frame.Width, frame.Height, pixels);
    }

    /// <summary>单帧多界面判定:返回第一个探针命中的界面名,均未命中返回 null。
    /// 一次截帧多次判定,避免逐界面截图造成的时间错位。</summary>
    public async Task<string?> WhichScreenAsync(nint hwnd, IReadOnlyList<string> screenNames)
    {
        var frame = Capture(hwnd);
        foreach (var name in screenNames)
        {
            if (await MatchScreenFromFrameAsync(frame, name))
                return name;
        }
        RecordKnownScene(null);
        return null;
    }

    /// <summary>是否处于指定界面:探针区域 OCR 文本包含约定关键字。</summary>
    public async Task<bool> IsOnAsync(nint hwnd, string screenName)
    {
        return await MatchScreenFromFrameAsync(Capture(hwnd), screenName);
    }

    public void ClickPoint(nint hwnd, string screenName, string pointName)
    {
        var rect = FocusedClientRect(hwnd);
        var p = Screen(screenName).Point(pointName);
        var (x, y) = PixelMapper.ToPixel(p, rect.Left, rect.Top, rect.Width, rect.Height);
        _log.Debug("点击 {Screen}.{Point} → 屏幕({X},{Y})", screenName, pointName, x, y);
        RequireFocusedTarget(() => _window.IsForeground(hwnd));
        InvalidateRestartSceneForInput();
        _input.ClickAt(x, y);
    }

    /// <summary>点击帧内像素坐标(OCR 定位到的行中心)。</summary>
    public void ClickFramePoint(nint hwnd, double frameX, double frameY) =>
        ClickFramePointCore(hwnd, frameX, frameY, reconnectConfirmation: false);

    private void ClickFramePointCore(nint hwnd, double frameX, double frameY, bool reconnectConfirmation)
    {
        var rect = FocusedClientRect(hwnd);
        RequireFocusedTarget(() => _window.IsForeground(hwnd));
        if (!reconnectConfirmation) InvalidateRestartSceneForInput();
        _input.ClickAt(rect.Left + (int)Math.Round(frameX), rect.Top + (int)Math.Round(frameY));
    }

    internal void ClickNormalizedPoint(nint hwnd, NPoint point)
    {
        var rect = FocusedClientRect(hwnd);
        var (x, y) = PixelMapper.ToPixel(point, rect.Left, rect.Top, rect.Width, rect.Height);
        RequireFocusedTarget(() => _window.IsForeground(hwnd));
        _log.Debug("点击固定安全点({X},{Y})。", x, y);
        InvalidateRestartSceneForInput();
        _input.ClickAt(x, y);
    }

    /// <summary>在区域中心滚动(负档向下翻列表)。</summary>
    public void ScrollRoi(nint hwnd, NRect roi, int notches)
    {
        var rect = FocusedClientRect(hwnd);
        var center = new NPoint { X = roi.X + roi.W / 2, Y = roi.Y + roi.H / 2 };
        var (x, y) = PixelMapper.ToPixel(center, rect.Left, rect.Top, rect.Width, rect.Height);
        RequireFocusedTarget(() => _window.IsForeground(hwnd));
        InvalidateRestartSceneForInput();
        _input.ScrollAt(x, y, notches);
    }

    /// <summary>在区域内按文本找行(TextMatch 规范形包含匹配,抗 0/O、1/I 同形误读)。
    /// 找不到返回 null,由调用方决定翻页或失败。</summary>
    public async Task<OcrLine?> FindLineAsync(nint hwnd, NRect area, string target)
    {
        var readout = await _ocr.ReadAsync(Capture(hwnd), area);
        return readout.Lines.FirstOrDefault(l => TextMatch.LineContains(l.Text, target));
    }

    /// <summary>读取区域内全部 OCR 行(配方目录扫描用)。</summary>
    public async Task<IReadOnlyList<OcrLine>> ReadAreaLinesAsync(nint hwnd, NRect area) =>
        (await _ocr.ReadAsync(Capture(hwnd), area)).Lines;

    /// <summary>保存整帧截图与全文 OCR 转储(失败现场/校准诊断)。返回(截图路径, OCR 文本)。</summary>
    public Task<(string PngPath, string OcrText)> DumpAsync(nint hwnd, string tag) =>
        DumpFrameAsync(Capture(hwnd), tag);

    /// <summary>用已确认的原始帧留存现场，后续最小化不会覆盖前面的识别失败原因。</summary>
    internal async Task<(string PngPath, string OcrText)> DumpFrameAsync(CapturedFrame frame, string tag)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string png = Path.Combine(_shotsDir, $"{stamp}-{tag}.png");
        await _capture.SavePngAsync(frame, png);
        var readout = await _ocr.ReadAsync(frame, roi: null, upscale: 1.0);
        await File.WriteAllTextAsync(Path.ChangeExtension(png, ".txt"), readout.FullText);
        _log.Information("已保存诊断截图:{Png}", png);
        return (png, readout.FullText);
    }

    /// <summary>OCR 文本归一化:去除空白(中文 OCR 常在词间插入空格)。</summary>
    public static string Normalize(string s) =>
        string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static string Compact(string s)
    {
        var one = Normalize(s);
        return one.Length <= 40 ? one : one[..40] + "…";
    }
}
