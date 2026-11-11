using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class LauncherStartTransactionTests
{
    private static readonly OcrLine Button = new("启动", 1380, 850);

    [Fact]
    public async Task Window_replaced_during_ocr_discards_old_frame_coordinates()
    {
        bool current = true;
        int clicks = 0;
        bool result = await LaunchFlow.ReadAndClickLauncherStartAsync(_ =>
        {
            current = false;
            return Task.FromResult<OcrLine?>(Button);
        }, () => current, () => true, _ => clicks++, default);

        Assert.False(result);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Window_changed_while_restoring_foreground_discards_old_button()
    {
        bool current = true;
        int focuses = 0;
        int clicks = 0;
        bool result = await LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => Task.FromResult<OcrLine?>(Button), () => current,
            () => { if (++focuses == 2) current = false; return true; },
            _ => clicks++, default);

        Assert.False(result);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Capture_failure_caused_by_known_window_handoff_only_retries()
    {
        bool current = true;
        int clicks = 0;
        bool result = await LaunchFlow.ReadAndClickLauncherStartAsync(_ =>
        {
            current = false;
            throw new StepFailedException("读取游戏画面", "旧窗口已切换。");
        }, () => current, () => true, _ => clicks++, default);

        Assert.False(result);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Stable_window_focus_failure_still_stops_without_reading_or_clicking()
    {
        int reads = 0;
        int clicks = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => { reads++; return Task.FromResult<OcrLine?>(Button); },
            () => true, () => false, _ => clicks++, default));
        Assert.Equal(0, reads);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Cancellation_after_ocr_sends_no_click()
    {
        using var stop = new CancellationTokenSource();
        int clicks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => { stop.Cancel(); return Task.FromResult<OcrLine?>(Button); },
            () => true, () => true, _ => clicks++, stop.Token));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Cancellation_during_final_window_check_sends_no_click()
    {
        using var stop = new CancellationTokenSource();
        int checks = 0;
        int clicks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => Task.FromResult<OcrLine?>(Button), () =>
            {
                if (++checks >= 2) stop.Cancel();
                return true;
            }, () => true, _ => clicks++, stop.Token));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Same_window_uses_the_recognized_button_once()
    {
        var clicks = new List<OcrLine>();
        Assert.True(await LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => Task.FromResult<OcrLine?>(Button), () => true, () => true, clicks.Add, default));
        Assert.Equal(new[] { Button }, clicks);
    }

    [Fact]
    public async Task Unrecognized_page_sends_no_click()
    {
        int clicks = 0;
        Assert.False(await LaunchFlow.ReadAndClickLauncherStartAsync(
            _ => Task.FromResult<OcrLine?>(null), () => true, () => true, _ => clicks++, default));
        Assert.Equal(0, clicks);
    }
}
