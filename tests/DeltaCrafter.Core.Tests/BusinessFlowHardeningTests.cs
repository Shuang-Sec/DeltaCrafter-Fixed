using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class BusinessFlowHardeningTests
{
    [Theory]
    [InlineData("仓空间不足", true)]
    [InlineData("仓空间不足C-TX*", true)]
    [InlineData("库空间不足", true)]
    [InlineData("仓库空间不足", true)]
    [InlineData("仓库找不到可以容纳钛合金的位置", true)]
    [InlineData("仓库已满", true)]
    [InlineData("仓库", false)]
    [InlineData("空间", false)]
    public void Warehouse_full_tolerates_single_dropped_prefix_char_without_false_positives(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsWarehouseFullText(raw, "仓库空间不足"));
    }

    [Theory]
    [InlineData("商品已售罄，购买失败", true)]
    [InlineData("商 品 已 售 罄", true)]
    [InlineData("部分商品售罄购买失败", true)]
    [InlineData("购买成功", false)]
    [InlineData("价格变动提醒", false)]
    [InlineData("", false)]
    public void Sold_out_toast_detector_recognizes_real_market_depletion_messages(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsSoldOutToastText(raw));
    }

    [Theory]
    [InlineData("等级3解锁", true)]
    [InlineData("立等级彐解锁", true)]
    [InlineData("Lv.3解锁", true)]
    [InlineData("生产", false)]
    [InlineData("中止", false)]
    [InlineData("一键补齐", false)]
    [InlineData("已解锁", false)]
    [InlineData("", false)]
    public void Locked_recipe_button_detector_catches_level_locked_formulas_without_touching_active_buttons(
        string normalizedLabel, bool expected)
    {
        Assert.Equal(expected, CraftStartFlow.IsLockedRecipeLabel(normalizedLabel));
    }

    [Theory]
    [InlineData("赛 季 通 行 证 进 度", "0 继 续", true)]
    [InlineData("赛季通行证进度", "继续", true)]
    [InlineData("通行证进度", "继续", true)]
    [InlineData("赛季通行证进度", "", false)]
    [InlineData("活动公告", "继续", false)]
    [InlineData("特勤处", "继续", false)]
    public void Season_pass_progress_overlay_is_classified_for_space_dismissal(
        string title, string continuation, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsSeasonPassProgressText(title, continuation));
    }

    [Theory]
    [InlineData("开 始 游 观\n仓 库\n特 战 干 员\n部 门\n交 男 行\n特 勤 处\n改 枪 台", true)]
    [InlineData("》 开 始 游 难 | 仓 库 | 特 战 干 员 | 特 勤 处", true)]
    [InlineData("开 始 游 观 仓 库", false)]
    [InlineData("开 始 游 难 特 勤 处", false)]
    public void Lobby_menu_header_tolerates_observed_start_game_tail_char_misreads(string raw, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsLobbyMenuHeader(raw));
    }
}
