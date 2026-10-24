using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using System.Reflection;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class FixedReleaseTests
{
    [Theory]
    [InlineData("OLIGHT WARRIOR 3S战术手电")]
    [InlineData("0凵GHTWARRIOR3S战术手电")]
    public void Production_list_selects_target_between_neighboring_models(string text)
    {
        var expected = new OcrLine(text, 100, 200);
        var lines = new[]
        {
            new OcrLine("OLIGHT Baldr Pro R多功能手电", 100, 100),
            expected,
            new OcrLine("OLIGHT Odin S战术手电", 100, 300),
        };
        Assert.Same(expected, PickLine(lines));
    }

    [Fact]
    public void Production_list_rejects_neighboring_models_without_target()
    {
        var lines = new[]
        {
            new OcrLine("OLIGHT Baldr Pro R多功能手电", 100, 100),
            new OcrLine("OLIGHT Odin S战术手电", 100, 300),
        };
        Assert.Null(PickLine(lines));
    }

    private static OcrLine? PickLine(IReadOnlyList<OcrLine> lines)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var flow = new CraftStartFlow(null!, null!, null!, logger);
        var method = typeof(CraftStartFlow).GetMethod("PickLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (OcrLine?)method.Invoke(flow, new object[] { lines, "WARRIOR3S战术手电", 0 });
    }

    [Fact]
    public void Default_profile_does_not_enable_automation_or_import_a_game_path()
    {
        var settings = new AppSettings();
        Assert.Equal("", settings.GamePath);
        Assert.False(settings.AutoLoopEnabled);
        Assert.Null(settings.WindowMatch.ExactTitle);
    }

    [Theory]
    [InlineData("OLIGHT WARRIOR 3S战术手电", true)]
    [InlineData("0凵GHTWARRIOR3S战术手电", true)]
    [InlineData("OLIGHT Baldr Pro R多功能手电", false)]
    [InlineData("OLIGHT Odin S战术手电", false)]
    [InlineData("WARRIOR3战术手电", false)]
    public void Catalog_key_identifies_only_warrior_3s(string text, bool expected)
    {
        var catalog = new JsonStoreBrick().Load<ItemCatalog>(
            Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));
        var item = Assert.Single(catalog.For(FacilityKey.TechCenter),
            entry => entry.Name == "OLIGHT WARRIOR 3S战术手电");
        Assert.Equal("WARRIOR3S战术手电", item.MatchKey);
        Assert.Equal(expected, TextMatch.LineContains(text, item.MatchKey));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Title_region_covers_long_name_without_leaving_frame(int width, int height)
    {
        var anchors = new JsonStoreBrick().Load<AnchorTable>(
            Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        var region = anchors.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiDetailTitle);
        Assert.Equal(0.325, region.X);
        Assert.Equal(0.30, region.W);
        var pixels = PixelMapper.ToPixelRect(region, width, height);
        Assert.True(pixels.X + pixels.W >= width * 0.54);
        Assert.True(pixels.X + pixels.W <= width);
        Assert.True(pixels.Y + pixels.H <= height);
    }

    [Fact]
    public void Data_directory_does_not_reuse_upstream_profile()
    {
        var paths = new AppDataBrick();
        Assert.Equal("DeltaCrafterFixed", Path.GetFileName(paths.Root));
        Assert.NotEqual(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaCrafter"), paths.Root);
    }
}
