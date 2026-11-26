using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NavigationAndFlowHardeningTests
{
    [Fact]
    public void Word_level_specops_selector_finds_exact_tab_center_inside_merged_top_bar_line()
    {
        IReadOnlyList<IReadOnlyList<OcrWordBox>> lines =
        [
            [
                new("交易行", 860, 55, 980, 90),
                new("特", 1070, 55, 1100, 90),
                new("勤", 1105, 55, 1135, 90),
                new("处", 1140, 55, 1170, 90),
                new("改枪台", 1280, 55, 1400, 90),
            ],
        ];
        var hit = ScreenProbe.SelectSpecOpsEntryFromWords(lines);
        Assert.NotNull(hit);
        Assert.Equal("特勤处", hit!.Text);
        Assert.Equal(1120.0, hit.CenterX, precision: 2);
        Assert.Equal(72.5, hit.CenterY, precision: 2);
    }

    [Fact]
    public void Word_level_specops_selector_rejects_enter_button_and_ambiguous_duplicates()
    {
        IReadOnlyList<IReadOnlyList<OcrWordBox>> enterOnly =
        [
            [new("进入", 100, 1360, 150, 1400), new("特勤处", 155, 1360, 240, 1400)],
        ];
        Assert.Null(ScreenProbe.SelectSpecOpsEntryFromWords(enterOnly));

        IReadOnlyList<IReadOnlyList<OcrWordBox>> duplicated =
        [
            [new("特勤处", 900, 55, 980, 90), new("特勤处", 1100, 55, 1180, 90)],
        ];
        Assert.Null(ScreenProbe.SelectSpecOpsEntryFromWords(duplicated));
    }

    [Fact]
    public async Task Specops_nav_retries_transient_fade_in_and_succeeds_without_fixed_coordinates()
    {
        int clicks = 0, delays = 0;
        bool entered = await SpecOpsNavFlow.TryEnterWithRetryAsync(
            () => Task.FromResult(false),
            () => Task.FromResult(++clicks == 3),
            (_, _) => { delays++; return Task.CompletedTask; },
            CancellationToken.None);
        Assert.True(entered);
        Assert.Equal(3, clicks);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Specops_nav_stops_without_clicking_when_already_on_specops_home()
    {
        int clicks = 0;
        bool entered = await SpecOpsNavFlow.TryEnterWithRetryAsync(
            () => Task.FromResult(true),
            () => { clicks++; return Task.FromResult(true); },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.False(entered);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task Specops_nav_throws_after_three_misses_without_falling_back_to_fixed_coordinates()
    {
        int clicks = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => SpecOpsNavFlow.TryEnterWithRetryAsync(
            () => Task.FromResult(false),
            () => { clicks++; return Task.FromResult(false); },
            (_, _) => Task.CompletedTask,
            CancellationToken.None));
        Assert.Equal(3, clicks);
    }

    [Theory]
    [InlineData("一键补齐", true)]
    [InlineData("一补齐", true)]
    [InlineData("一卜齐", true)]
    [InlineData("一健补齐", true)]
    [InlineData("生产", false)]
    [InlineData("中止", false)]
    [InlineData("中止一卜齐", false)]
    [InlineData("", false)]
    public void Replenish_button_detector_accepts_verified_ocr_variants_only(string label, bool expected)
    {
        Assert.Equal(expected, CraftStartFlow.IsReplenishLabel(label, ["一键补齐", "补齐"]));
    }

    [Theory]
    [InlineData("一补齐", true)]
    [InlineData("一卜齐", true)]
    [InlineData("一健补齐", true)]
    [InlineData("生产", true)]
    [InlineData("中止", false)]
    public void Abort_idle_confirmation_accepts_verified_replenish_ocr_variants(string label, bool expected)
    {
        Assert.Equal(expected, AbortFlow.IsConfirmedIdleLabel(label));
    }

    [Theory]
    [InlineData("厶 一 补 齐 清 单\n高级燃料 * 2 前 去 兑 换", true)]
    [InlineData("一键补齐清单 交易行 强力胶*2", false)]
    [InlineData("前去兑换", false)]
    public void Exchange_required_replenish_popup_is_detected_precisely(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsExchangeRequiredReplenishText(raw));
    }

    [Theory]
    [InlineData("剩余时尚：07：59：48", 7, 59, 48)]
    [InlineData("剩 余 时 尚 ． 07 ： 59 ： 48", 7, 59, 48)]
    public void Detail_countdown_handles_observed_label_misread_with_strict_hms_validation(
        string raw, int hours, int minutes, int seconds)
    {
        Assert.True(ScreenProbe.TryParseCountdownReadout(raw, labelledDetail: true, out var remaining));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), remaining);
        Assert.True(CountdownParser.TryParse(raw, out var parsed));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), parsed);
    }

    [Theory]
    [InlineData("剩余时尚：07：60：48")]
    [InlineData("剩余时尚：07：59：480")]
    public void Detail_countdown_label_misread_still_rejects_invalid_numbers(string raw)
    {
        Assert.False(ScreenProbe.TryParseCountdownReadout(raw, labelledDetail: true, out _));
    }
}
