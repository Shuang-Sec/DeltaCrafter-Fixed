using System.Reflection;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class CountdownReadingIntegrationTests
{
    [Theory]
    [InlineData("剩余时间：05：59：50", 5, 59, 50)]
    [InlineData("剩 余 时 间 ： 05 ． 59 ． 50", 5, 59, 50)]
    [InlineData("05:59:50", 5, 59, 50)]
    [InlineData("剩余时间：102:00:00", 102, 0, 0)]
    [InlineData("剩余时间：2小时30分钟", 2, 30, 0)]
    public void Detail_candidate_reaches_real_parser_only_after_whole_field_validation(string raw, int h, int m, int s)
    {
        Assert.True(Parse(raw, true, out var time));
        Assert.Equal(new TimeSpan(h, m, s), time);
    }

    [Theory]
    [InlineData("剩余时间：05．595C")]
    [InlineData("剩余时间：05:59:60")]
    [InlineData("剩余时间：05:59:501")]
    [InlineData("剩余时间：05:59:50:12")]
    [InlineData("剩余时间：1059:59:50")]
    [InlineData("剩余时间：05:60:50")]
    [InlineData("剩余时间：05:59:50 01")]
    [InlineData("01 剩余时间：05:59:50")]
    [InlineData("剩余时间：05:59:50x")]
    [InlineData("剩余时间：055950")]
    [InlineData("剩余时间：0559．50")]
    [InlineData("05:59:60")]
    [InlineData("05:59:501")]
    [InlineData("105:59:59")]
    [InlineData("OB:53:49")]
    [InlineData("00:49:口7")]
    [InlineData("参考价格54,056 出售总价46,974 17,509")]
    public void Detail_candidate_rejects_damaged_or_extra_fields(string raw)
    {
        Assert.False(Parse(raw, true, out _));
    }

    [Theory]
    [InlineData("05:59:60")]
    [InlineData("05:59:501")]
    [InlineData("1059:59:50")]
    [InlineData("05:59:50:12")]
    [InlineData("1:05:59:50")]
    [InlineData("剩余时间：05:59:50 01")]
    [InlineData("剩余时间：1059:59:50")]
    public void Generic_parser_does_not_truncate_a_bad_numeric_field(string raw)
    {
        Assert.False(CountdownParser.TryParse(raw, out _));
    }

    [Fact]
    public void Independent_frames_must_decrease_at_a_plausible_rate()
    {
        Assert.True(ScreenProbe.CountdownReadingsConsistent(new TimeSpan(5, 59, 50), 1000,
            new TimeSpan(5, 59, 49), 2100));
        Assert.False(ScreenProbe.CountdownReadingsConsistent(TimeSpan.FromHours(1), 1000,
            TimeSpan.FromHours(6), 2100));
        Assert.False(ScreenProbe.CountdownReadingsConsistent(TimeSpan.FromHours(6), 1000,
            TimeSpan.FromHours(1), 2100));
    }

    [Fact]
    public async Task Detail_uses_the_same_bounded_candidate_pipeline_instead_of_plain_roi_only()
    {
        var calls = new List<(int?, double)>();
        var method = typeof(ScreenProbe).GetMethod("ReadCountdownCandidatesAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Func<int?, double, Task<string>> read = (threshold, scale) =>
        {
            calls.Add((threshold, scale));
            return Task.FromResult(threshold switch
            {
                null => "剩余时间：05．595C",
                150 => "剩余时间：055950",
                80 => "剩余时间：O5:59:5O",
                130 => "剩余时间：05：59：50",
                _ => throw new InvalidOperationException(),
            });
        };
        var task = (Task)method.Invoke(null, new object[] { true, read })!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(new TimeSpan(5, 59, 50), result.GetType().GetProperty("Remaining")!.GetValue(result));
        Assert.Equal(new (int?, double)[] { (null, 2), (150, 2), (80, 3), (130, 2) }, calls);
    }

    private static bool Parse(string raw, bool detail, out TimeSpan time)
    {
        var method = typeof(ScreenProbe).GetMethod("TryParseCountdownReadout", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object[] args = { raw, detail, TimeSpan.Zero };
        var ok = (bool)method.Invoke(null, args)!;
        time = (TimeSpan)args[2];
        return ok;
    }
}
