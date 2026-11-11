using DeltaCrafter.Core.L0;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class TextMatchTests
{
    [Theory]
    [InlineData("“ 体 能 强 化 剂", "体能强化剂", true)]
    [InlineData("“体能强化剂”", "体能强化剂", true)]
    [InlineData("“5.45×39mm BS ST+”", "5.45×39mm BS", false)]
    [InlineData("“XCOG突击35倍瞄准镜”", "XCOG突击3.5倍瞄准镜", false)]
    [InlineData("体“能强化剂", "体能强化剂", false)]
    public void Recipe_title_ignores_only_edge_chinese_quotes(string observed, string expected, bool matches)
    {
        Assert.Equal(matches, TextMatch.MatchesRecipeName(observed, expected));
    }

    [Theory]
    [InlineData("5．45x39mm BT * 180", "5.45×39mm BT", true)]
    [InlineData("5．45×39mmBT×180", "5.45×39mm BT", true)]
    [InlineData("5.45×39mm BT", "5.45×39mm BT *180", true)]
    [InlineData("5.45×39mm BT ×180", "5.45×39mm BT ×180", true)]
    [InlineData("5.45×39mm BS ST+", "5.45×39mm BS", false)]
    [InlineData("5.45×39mm BS", "5.45×39mm BS ST+", false)]
    [InlineData("5.8×42mm DVC12+P", "5.8×42mm DVC12", false)]
    [InlineData("5.45×39mm BS", "5.45×39mm BT", false)]
    [InlineData("5.56×45mm BT", "5.45×39mm BT", false)]
    [InlineData("XCOG突击35倍瞄准镜", "XCOG突击3.5倍瞄准镜", false)]
    [InlineData("7`62x51mmBPZ", "7.62×51mm BPZ", true)]
    [InlineData("7毛2x39mmAP", "7.62×39mm AP", true)]
    [InlineData("OLIGHT WARRIOR 3S战术手电", "WARRIOR3S战术手电", true)]
    [InlineData("0凵GHTWARRIOR3S战术手电", "WARRIOR3S战术手电", true)]
    [InlineData("0 凵GHT WARRIOR 3S战术手电", "WARRIOR3S战术手电", true)]
    [InlineData("0 凵 G H T WARRIOR 3S战术手电", "WARRIOR3S战术手电", true)]
    [InlineData("OLIGHT WARRIOR 3战术手电", "WARRIOR3S战术手电", false)]
    [InlineData("视点3倍喵准镜", "视点3倍瞄准镜", true)]
    [InlineData("体能氵敫活针", "体能激活针", true)]
    [InlineData("04．6x30mm AP SX * 150", "4.6×30mm AP SX", true)]
    [InlineData("04 ． 6x30mm AP SX * 150", "4.6×30mm AP SX", true)]
    [InlineData("》4．6x30mm AP SX * 150", "4.6×30mm AP SX", true)]
    [InlineData("卜4．6x30mm AP SX * 150", "4.6×30mm AP SX", true)]
    [InlineData("0重型突击背心", "重型突击背心", true)]
    [InlineData("卜重型突击背心", "重型突击背心", true)]
    [InlineData("》重型突击背心", "重型突击背心", true)]
    [InlineData("体能强化针", "体能激活针", false)]
    [InlineData("． 300 BLK * 120", ".300 BLK", true)]
    [InlineData("300 BLK *120", ".300 BLK", false)]
    [InlineData("0 · 300 BLK *120", ".300 BLK", false)]
    [InlineData(".300 BLK SUB-5", ".300 BLK", false)]
    [InlineData("未知前缀5.45×39mm BS", "5.45×39mm BS", false)]
    [InlineData("未知前缀重型突击背心", "重型突击背心", false)]
    [InlineData("DVC12X180", "DVC12", false)]
    [InlineData("", "", false)]
    public void Recipe_match_checks_the_complete_model(string observed, string expected, bool matches)
    {
        Assert.Equal(matches, TextMatch.MatchesRecipeName(observed, expected));
    }

    [Theory]
    [InlineData("5．45x39mm BT * 180")]
    [InlineData("5．45×39mm BT")]
    public void Fullwidth_decimal_in_selected_title_matches_the_same_ammunition(string observed)
    {
        Assert.True(TextMatch.LineContains(observed, "5.45×39mm BT"));
    }

    [Theory]
    [InlineData("5．45x39mm BS * 180")]
    [InlineData("5．45x39mm BP * 180")]
    [InlineData("5．56x45mm BT * 180")]
    public void Fullwidth_decimal_does_not_hide_a_different_ammunition_model(string observed)
    {
        Assert.False(TextMatch.LineContains(observed, "5.45×39mm BT"));
    }

    [Theory]
    [InlineData("7.62×39mm AP", "7毛2x39mmAP")]
    [InlineData("7.62×51mm BPZ", "7`62×51mmBPZ")]
    [InlineData("OE2战斗兴奋剂", "0E2战斗兴奋剂")]
    public void Canonical_folds_observed_ocr_variants(string expected, string observed)
    {
        Assert.Equal(TextMatch.Canonical(expected), TextMatch.Canonical(observed));
    }

    [Fact]
    public void Substring_distance_ignores_price_and_badge_text()
    {
        int distance = TextMatch.SubstringDistance("1250 7.62×39mm AP 已解锁", "7.62×39mm AP");

        Assert.Equal(0, distance);
    }

    [Fact]
    public void Similar_ammunition_variants_remain_distinguishable()
    {
        int distance = TextMatch.SubstringDistance("7.62×39mm BP", "7.62×39mm AP");

        Assert.Equal(1, distance);
    }
}
