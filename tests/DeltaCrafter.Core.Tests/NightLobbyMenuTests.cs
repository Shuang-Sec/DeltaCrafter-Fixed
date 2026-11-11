using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NightLobbyMenuTests
{
    [Theory]
    [InlineData("开 始 游 戏\n仓 库\n特 战 干 员\n部 门\n交 易 行\n特 勤 处\n改 枪 台")]
    [InlineData("开始游戏 仓库 特勤处")]
    public void Real_warehouse_and_trading_header_confirm_a_menu_with_a_specops_entry(string raw)
        => Assert.True(ScreenProbe.IsLobbyMenuHeader(raw));

    [Theory]
    [InlineData("进入特勤处")]
    [InlineData("开始游戏 仓库")]
    [InlineData("开始游戏 特勤处")]
    [InlineData("烽火地带 全面战场 In The Summer")]
    [InlineData("MC201防弹头盔 00:20:30 Tab Ctrl")]
    public void Partial_labels_and_match_HUD_are_not_treated_as_the_navigation_header(string raw)
        => Assert.False(ScreenProbe.IsLobbyMenuHeader(raw));

    [Fact]
    public async Task Header_OCR_has_a_bounded_unscaled_fallback()
    {
        int reads = 0;
        bool hit = await ScreenProbe.ReadLobbyMenuHeaderAsync(scale =>
        { reads++; return Task.FromResult(scale == 1 ? "开始游戏 仓库 特勤处" : "开始游戏 仓库"); }, default);
        Assert.True(hit); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Cancellation_during_header_OCR_is_not_accepted_as_navigation_permission()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenProbe.ReadLobbyMenuHeaderAsync(_ =>
        { cancellation.Cancel(); return Task.FromResult("开始游戏 仓库 特勤处"); }, cancellation.Token));
    }
}
