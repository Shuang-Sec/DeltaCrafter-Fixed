using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class TimerReadoutQualityTests
{
    [Theory]
    [InlineData("004907", "00 ： 49 ： 07")]
    [InlineData("014011", "01 ： 40 ： 11")]
    [InlineData("03乇9：43", "03 ： 59 ： 43")]
    [InlineData("", "09 ： 59 ： 11")]
    [InlineData("OB:53:49", "08 ： 53 ： 49")]
    [InlineData("OO:34:48", "00 ： 34 ： 48")]
    public void Complete_fallback_replaces_only_the_unreadable_primary(string primary, string fallback)
    {
        // 原ROI真实像素对照：150/2x会丢分隔，80/3x恢复完整两分隔。
        Assert.Equal(fallback, Select(primary, fallback));
    }

    [Theory]
    [InlineData("03：59：43")]
    [InlineData("03 ： 59 ： 43")]
    [InlineData("03．59：43")]
    [InlineData("03：59，43")]
    [InlineData("102:00:00")]
    public void Complete_primary_keeps_priority_even_when_fallback_differs(string primary)
    {
        Assert.Equal(primary, Select(primary, "01:00:00"));
    }

    [Theory]
    [InlineData("004907")]
    [InlineData("0049：07")]
    [InlineData("00：4907")]
    [InlineData("00:60:07")]
    [InlineData("00:49:60")]
    [InlineData("00:49:口7")]
    [InlineData("O0:49:07")]
    [InlineData("OB:53:49")]
    [InlineData("OO:34:48")]
    [InlineData("902:10:24")]
    [InlineData("00：49汜7")]
    [InlineData("00:49:07x")]
    [InlineData("剩余时间00:49:07")]
    [InlineData("")]
    public void Invalid_fallback_does_not_replace_the_original_text(string fallback)
    {
        const string primary = "004907";
        Assert.Equal(primary, Select(primary, fallback));
    }

    [Theory]
    [InlineData("03．59，43")]
    [InlineData("03，59．43")]
    public void Selected_complete_timer_is_accepted_by_the_real_slot_classifier(string fallback)
    {
        var selected = Select("035943", fallback);
        using var logger = new LoggerConfiguration().CreateLogger();
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        var probe = new ScreenProbe(null!, null!, null!, null!, () => anchors, "", logger);
        var flow = new CollectFlow(probe, null!, null!, null!, logger);
        var classify = typeof(CollectFlow).GetMethod("Classify", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var observation = (FacilityObservation)classify.Invoke(flow, new object[] { selected + "\n精英防弹背心" })!;
        Assert.Equal(FacilityPhase.Crafting, observation.Phase);
        Assert.Equal(new TimeSpan(3, 59, 43), observation.Remaining);
    }

    private static string Select(string primary, string fallback)
    {
        var method = typeof(ScreenProbe).GetMethod("SelectTimerReadout", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { primary, fallback })!;
    }
}
