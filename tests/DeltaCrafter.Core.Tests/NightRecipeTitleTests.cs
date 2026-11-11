using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NightRecipeTitleTests
{
    [Theory]
    [InlineData("6x30mmAPSX*150")]
    [InlineData("-4 ， 6x30mm AP SX * 150")]
    [InlineData("04 ， 6 × 30mm AP SX * 150")]
    public async Task Complete_name_from_an_independent_crop_recovers_real_AP_SX_title(string primary)
    {
        int reads = 0;
        var result = await ScreenProbe.ReadRecipeTitleCandidatesAsync("4.6×30mm AP SX", (narrow, scale) =>
        { reads++; return Task.FromResult(narrow ? "4 ， 6x30mm AP SX * 150" : primary); }, default);
        Assert.True(result.Matched);
        Assert.True(reads > 1);
        Assert.Equal("4 ， 6x30mm AP SX * 150", result.Text);
    }

    [Fact]
    public async Task Valid_original_title_remains_the_first_choice()
    {
        int reads = 0;
        var result = await ScreenProbe.ReadRecipeTitleCandidatesAsync("体能强化剂", (_, _) =>
        { reads++; return Task.FromResult("“ 体 能 强 化 剂"); }, default);
        Assert.True(result.Matched); Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("4.6×30mm FMJ SX *150", "4.6×30mm AP SX")]
    [InlineData("5.45×39mm BS ST+", "5.45×39mm BS")]
    [InlineData("5.8×42mm DVC12+P", "5.8×42mm DVC12")]
    [InlineData("XCOG 35倍", "XCOG 3.5倍")]
    [InlineData("6x30mmAPSX*150", "4.6×30mm AP SX")]
    public async Task Crop_fallback_never_guesses_missing_model_characters(string observed, string expected)
    {
        var result = await ScreenProbe.ReadRecipeTitleCandidatesAsync(expected, (_, _) => Task.FromResult(observed), default);
        Assert.False(result.Matched); Assert.Equal(observed, result.Text);
    }

    [Fact]
    public async Task Original_one_and_a_half_scale_can_recover_when_the_inner_crop_is_bad()
    {
        var result = await ScreenProbe.ReadRecipeTitleCandidatesAsync("4.6×30mm AP SX", (narrow, scale) =>
            Task.FromResult(!narrow && scale == 1.5 ? "4 ， 6x30mm AP SX *150" : "6x30mmAPSX*150"), default);
        Assert.True(result.Matched);
    }

    [Fact]
    public async Task Cancellation_during_OCR_is_observed_before_acceptance_or_more_reads()
    {
        using var cancellation = new CancellationTokenSource(); int reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenProbe.ReadRecipeTitleCandidatesAsync(
            "4.6×30mm AP SX", (_, _) => { reads++; cancellation.Cancel(); return Task.FromResult("4.6×30mm AP SX"); }, cancellation.Token));
        Assert.Equal(1, reads);
    }
}

public sealed class NightWindowGuardTests
{
    [Fact]
    public void Background_or_minimized_target_does_not_capture_other_windows()
    {
        int captures = 0;
        Assert.Throws<StepFailedException>(() => ScreenProbe.ReadFromFocusedTarget(() => false, () => ++captures));
        Assert.Equal(0, captures);
    }

    [Fact]
    public void Focus_change_during_capture_discards_the_frame()
    {
        int checks = 0, captures = 0;
        Assert.Throws<StepFailedException>(() => ScreenProbe.ReadFromFocusedTarget(() => ++checks == 1, () => ++captures));
        Assert.Equal(1, captures);
    }

    [Fact]
    public void Geometry_race_is_reported_as_an_interrupted_read_without_retrying()
    {
        int captures = 0;
        var failure = Assert.Throws<StepFailedException>(() => ScreenProbe.ReadFromFocusedTarget<int>(() => true,
            () => { captures++; throw new InvalidOperationException("客户区已最小化"); }));
        Assert.Contains("客户区已最小化", failure.Message); Assert.Equal(1, captures);
    }

    [Fact]
    public void Input_guard_rejects_a_background_window()
    {
        Assert.Throws<StepFailedException>(() => ScreenProbe.RequireFocusedTarget(() => false));
    }

    [Fact]
    public void Stable_foreground_capture_is_not_repeated()
    {
        int captures = 0;
        Assert.Equal(1, ScreenProbe.ReadFromFocusedTarget(() => true, () => ++captures));
        Assert.Equal(1, captures);
    }
}
