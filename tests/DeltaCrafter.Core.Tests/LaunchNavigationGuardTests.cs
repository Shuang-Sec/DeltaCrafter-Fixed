using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>实际导航决策及输入事务，不调用窗口、键盘、鼠标或游戏进程。</summary>
public sealed class LaunchNavigationGuardTests
{
    [Fact]
    public void Collection_menu_is_recognized_by_navigation_and_gets_at_most_two_safe_escapes()
    {
        const string menu = "collection-menu";
        Assert.Contains(menu, LaunchFlow.KnownScreens);
        var state = new LaunchFlow.LobbyNavigationState();
        int inputs = 0;
        Assert.NotEqual(LaunchFlow.LobbyAction.Wait,
            LaunchFlow.DispatchLobbyScreen(menu, state, () => true, _ => inputs++, default));
        Assert.Equal(1, inputs);
        Assert.NotEqual(LaunchFlow.LobbyAction.Wait,
            LaunchFlow.DispatchLobbyScreen(menu, state, () => true, _ => inputs++, default));
        Assert.Equal(2, inputs);
        Assert.Equal(LaunchFlow.LobbyAction.Wait,
            LaunchFlow.DispatchLobbyScreen(menu, state, () => true, _ => inputs++, default));
        Assert.Equal(2, inputs);
    }

    [Theory]
    [InlineData(AnchorKeys.ModeSelect)]
    [InlineData(AnchorKeys.Safehouse)]
    [InlineData(AnchorKeys.PromoAnnounce)]
    [InlineData("collection-menu")]
    public void A_known_navigation_action_requires_foreground_and_sends_no_input_when_focus_fails(string screen)
    {
        int inputs = 0;
        Assert.Throws<StepFailedException>(() => LaunchFlow.DispatchLobbyScreen(screen,
            new LaunchFlow.LobbyNavigationState(), () => false, _ => inputs++, CancellationToken.None));
        Assert.Equal(0, inputs);
    }

    [Theory]
    [InlineData("武器 近战 挂饰 载具 道具 特殊物品", "Esc 返回", true)]
    [InlineData("收藏室 常规 限定 收藏柜", "Esc 返回", true)]
    [InlineData("收藏室 常规 限定 收藏柜", "Es 返回", true)]
    [InlineData("武器 弹药 背包 撤离", "Esc 返回", false)]
    [InlineData("武器 近战 挂饰 载具 道具 特殊物品", "使用", false)]
    [InlineData("价格 品质 全部类型", "Esc 返回", false)]
    [InlineData("开始游戏 仓库 特勤处", "Esc 返回", false)]
    public void Collection_navigation_requires_known_menu_markers_and_escape_hint(string header, string footer, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsCollectionMenuText(header, footer));
    }

    [Theory]
    [InlineData("增加容量 当前武器架容量为2", "Esc 返回", true)]
    [InlineData("增加容量 当前背包容量为2", "Esc 返回", false)]
    [InlineData("增加容量 当前武器架容量为2", "使用", false)]
    public void Collection_capacity_page_requires_capacity_marker_and_escape_hint(
        string content, string footer, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsCollectionCapacityText(content, footer));
    }

    [Fact]
    public void Launcher_click_transaction_stops_without_input_when_focus_fails()
    {
        int clicks = 0;
        Assert.Throws<StepFailedException>(() => LaunchFlow.RunFocusedAction(() => false, () => clicks++, CancellationToken.None));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void Cancellation_during_foreground_activation_prevents_the_following_global_input()
    {
        int inputs = 0;
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => LaunchFlow.RunFocusedAction(
            () => { cancellation.Cancel(); return true; }, () => inputs++, cancellation.Token));
        Assert.Equal(0, inputs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unrecognized-match-or-disconnect")]
    public void Unknown_screens_get_a_bounded_number_of_escape_attempts_then_only_wait(string? screen)
    {
        int escapes = 0;
        var state = new LaunchFlow.LobbyNavigationState();
        for (int pass = 0; pass < LaunchFlow.MaxUnknownEscapes; pass++)
            Assert.Equal(LaunchFlow.LobbyAction.EscapeUnknown,
                LaunchFlow.DispatchLobbyScreen(screen, state, () => true, _ => escapes++, CancellationToken.None));
        for (int pass = 0; pass < 20; pass++)
            Assert.Equal(LaunchFlow.LobbyAction.Wait,
                LaunchFlow.DispatchLobbyScreen(screen, state, () => true, _ => escapes++, CancellationToken.None));
        Assert.Equal(LaunchFlow.MaxUnknownEscapes, escapes);
    }

    [Fact]
    public void Unknown_screen_escape_returns_to_arrived_when_the_lobby_appears()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        int escapes = 0;
        Assert.Equal(LaunchFlow.LobbyAction.EscapeUnknown, LaunchFlow.DispatchLobbyScreen(
            "mail-or-notification", state, () => true, _ => escapes++, CancellationToken.None));
        Assert.Equal(LaunchFlow.LobbyAction.Arrived, LaunchFlow.DispatchLobbyScreen(
            AnchorKeys.Lobby, state, () => true, _ => escapes++, CancellationToken.None));
        Assert.Equal(1, escapes);
    }

    [Fact]
    public void A_recognized_production_page_gets_one_escape_then_only_observation_until_it_changes()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        var sent = new List<LaunchFlow.LobbyAction>();
        Assert.Equal(LaunchFlow.LobbyAction.EscapeProduction, LaunchFlow.DispatchLobbyScreen(
            AnchorKeys.Production, state, () => true, sent.Add, CancellationToken.None));
        for (int pass = 0; pass < 12; pass++)
            Assert.Equal(LaunchFlow.LobbyAction.Wait, LaunchFlow.DispatchLobbyScreen(
                AnchorKeys.Production, state, () => true, sent.Add, CancellationToken.None));
        Assert.Equal(new[] { LaunchFlow.LobbyAction.EscapeProduction }, sent);
        Assert.Equal(LaunchFlow.LobbyAction.Arrived, LaunchFlow.DispatchLobbyScreen(
            AnchorKeys.SpecOpsHome, state, () => true, sent.Add, CancellationToken.None));
        Assert.Single(sent);
    }

    [Fact]
    public void Production_is_in_the_actual_known_screen_set_used_by_the_probe()
    {
        Assert.Contains(AnchorKeys.Production, LaunchFlow.KnownScreens);
    }

    [Fact]
    public void Cancellation_after_screen_ocr_prevents_navigation_input()
    {
        int inputs = 0;
        Assert.Throws<OperationCanceledException>(() => LaunchFlow.DispatchLobbyScreen(
            AnchorKeys.Safehouse, new LaunchFlow.LobbyNavigationState(), () => true,
            _ => inputs++, new CancellationToken(true)));
        Assert.Equal(0, inputs);
    }

    [Fact]
    public void Recognized_promotions_keep_the_existing_eight_page_escape_budget()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        int escapes = 0;
        for (int pass = 0; pass < 8; pass++)
            Assert.Equal(LaunchFlow.LobbyAction.EscapePromo, LaunchFlow.DispatchLobbyScreen(
                AnchorKeys.PromoAnnounce, state, () => true, _ => escapes++, CancellationToken.None));
        Assert.Equal(LaunchFlow.LobbyAction.PromoLimit, LaunchFlow.DispatchLobbyScreen(
            AnchorKeys.PromoAnnounce, state, () => true, _ => escapes++, CancellationToken.None));
        Assert.Equal(8, escapes);
    }
}
