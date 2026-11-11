using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class FacilitySlotOcrTests
{
    [Theory]
    [InlineData("0凵GHTWARR℃R3S战术手电")]
    [InlineData("0LIGHT WARR℃R3S战术手电")]
    [InlineData("0凵GHTWARRIOR3S战术手电")]
    [InlineData("OLIGHT WARRIOR 3S战术手电")]
    public void Completed_flashlight_is_not_mistaken_for_a_damaged_countdown(string text)
    {
        var observation = Classify(text);
        Assert.Equal(FacilityPhase.ReadyToCollect, observation.Phase);
        Assert.Equal(text, observation.ItemName);
        Assert.Null(observation.Remaining);
    }

    [Theory]
    [InlineData("04：7口：口4")]
    [InlineData("09．75．35")]
    [InlineData("90145．48")]
    [InlineData("013652")]
    [InlineData("04℃x℃6")]
    [InlineData("℃04")]
    public void Damaged_countdown_is_not_mistaken_for_a_collectible_item(string text)
    {
        Assert.Equal(FacilityPhase.Unknown, Classify(text).Phase);
    }

    [Theory]
    [InlineData("09．15．35", 9, 15, 35)]
    [InlineData("09．15：36", 9, 15, 36)]
    [InlineData("09 ℃ 6 ： 40", 9, 6, 40)]
    public void Slot_countdown_with_fullwidth_dots_is_crafting(string timer, int hours, int minutes, int seconds)
    {
        var observation = Classify("精英防弹背心\n" + timer);
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal(new TimeSpan(hours, minutes, seconds), observation.Remaining);
        Assert.Equal("精英防弹背心", observation.ItemName);
    }

    [Fact]
    public void Numeric_temperature_misread_still_parses_as_countdown()
    {
        var observation = Classify("0凵GHTWARR℃R3S战术手电\n04℃3℃6");
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(6), observation.Remaining);
        Assert.Equal("0凵GHTWARR℃R3S战术手电", observation.ItemName);
    }

    [Theory]
    [InlineData("0210，24", "重型突击背心．0210：24", 2, 10, 24)]
    [InlineData("0210，24", "重型突击背心（902：10：24", 2, 10, 24)]
    [InlineData("0210：26", "重型突击背心．0210：26", 2, 10, 26)]
    [InlineData("01：1921", "体能强化剂（9011921", 1, 19, 21)]
    [InlineData("02:1024", "重型突击背心", 2, 10, 24)]
    [InlineData("0210．24", "重型突击背心", 2, 10, 24)]
    public void Slot_timer_with_exact_six_digits_and_one_separator_is_crafting(
        string timer, string slot, int hours, int minutes, int seconds)
    {
        // 与生产调用一致：独立计时区为首行，完整槽位为后续文本。
        var observation = Classify(timer + "\n" + slot);
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal(new TimeSpan(hours, minutes, seconds), observation.Remaining);
    }

    [Theory]
    [InlineData("021024")]
    [InlineData("02：1a26")]
    [InlineData("02：1口26")]
    [InlineData("021a，26")]
    [InlineData("02，1口26")]
    [InlineData("021a．26")]
    [InlineData("02．1口26")]
    [InlineData("0260，24")]
    [InlineData("0210，60")]
    [InlineData("01：6021")]
    [InlineData("01：19260")]
    [InlineData("10210，24")]
    public void Invalid_or_ambiguous_compacted_slot_timer_remains_unknown(string timer)
    {
        var observation = Classify(timer + "\n重型突击背心");
        Assert.Equal(FacilityPhase.Unknown, observation.Phase);
        Assert.Null(observation.Remaining);
    }

    [Theory]
    [InlineData("0210，24")]
    [InlineData("0210：26")]
    [InlineData("01：1921")]
    public void Compacted_timer_format_does_not_expand_the_general_parser(string timer)
    {
        Assert.False(CountdownParser.TryParse(timer, out _));
    }

    [Fact]
    public void Complete_independent_timer_precedes_a_later_compacted_slot_line()
    {
        var observation = Classify("02：10：24\n重型突击背心\n0210，26");
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal(new TimeSpan(2, 10, 24), observation.Remaining);
    }

    [Fact]
    public void Observed_armor_sequence_can_obtain_two_consistent_crafting_votes()
    {
        // 08:42实测五遍输入；旧实现只有第4遍一票，因此整个制造轮末尾失败。
        var raw = new[]
        {
            "02：1a26\n重型突击背心．0210：26",
            "02：1a25\n重型突击背心{021025",
            "0210，24\n重型突击背心．0210：24",
            "0210，24\n重型突击背心（902：10：24",
            "02：1a23\n重型突击背心．0210：23",
        };
        var observations = raw.Select(Classify).ToArray();
        var crafting = observations.Select((observation, index) => (observation, index))
            .Where(entry => entry.observation.Phase == FacilityPhase.Crafting).ToArray();
        Assert.True(crafting.Length >= 2);
        var consistent = typeof(CollectFlow).GetMethod("CountdownConsistent",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)consistent.Invoke(null, new object[]
        {
            crafting[0].observation, (long)crafting[0].index * 800,
            crafting[1].observation, (long)crafting[1].index * 800,
        })!);
    }

    private static FacilityObservation Classify(string text)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        var probe = new ScreenProbe(null!, null!, null!, null!, () => anchors, "", logger);
        var flow = new CollectFlow(probe, null!, null!, null!, logger);
        var method = typeof(CollectFlow).GetMethod("Classify", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (FacilityObservation)method.Invoke(flow, new object[] { text })!;
    }
}
