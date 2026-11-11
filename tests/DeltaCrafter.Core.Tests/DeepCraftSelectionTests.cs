using System.Reflection;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class DeepCraftSelectionTests
{
    [Theory]
    [InlineData("5.45×39mm BS", "5.45×39mm BS ST+")]
    [InlineData("5.8×42mm DVC12", "5.8×42mm DVC12+P")]
    public void Recipe_list_does_not_choose_the_longer_neighboring_model(string target, string neighbor)
    {
        Assert.Null(Pick([new OcrLine(neighbor, 10, 20)], target));
        var expected = new OcrLine(target, 10, 30);
        Assert.Same(expected, Pick([new OcrLine(neighbor, 10, 20), expected], target));
    }

    [Fact]
    public void Single_character_ammunition_model_error_is_not_a_license_to_select_another_recipe()
    {
        Assert.Null(Pick([new OcrLine("5.45×39mm BS", 10, 20)], "5.45×39mm BT"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    public void Empty_normalized_search_key_does_not_select_the_first_recipe(string target)
    {
        Assert.Null(Pick([new OcrLine("战地医疗箱", 10, 20)], target));
    }

    [Fact]
    public void Observed_chinese_character_error_and_explicit_quantity_are_accepted_by_the_actual_list_selector()
    {
        var expected = new OcrLine("视点3倍喵准镜 * 1", 10, 30);
        Assert.Same(expected, Pick([new OcrLine("视点6倍瞄准镜", 10, 20), expected], "视点3倍瞄准镜"));
    }

    private static OcrLine? Pick(IReadOnlyList<OcrLine> lines, string target)
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var flow = new CraftStartFlow(null!, null!, null!, log);
        var method = typeof(CraftStartFlow).GetMethod("PickLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (OcrLine?)method.Invoke(flow, [lines, target, 0]);
    }
}
