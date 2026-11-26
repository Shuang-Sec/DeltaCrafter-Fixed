using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class WeGameTrayWakeTests
{
    private static readonly GameWindowInfo HiddenShell = new(101, "WeGame", "TWINCONTROL", 10);
    private static readonly GameWindowInfo VisibleBrowser = new(202, "WeGame", "CefTopWindow", 20);

    [Fact]
    public async Task Hidden_wegame_window_triggers_single_instance_wake_and_returns_refound_browser()
    {
        int wakes = 0, delays = 0;
        var resolved = await LaunchFlow.EnsureWeGameFocusOrWakeFromTrayAsync(
            HiddenShell,
            tryForeground: _ => false,
            isVisible: _ => false,
            wakeLauncher: () => wakes++,
            refindLauncher: () => VisibleBrowser,
            delay: (_, _) => { delays++; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(VisibleBrowser, resolved);
        Assert.Equal(1, wakes);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Visible_wegame_window_that_fails_foreground_does_not_relaunch()
    {
        int wakes = 0;
        var resolved = await LaunchFlow.EnsureWeGameFocusOrWakeFromTrayAsync(
            VisibleBrowser,
            tryForeground: _ => false,
            isVisible: _ => true,
            wakeLauncher: () => wakes++,
            refindLauncher: () => throw new InvalidOperationException("可见窗不应触发托盘唤醒重查"),
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(VisibleBrowser, resolved);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task Already_foreground_wegame_window_returns_immediately_without_waking()
    {
        int wakes = 0;
        var resolved = await LaunchFlow.EnsureWeGameFocusOrWakeFromTrayAsync(
            VisibleBrowser,
            tryForeground: _ => true,
            isVisible: _ => false,
            wakeLauncher: () => wakes++,
            refindLauncher: () => null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(VisibleBrowser, resolved);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task Cancellation_prevents_waking_hidden_wegame_window()
    {
        int wakes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LaunchFlow.EnsureWeGameFocusOrWakeFromTrayAsync(
                HiddenShell,
                tryForeground: _ => false,
                isVisible: _ => false,
                wakeLauncher: () => wakes++,
                refindLauncher: () => VisibleBrowser,
                delay: (_, ct) => Task.Delay(100, ct),
                new CancellationToken(canceled: true)));
        Assert.Equal(0, wakes);
    }
}
