using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ScreenSafetyGuardTests
{
    [Theory]
    [InlineData("仓库空间不足", true)]
    [InlineData("一仓库找不到可以容纳钛合金的位置", true)]
    [InlineData("找不到可以容纳芳纶纤维的位置", true)]
    [InlineData("仓库已满", true)]
    [InlineData("仑纤维，原木木板×2购买成功，价格更", false)]
    [InlineData("「管×3，枪械零件×1，原木木板×1购买成〕", false)]
    [InlineData("将使用1个槽位", false)]
    [InlineData("仓库", false)]
    [InlineData("", false)]
    public void Warehouse_full_detector_catches_fragmented_space_toast_without_false_positives(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsWarehouseFullText(raw, "仓库空间不足"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Off_specops_home_guard_tolerates_one_transient_frame_and_aborts_on_two(int offPagePasses, bool shouldAbort)
    {
        Assert.Equal(shouldAbort, CollectFlow.ShouldAbortOffSpecOpsHome(offPagePasses));
    }

    [Fact]
    public async Task Slot_click_guard_stops_without_clicking_when_not_on_specops_home()
    {
        int failures = 0;
        var ex = await Assert.ThrowsAsync<StepFailedException>(() =>
            CollectFlow.EnsureOnSpecOpsHomeBeforeSlotClickAsync(
                () => Task.FromResult(false),
                () =>
                {
                    failures++;
                    return Task.FromResult(new StepFailedException("领取测试", "不在特勤处"));
                },
                CancellationToken.None));
        Assert.Equal(1, failures);
        Assert.Contains("不在特勤处", ex.Message);
    }

    [Fact]
    public async Task Slot_click_guard_allows_proceeding_when_confirmed_on_specops_home()
    {
        int failures = 0;
        await CollectFlow.EnsureOnSpecOpsHomeBeforeSlotClickAsync(
            () => Task.FromResult(true),
            () =>
            {
                failures++;
                return Task.FromResult(new StepFailedException("领取测试", "不应失败"));
            },
            CancellationToken.None);
        Assert.Equal(0, failures);
    }

    [Theory]
    [InlineData("0 升 级 制 造\n誓 收 藏", true)]
    [InlineData("0et升级制造0", true)]
    [InlineData("升级制造", true)]
    [InlineData("限时商品集市", false)]
    [InlineData("仓库", false)]
    [InlineData("", false)]
    public void Specops_home_probe_text_matches_only_real_specops_header(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsSpecOpsHomeProbeText(raw, "升级制造"));
    }

    [Fact]
    public async Task Specops_home_frame_guard_rejects_blank_frame()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var blankFrame = new CapturedFrame(1920, 1080, new byte[1920 * 1080 * 4]);
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(fixture, "completed-specops-anchors.json"));
        using var logger = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(null!, null!, OcrBrick.CreateSimplifiedChinese(), null!, () => anchors, "", logger);

        Assert.False(await probe.IsSpecOpsHomeFrameAsync(blankFrame, CancellationToken.None));
    }
}
