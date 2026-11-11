using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>直接运行真实步骤执行器，验证 OCR 等待期间取消不会被成功读数覆盖。</summary>
public sealed class StepRunnerCancellationTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    public async Task Cancellation_during_an_awaited_verification_cannot_complete_the_step(int timeoutMs)
    {
        using var log = new LoggerConfiguration().CreateLogger();
        using var cancellation = new CancellationTokenSource();
        var runner = new StepRunner(null!, log);
        int actions = 0, verifications = 0;
        var step = new Step("取消中的OCR", () => actions++, async () =>
        {
            verifications++;
            await Task.Yield();
            cancellation.Cancel();
            return true;
        }, TimeSpan.FromMilliseconds(timeoutMs));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(0, step, cancellation.Token));
        Assert.Equal(1, actions);
        Assert.Equal(1, verifications);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    public async Task Cancellation_from_the_action_prevents_any_verification(int timeoutMs)
    {
        using var log = new LoggerConfiguration().CreateLogger();
        using var cancellation = new CancellationTokenSource();
        var runner = new StepRunner(null!, log);
        int actions = 0, verifications = 0;
        var step = new Step("动作后取消", () => { actions++; cancellation.Cancel(); },
            () => { verifications++; return Task.FromResult(true); }, TimeSpan.FromMilliseconds(timeoutMs));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(0, step, cancellation.Token));
        Assert.Equal(1, actions);
        Assert.Equal(0, verifications);
    }

    [Fact]
    public async Task An_already_cancelled_request_never_performs_the_action()
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var runner = new StepRunner(null!, log);
        int actions = 0, verifications = 0;
        var step = new Step("预取消", () => actions++,
            () => { verifications++; return Task.FromResult(true); }, TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(0, step, new CancellationToken(true)));
        Assert.Equal(0, actions);
        Assert.Equal(0, verifications);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    public async Task A_successful_verification_still_completes_without_retry(int timeoutMs)
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var runner = new StepRunner(null!, log);
        int actions = 0, verifications = 0;
        var step = new Step("正常OCR", () => actions++, async () =>
        {
            verifications++;
            await Task.Yield();
            return true;
        }, TimeSpan.FromMilliseconds(timeoutMs));

        await runner.RunAsync(0, step, CancellationToken.None);
        Assert.Equal(1, actions);
        Assert.Equal(1, verifications);
    }
}
