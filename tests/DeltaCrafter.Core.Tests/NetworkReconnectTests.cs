using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NetworkReconnectTests
{
    [Theory]
    [InlineData("与三角洲行动网络断开，是否尝试再次连接 返回登录 确认重连")]
    [InlineData("与 三角洲 行动 网络 断开，是否 尝试 再次 连接\n返回登录\n确认重连")]
    [InlineData("网络断开 再次连接 确认重连 返回登录")]
    public void Real_reconnect_dialog_text_is_detected(string text)
        => Assert.True(ScreenProbe.IsNetworkReconnectText(text));

    [Theory]
    [InlineData("开始游戏 仓库 特勤处")]
    [InlineData("对局倒计时 17:09 生命值 100")]
    [InlineData("与三角洲行动网络断开 返回登录")]
    [InlineData("登录页面 确认重连")]
    public void Ambiguous_or_incomplete_text_is_rejected(string text)
        => Assert.False(ScreenProbe.IsNetworkReconnectText(text));

    [Fact]
    public void Navigation_selects_only_confirm_reconnect()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        var inputs = new List<LaunchFlow.LobbyAction>();
        var action = LaunchFlow.DispatchLobbyScreen(AnchorKeys.NetworkReconnect, state,
            () => true, inputs.Add, default);
        Assert.Equal(LaunchFlow.LobbyAction.ConfirmNetworkReconnect, action);
        Assert.Equal(new[] { LaunchFlow.LobbyAction.ConfirmNetworkReconnect }, inputs);
    }

    [Fact]
    public void Reconnect_attempts_are_bounded_and_do_not_fall_back_to_login()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        var inputs = new List<LaunchFlow.LobbyAction>();
        for (int i = 0; i < 5; i++)
            Assert.Equal(LaunchFlow.LobbyAction.ConfirmNetworkReconnect,
                LaunchFlow.DispatchLobbyScreen(AnchorKeys.NetworkReconnect, state, () => true, inputs.Add, default));
        Assert.Equal(LaunchFlow.LobbyAction.ReconnectLimit,
            LaunchFlow.DispatchLobbyScreen(AnchorKeys.NetworkReconnect, state, () => true, inputs.Add, default));
        Assert.All(inputs, input => Assert.Equal(LaunchFlow.LobbyAction.ConfirmNetworkReconnect, input));
    }

    [Fact]
    public void Reconnect_button_point_stays_inside_the_green_button_from_the_reference_image()
    {
        var point = ScreenProbe.NetworkReconnectConfirmPoint;
        Assert.InRange(point.X, 0.505, 0.66);
        Assert.InRange(point.Y, 0.599, 0.653);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Idle_monitor_only_runs_when_no_round_or_maintenance_is_active(bool running, bool blocked, bool expected)
        => Assert.Equal(expected, AutomationCoordinator.ShouldMonitorIdleNetworkReconnect(running, blocked));
}
