using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>覆盖真实候选入口到槽位分类器之间的边界，防止失败读数降格为可领取。</summary>
public class DeepCountdownPipelineTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task Exhausted_candidates_return_the_primary_without_inventing_a_duration(bool detail, int expectedCalls)
    {
        int calls = 0;
        var reading = await ScreenProbe.ReadCountdownCandidatesAsync(detail, (_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? "原始损坏读数" : "055950");
        });

        Assert.Equal(expectedCalls, calls);
        Assert.Equal("原始损坏读数", reading.Text);
        Assert.Null(reading.Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_primary_short_circuits_fallbacks(bool detail)
    {
        int calls = 0;
        var reading = await ScreenProbe.ReadCountdownCandidatesAsync(detail, (_, _) =>
        {
            Assert.Equal(1, ++calls);
            return Task.FromResult("05:59:50");
        });
        Assert.Equal(new TimeSpan(5, 59, 50), reading.Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_cancellation_propagates_without_reading_more_variants(bool detail)
    {
        int calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ScreenProbe.ReadCountdownCandidatesAsync(detail, (_, _) =>
            {
                calls++;
                throw new OperationCanceledException();
            }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("０５:５９:５０")]
    [InlineData("٠٥:٥٩:٥٠")]
    [InlineData("剩余时间 ０５ ５９ ５０")]
    [InlineData("５小时")]
    [InlineData("５:１:２")]
    public void Non_ascii_digit_input_fails_cleanly_instead_of_throwing(string raw)
    {
        // 严格消费者只认可 ASCII 数字；公共 TryParse 同样应返回 false，而不是因 \d 范围更宽抛异常。
        Assert.False(CountdownParser.TryParse(raw, out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Theory]
    [InlineData("05.59.50")]
    [InlineData("05,59,50")]
    [InlineData("05;59;50")]
    [InlineData("05·59·50")]
    public async Task Unreadable_numeric_timer_does_not_become_a_collectible_item(string timer)
    {
        var reading = await ScreenProbe.ReadCountdownCandidatesAsync(false, (_, _) => Task.FromResult(timer));
        Assert.Null(reading.Remaining);
        var observation = Classify(ScreenProbe.Normalize(reading.Text) + "\n精英防弹背心");
        Assert.Equal(FacilityPhase.Unknown, observation.Phase);
        Assert.Null(observation.Remaining);
    }

    [Theory]
    [InlineData("０５:５９:５０")]
    [InlineData("٠٥:٥٩:٥٠")]
    public void Unicode_timer_noise_cannot_crash_slot_classification(string timer)
    {
        var observation = Classify(timer + "\n精英防弹背心");
        Assert.Equal(FacilityPhase.Unknown, observation.Phase);
        Assert.Null(observation.Remaining);
    }

    [Theory]
    [InlineData("乁 11 ． 57 ． 25")]
    [InlineData("08．57 37")]
    [InlineData("（9 011919")]
    public void Timer_noise_does_not_replace_the_real_item_name(string noise)
    {
        var observation = Classify("08:00:47\n体能强化剂\n" + noise);
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal("体能强化剂", observation.ItemName);
    }

    [Theory]
    [InlineData("5．45×39mm BT")]
    [InlineData("7．62×39mm AP")]
    public void Fullwidth_decimal_in_real_ammunition_name_is_not_a_damaged_timer(string item)
    {
        var completed = Classify(item);
        Assert.Equal(FacilityPhase.ReadyToCollect, completed.Phase);
        Assert.Equal(item, completed.ItemName);
        Assert.Null(completed.Remaining);

        var crafting = Classify("05:59:50\n" + item);
        Assert.Equal(FacilityPhase.Crafting, crafting.Phase);
        Assert.Equal(item, crafting.ItemName);
        Assert.Equal(new TimeSpan(5, 59, 50), crafting.Remaining);
    }

    [Theory]
    [InlineData("09．75．35")]
    [InlineData("90145．48")]
    [InlineData("05．59．60")]
    [InlineData("04．29．500")]
    public void Invalid_fullwidth_timer_still_stays_unknown_when_a_real_ammunition_name_is_present(string timer)
    {
        var observation = Classify(timer + "\n5．45×39mm BT");
        Assert.Equal(FacilityPhase.Unknown, observation.Phase);
        Assert.Null(observation.Remaining);
    }

    [Theory]
    [InlineData("08 ． 00 ， 47", true)]
    [InlineData("08℃0：47", false)]
    [InlineData("& 08:00:47", false)]
    [InlineData("080047", false)]
    [InlineData("908:00:47", false)]
    public async Task Narrow_detail_candidate_still_requires_a_complete_two_hour_digit_field(string raw, bool expected)
    {
        var method = typeof(ScreenProbe).GetMethod("ReadDetailDigitsFallbackAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var primary = new CountdownOcrReadout("& 剩余时间：08℃0：47", null);
        int calls = 0;
        Func<Task<string>> read = () => { calls++; return Task.FromResult(raw); };
        var result = await (Task<CountdownOcrReadout>)method.Invoke(null, [primary, read])!;
        Assert.Equal(1, calls);
        Assert.Equal(expected, result.Remaining is not null);
        if (expected) Assert.Equal(new TimeSpan(8, 0, 47), result.Remaining);
        else Assert.Same(primary, result);
    }

    [Fact]
    public async Task Narrow_detail_candidate_never_overwrites_an_already_valid_primary()
    {
        var method = typeof(ScreenProbe).GetMethod("ReadDetailDigitsFallbackAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var primary = new CountdownOcrReadout("05:59:50", new TimeSpan(5, 59, 50));
        Func<Task<string>> read = () => throw new InvalidOperationException("有效主读数不得执行备用OCR");
        var result = await (Task<CountdownOcrReadout>)method.Invoke(null, [primary, read])!;
        Assert.Same(primary, result);
    }

    [Theory]
    [InlineData(800, true)]
    [InlineData(10_000, false)]
    public void Timer_consistency_bounds_the_drop_by_capture_elapsed_time(long dropMs, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.CountdownReadingsConsistent(
            TimeSpan.FromMinutes(5), 10_000, TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(dropMs), 11_000));
    }

    private static FacilityObservation Classify(string raw)
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var anchors = new AnchorTable();
        var probe = new ScreenProbe(null!, null!, null!, null!, () => anchors, "", log);
        var flow = new CollectFlow(probe, null!, null!, null!, log);
        var method = typeof(CollectFlow).GetMethod("Classify", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (FacilityObservation)method.Invoke(flow, [raw])!;
    }
}
