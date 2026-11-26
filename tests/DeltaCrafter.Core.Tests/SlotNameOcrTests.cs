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

    [Fact]
    public void Real_slot_title_candidates_normalize_verified_ocr_artifacts_and_prefer_clean_readings()
    {
        Assert.Equal(".300 BLK TAC-TX", ScreenProbe.SelectSlotName([
            "、 300 BLK TAC-TX", "、 3 囗 囗 BLK TAC-TX", "、 30 囗 BLK TAC-TX",
            "． 3U0 BLK TAC-TX", "· 300 BLK TAC-TX", "． 300 BLK TAC-TX"
        ]));
        Assert.Equal("XCOG突击3.5倍瞄准镜", ScreenProbe.SelectSlotName([
            "× COG 突 击 3 、 5 倍 喵 准 镜", "× COG 突 击 3 ． 5 倍 瞄 准 镜",
            "XCOG 突 击 3 ． 5 倍 瞄 准 镜", "XCOG 突 击 3 · 5 倍 瞄 准 镜"
        ]));
        Assert.Equal("4.6×30mm AP SX", ScreenProbe.SelectSlotName([
            "4 ． 6X3 囗 mm AP SX", "4 · 6 × 30mm AP SX", "4 ． 6 × 30 mm AP SX"
        ]));
        Assert.Equal("PEQ-2红色激光镭指", ScreenProbe.SelectSlotName([
            "PEQ 一 2 红 色 激 光 镭 指", "PEQ 一 2 红 色 激 光 镭 指", "PEQ 一 2 红 色 激 光 镭 指"
        ]));
    }
}
