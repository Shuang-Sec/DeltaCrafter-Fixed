using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>
/// 计时区归一化的真实语料来自 2026-10-28 至 2026-11-10 的运行日志
/// （[计时区:...] 与整卡原文）。规则只服务独立计时 ROI 这一行，
/// 不改变 Classify/TryParseCountdownReadout 的严格契约。
/// </summary>
public class SlotTimerCanonicalizerTests
{
    [Theory]
    [InlineData("0J58：09", "0:58:09")]
    [InlineData("0J58：01", "0:58:01")]
    [InlineData("0J32：49", "0:32:49")]
    [InlineData("07·59：28", "7:59:28")]
    [InlineData("07．57·40", "7:57:40")]
    [InlineData("02·47·08", "2:47:08")]
    [InlineData("OO：5&OO", "0:57:00")]
    [InlineData("OO:5&O2", "0:57:02")]
    [InlineData("00：58℃9", "0:58:09")]
    [InlineData("06℃3：37-", "6:03:37")]
    [InlineData("0058：09", "0:58:09")]
    [InlineData("00：57：50", "0:57:50")]
    [InlineData(" 00 : 57 : 50 ", "0:57:50")]
    public void Observed_slot_timer_noise_folds_into_a_complete_countdown(string raw, string expected)
    {
        Assert.True(SlotTimerCanonicalizer.TryCanonicalize(raw, out string canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("07：511")]
    [InlineData("01：227")]
    [InlineData("021024")]
    [InlineData("902:10:24")]
    [InlineData("0J58：09：01")]
    [InlineData("00：6l：09")]
    [InlineData("45,781")]
    [InlineData("125,230")]
    [InlineData("10,831K")]
    [InlineData("1,290+")]
    [InlineData("4．6x30mmAPSX")]
    [InlineData("300BLK")]
    [InlineData("重型突击背心")]
    [InlineData("已领取")]
    public void Non_countdown_text_stays_unparsed(string raw)
    {
        Assert.False(SlotTimerCanonicalizer.TryCanonicalize(raw, out _));
    }

    [Theory]
    [InlineData("00:60:09")]
    [InlineData("00:09:60")]
    [InlineData("99:59:59")]
    public void Field_range_is_enforced(string raw)
    {
        bool ok = SlotTimerCanonicalizer.TryCanonicalize(raw, out _);
        Assert.Equal(raw is "99:59:59", ok);
    }
}
