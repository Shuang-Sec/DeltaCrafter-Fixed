using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class DeepOcrRescueTests
{
    [Fact]
    public async Task Overview_recovers_exact_compacted_six_digit_timer_from_raw_frame_before_binarized_fallbacks()
    {
        var calls = new List<(int?, double)>();
        var reading = await ScreenProbe.ReadCountdownCandidatesAsync(false, (threshold, scale) =>
        {
            calls.Add((threshold, scale));
            return Task.FromResult(threshold switch
            {
                150 => "01：230",
                80 => "",
                130 => "01：2&30", // 阈值130误把8读成&
                null => "0128：30", // 原图2x读出完整六位纯数字
                _ => throw new InvalidOperationException(),
            });
        });

        Assert.Equal(new TimeSpan(1, 28, 30), reading.Remaining);
        Assert.Equal("0128：30", reading.Text);
        Assert.Equal(new (int?, double)[] { (150, 2), (80, 3), (130, 2), (null, 2) }, calls);
    }

    [Fact]
    public async Task Overview_recovers_exact_compacted_six_digit_timer_from_threshold_130_when_raw_is_empty()
    {
        var reading = await ScreenProbe.ReadCountdownCandidatesAsync(false, (threshold, _) =>
            Task.FromResult(threshold switch
            {
                150 => "0a21：1",
                80 => "OO：21：1",
                130 => "00：2117",
                null => "",
                _ => throw new InvalidOperationException(),
            }));

        Assert.Equal(new TimeSpan(0, 21, 17), reading.Remaining);
        Assert.Equal("00：2117", reading.Text);
    }

    [Theory]
    [InlineData("0128：30", true, 1, 28, 30)]
    [InlineData("0158．26", true, 1, 58, 26)]
    [InlineData("00：2117", true, 0, 21, 17)]
    [InlineData("01：2&30", false, 0, 0, 0)]
    [InlineData("012830", false, 0, 0, 0)]
    [InlineData("01：6030", false, 0, 0, 0)]
    [InlineData("0128：60", false, 0, 0, 0)]
    [InlineData("90128：30", false, 0, 0, 0)]
    [InlineData("0128：30-", false, 0, 0, 0)]
    public void Exact_compacted_overview_timer_parser_accepts_only_strict_six_ascii_digits(
        string raw, bool expected, int h, int m, int s)
    {
        bool ok = ScreenProbe.TryParseExactCompactedOverviewTimer(raw, out var remaining);
        Assert.Equal(expected, ok);
        if (expected) Assert.Equal(new TimeSpan(h, m, s), remaining);
    }

    [Theory]
    [InlineData("僉 FS 复 合 防 弹 衣", "FS复合防弹衣", true)]
    [InlineData("僉 重 型 突 击 背 心", "重型突击背心", true)]
    [InlineData("僉 4 ． 6x30mm AP SX * 150", "4.6×30mm AP SX", true)]
    [InlineData("※ 5 ． 45x39mm BT * 180", "5.45×39mm BT", true)]
    [InlineData("※ XCOG 突 击 3 ． 5 倍 瞄 准 镜", "XCOG突击3.5倍瞄准镜", true)]
    [InlineData("※ .300 BLK TAC-TX * 150", ".300 BLK TAC-TX", true)]
    [InlineData("僉 5 ． 45x39mm BT * 180", "5.45×39mm BS", false)]
    public void Quality_icon_noise_prefix_is_stripped_without_relaxing_model_identity(
        string observed, string expected, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, TextMatch.MatchesRecipeName(observed, expected));
    }

    [Theory]
    [InlineData("僉 FS 复 合 防 弹 衣", "FS复合防弹衣")]
    [InlineData("※ 5 ． 45 × 39 mm BT", "5.45×39mm BT")]
    public void CleanSlotName_strips_leading_quality_icon_glyphs(string raw, string expected)
    {
        Assert.Equal(expected, ScreenProbe.CleanSlotName(raw));
    }
}
