using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>驱动生产启动循环，仅替换窗口、时钟和输入边界，不接触真实游戏。</summary>
public sealed class LauncherWindowHandoffTests
{
    private static readonly GameWindowInfo Shell = new(11, "WeGame", "TWINCONTROL", 101);
    private static readonly GameWindowInfo Browser = new(22, "WeGame", "CefTopWindow", 202);
    private static readonly GameWindowInfo Game = new(33, "三角洲行动", "UnrealWindow", 303);

    [Fact]
    public async Task Login_shell_can_disappear_before_browser_and_client_arrive()
    {
        var run = new Scenario();
        run.Launcher = () => run.Elapsed < 3000 ? Shell : run.Elapsed < 6000 ? null : Browser;
        run.Alive = hwnd => hwnd != Shell.Hwnd || run.Elapsed < 3000;
        run.Client = () => run.Elapsed >= 12000 ? Game : null;

        Assert.Equal(Game, await run.RunAsync());
        Assert.Equal(0, run.Launches);
        Assert.Contains(run.Attempts, entry => entry.Window == Browser.Hwnd);
    }

    [Fact]
    public async Task Browser_is_reacquired_even_while_login_shell_still_exists()
    {
        var run = new Scenario();
        bool clickedBrowser = false;
        run.Launcher = () => run.Elapsed < 3000 ? Shell : Browser;
        run.Client = () => clickedBrowser ? Game : null;
        run.Start = (window, _, _) =>
        {
            clickedBrowser = window.Hwnd == Browser.Hwnd;
            return Task.FromResult(clickedBrowser);
        };

        Assert.Equal(Game, await run.RunAsync());
        Assert.Equal(0, run.Launches);
        Assert.Contains(run.Attempts, entry => entry.Window == Browser.Hwnd);
    }

    [Fact]
    public async Task Cold_start_launches_platform_only_once_across_window_gap()
    {
        var run = new Scenario();
        run.Launcher = () => run.Elapsed < 2000 ? null : run.Elapsed < 5000 ? Shell
            : run.Elapsed < 8000 ? null : Browser;
        run.Alive = hwnd => hwnd != Shell.Hwnd || run.Elapsed < 5000;
        run.Client = () => run.Elapsed >= 14000 ? Game : null;

        Assert.Equal(Game, await run.RunAsync());
        Assert.Equal(1, run.Launches);
    }

    [Fact]
    public async Task No_successor_waits_until_original_timeout_without_relaunching()
    {
        var run = new Scenario { TimeoutSeconds = 12 };
        run.Launcher = () => run.Elapsed < 3000 ? Shell : null;
        run.Alive = _ => run.Elapsed < 3000;

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Contains("12s", failure.Message);
        Assert.Equal(12000, run.Elapsed);
        Assert.Equal(0, run.Launches);
    }

    [Fact]
    public async Task Initial_platform_wait_and_client_wait_share_one_deadline()
    {
        var run = new Scenario { TimeoutSeconds = 12 };
        run.Launcher = () => run.Elapsed < 6000 ? null : Browser;

        await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Equal(12000, run.Elapsed);
        Assert.Equal(1, run.Launches);
    }

    [Fact]
    public async Task Cancellation_before_start_sends_no_launch_or_click()
    {
        var run = new Scenario();
        run.Launcher = () => null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.RunAsync(new CancellationToken(true)));
        Assert.Equal(0, run.Launches);
        Assert.Empty(run.Attempts);
    }

    [Fact]
    public async Task Cancellation_during_window_gap_is_propagated()
    {
        var run = new Scenario();
        using var stop = new CancellationTokenSource();
        run.Launcher = () => run.Elapsed < 3000 ? Shell : null;
        run.Alive = _ => run.Elapsed < 3000;
        run.AfterDelay = () => { if (run.Elapsed >= 6000) stop.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.RunAsync(stop.Token));
        Assert.Equal(6000, run.Elapsed);
        Assert.Equal(0, run.Launches);
    }

    [Fact]
    public async Task Existing_client_returns_before_finding_or_restoring_platform()
    {
        var run = new Scenario();
        run.Client = () => Game;
        run.Launcher = () => throw new InvalidOperationException("不应还原平台窗口。");

        Assert.Equal(Game, await run.RunAsync());
        Assert.Equal(0, run.Launches);
        Assert.Empty(run.Attempts);
    }

    [Fact]
    public async Task First_click_is_immediate_and_window_changes_do_not_reset_two_click_budget()
    {
        var run = new Scenario { InitialTime = 0, TimeoutSeconds = 80 };
        run.Launcher = () => new GameWindowInfo((nint)(100 + run.Elapsed / 3000), "WeGame", "CefTopWindow", 202);
        run.Start = (_, _, _) => Task.FromResult(true);

        await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Equal(2, run.Attempts.Count);
        Assert.Equal(0, run.Attempts[0].At);
        Assert.True(run.Attempts[1].At - run.Attempts[0].At >= 30000);
        Assert.NotEqual(run.Attempts[0].Window, run.Attempts[1].Window);
        Assert.Equal(0, run.Launches);
    }

    [Fact]
    public async Task Unrecognized_login_page_never_consumes_click_budget()
    {
        var run = new Scenario { TimeoutSeconds = 12 };
        run.Start = (_, _, _) => Task.FromResult(false);

        await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.True(run.Attempts.Count >= 3);
        Assert.Equal(0, run.Launches);
    }

    [Fact]
    public async Task Independent_launcher_preserves_disappeared_window_failure()
    {
        var run = new Scenario { WeGame = false };
        run.Alive = _ => false;

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Contains("启动器窗口已消失", failure.Message);
        Assert.Equal(0, run.Elapsed);
    }

    [Fact]
    public async Task Window_destroyed_during_ocr_is_reacquired_instead_of_failing()
    {
        var run = new Scenario();
        bool shellGone = false;
        bool browserClicked = false;
        run.Launcher = () => shellGone ? Browser : Shell;
        run.Alive = hwnd => hwnd != Shell.Hwnd || !shellGone;
        run.Client = () => browserClicked ? Game : null;
        run.Start = (window, _, _) =>
        {
            if (window.Hwnd == Shell.Hwnd)
            {
                shellGone = true;
                throw new StepFailedException("读取游戏画面", "窗口已销毁。");
            }
            browserClicked = true;
            return Task.FromResult(true);
        };

        Assert.Equal(Game, await run.RunAsync());
        Assert.Equal(0, run.Launches);
    }

    [Fact]
    public async Task Real_foreground_failure_is_not_hidden_as_window_handoff()
    {
        var run = new Scenario();
        run.Start = (_, _, _) => throw new StepFailedException("窗口前台化", "前台失败。");

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Contains("前台失败", failure.Message);
        Assert.Equal(0, run.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_crossed_during_ocr_or_final_focus_sends_no_late_start_click(bool expireDuringFocus)
    {
        var run = new Scenario { TimeoutSeconds = 12 };
        int clicks = 0;
        int focuses = 0;
        run.Start = (_, canStart, token) => LaunchFlow.ReadAndClickLauncherStartAsync(
            _ =>
            {
                if (!expireDuringFocus) run.Elapsed = 13000;
                return Task.FromResult<OcrLine?>(new OcrLine("启动", 1380, 850));
            },
            () => true,
            () =>
            {
                if (expireDuringFocus && ++focuses == 2) run.Elapsed = 13000;
                return true;
            },
            _ => clicks++, token, canStart);

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => run.RunAsync());
        Assert.Contains("12s", failure.Message);
        Assert.Equal(0, clicks);
        Assert.Equal(0, run.Launches);
        Assert.Single(run.Attempts);
    }

    private sealed class Scenario
    {
        internal long InitialTime = 100000;
        internal long Elapsed;
        internal int TimeoutSeconds = 30;
        internal bool WeGame = true;
        internal int Launches;
        internal Func<GameWindowInfo?> Launcher = () => Shell;
        internal Func<GameWindowInfo?> Client = () => null;
        internal Func<nint, bool> Alive = _ => true;
        internal Func<GameWindowInfo, Func<bool>, CancellationToken, Task<bool>> Start = (_, _, _) => Task.FromResult(false);
        internal Action? AfterDelay;
        internal readonly List<(nint Window, long At)> Attempts = [];

        internal Task<GameWindowInfo> RunAsync(CancellationToken ct = default)
        {
            var runtime = new LaunchFlow.LauncherRuntime(
                () => Launcher(), () => Client(), hwnd => Alive(hwnd), () => Launches++,
                (window, canStart, token) =>
                {
                    Attempts.Add((window.Hwnd, Elapsed));
                    return Start(window, canStart, token);
                },
                _ => Task.FromResult<string?>(null),
                () => InitialTime + Elapsed,
                (milliseconds, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Elapsed += milliseconds;
                    AfterDelay?.Invoke();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
            return LaunchFlow.WaitForLauncherClientAsync(new AppSettings
            {
                LaunchTimeoutSeconds = TimeoutSeconds,
                GamePath = WeGame ? @"F:\game\WeGame\wegame.exe" : @"F:\game\launcher.exe"
            }, WeGame, runtime, new LoggerConfiguration().CreateLogger(), ct);
        }
    }
}
