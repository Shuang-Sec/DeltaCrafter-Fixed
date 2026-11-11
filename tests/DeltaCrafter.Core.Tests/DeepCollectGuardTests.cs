using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>测试生产页领取事务的实际委托顺序，确保同位置的中止/生产/补齐按钮不被误点。</summary>
public class DeepCollectGuardTests
{
    [Theory]
    [InlineData("中止")]
    [InlineData("生产")]
    [InlineData("一键补齐")]
    [InlineData("领取 中止")]
    [InlineData("不可领取")]
    [InlineData("")]
    public async Task Non_collect_action_never_clicks_and_requests_diagnostics(string label)
    {
        var events = new List<string>();
        await Assert.ThrowsAsync<StepFailedException>(() => Invoke(
            () => { events.Add("screen"); return Task.FromResult(true); },
            () => { events.Add("label"); return Task.FromResult(label); },
            () => events.Add("click"),
            _ => { events.Add("dump"); return Task.FromResult(new StepFailedException("领取前检查", "诊断")); }));
        Assert.Equal(new[] { "screen", "label", "dump" }, events);
    }

    [Fact]
    public async Task Changed_screen_fails_before_reading_the_action_or_clicking()
    {
        int reads = 0, clicks = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => Invoke(() => Task.FromResult(false),
            () => { reads++; return Task.FromResult("领取"); }, () => clicks++,
            _ => Task.FromResult(new StepFailedException("领取前检查", "页面变化"))));
        Assert.Equal(0, reads); Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Confirmed_collect_action_clicks_exactly_once()
    {
        var events = new List<string>();
        await Invoke(() => { events.Add("screen"); return Task.FromResult(true); },
            () => { events.Add("label"); return Task.FromResult("领 取"); },
            () => events.Add("click"), _ => throw new InvalidOperationException("不得保存失败截图"));
        Assert.Equal(new[] { "screen", "label", "click" }, events);
    }

    [Fact]
    public async Task Cancellation_during_label_read_prevents_the_click()
    {
        using var cancellation = new CancellationTokenSource();
        int clicks = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Invoke(() => Task.FromResult(true),
            () => { cancellation.Cancel(); return Task.FromResult("领取"); }, () => clicks++,
            _ => throw new InvalidOperationException(), cancellation.Token));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Read_error_and_click_error_propagate_without_a_second_click()
    {
        int clicks = 0;
        await Assert.ThrowsAsync<IOException>(() => Invoke(() => Task.FromResult(true),
            () => throw new IOException("OCR失败"), () => clicks++, _ => throw new InvalidOperationException()));
        Assert.Equal(0, clicks);
        await Assert.ThrowsAsync<IOException>(() => Invoke(() => Task.FromResult(true),
            () => Task.FromResult("领取"), () => { clicks++; throw new IOException("点击失败"); },
            _ => throw new InvalidOperationException()));
        Assert.Equal(1, clicks);
    }

    private static async Task Invoke(Func<Task<bool>> screen, Func<Task<string>> label, Action click,
        Func<string, Task<StepFailedException>> failure, CancellationToken ct = default)
    {
        var method = typeof(CollectFlow).GetMethod("ClickCollectOnceAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await (Task)method.Invoke(null, [screen, label, new StateKeywords(), click, failure, ct])!;
    }
}
