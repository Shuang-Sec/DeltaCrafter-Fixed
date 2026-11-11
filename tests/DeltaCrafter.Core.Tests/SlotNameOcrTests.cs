using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class SlotNameOcrTests
{
    [Fact]
    public void Real_decimal_name_requires_three_complete_readings_and_preserves_the_decimal()
    {
        string[] readings = ["、 300 BLK", "300 BLK", "、 300 BLK", "· 300 BLK", "· 300 BLK", "． 300 BLK"];
        Assert.Equal(".300 BLK", ScreenProbe.SelectSlotName(readings));
    }

    [Fact]
    public void Cropped_medical_title_can_be_outvoted_by_complete_independent_readings()
    {
        Assert.Equal("战地医疗箱", ScreenProbe.SelectSlotName(
            ["医 疗 箱", "战 地 医 疗 箱", "战 地 医 疗 箱", "战 地 医 疗 箱"]));
    }

    [Fact]
    public void Ambiguous_models_do_not_win_by_partial_matching_or_by_using_a_plan()
    {
        Assert.Equal("", ScreenProbe.SelectSlotName(
            ["5.45×39mm BS", "5.45×39mm BS", "5.45×39mm BS ST+", "5.45×39mm BS ST+"]));
        Assert.Equal("", ScreenProbe.SelectSlotName(["H09防暴头盔", "H囗9防暴头盔", "H29防暴头盔"]));
        Assert.Equal("300BLK", ScreenProbe.SelectSlotName([".300BLK", ".300BLK", "300BLK", "300BLK", "300BLK"]));
    }
}
