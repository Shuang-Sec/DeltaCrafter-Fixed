using System.Reflection;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>覆盖打开补齐页的异步 OCR 成功返回与停止请求之间的购买事务边界。</summary>
public class DeepPurchaseCancellationTests
{
    [Fact]
    public async Task Cancellation_during_successful_popup_verification_does_not_buy()
    {
        using var cancellation = new CancellationTokenSource();
        int clicks = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Buy(
            () => { cancellation.Cancel(); return Task.CompletedTask; }, () => clicks++, cancellation.Token));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Cancellation_before_opening_does_not_open_or_buy()
    {
        int opens = 0, clicks = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Buy(
            () => { opens++; return Task.CompletedTask; }, () => clicks++, new CancellationToken(true)));
        Assert.Equal(0, opens); Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Successful_popup_is_followed_by_one_purchase_in_order()
    {
        var events = new List<string>();
        await Buy(() => { events.Add("open"); return Task.CompletedTask; }, () => events.Add("buy"));
        Assert.Equal(new[] { "open", "buy" }, events);
    }

    [Fact]
    public async Task Opening_failure_and_purchase_failure_never_repeat_the_purchase()
    {
        int clicks = 0;
        await Assert.ThrowsAsync<IOException>(() => Buy(() => throw new IOException("补齐页读取失败"), () => clicks++));
        Assert.Equal(0, clicks);
        await Assert.ThrowsAsync<IOException>(() => Buy(() => Task.CompletedTask,
            () => { clicks++; throw new IOException("购买点击失败"); }));
        Assert.Equal(1, clicks);
    }

    private static async Task Buy(Func<Task> open, Action click, CancellationToken ct = default)
    {
        var method = typeof(CraftStartFlow).GetMethod("BuyMaterialsOnceAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method.Invoke(null, [open, click, ct])!;
    }
}
