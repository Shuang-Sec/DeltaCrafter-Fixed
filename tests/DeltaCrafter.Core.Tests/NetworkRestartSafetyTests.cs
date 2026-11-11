using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NetworkRestartSafetyTests
{
    [Fact]
    public void Restart_fallback_is_enabled_by_default_after_explicit_user_opt_in()
        => Assert.True(new AppSettings().AutoRestartOnNetworkReconnect);

    [Theory]
    [InlineData("与三角洲行动网络断开 是否尝试再次连接 确认重连 返回登录")]
    public void A_plain_reconnect_dialog_is_safe_to_restart(string text)
        => Assert.True(ScreenProbe.IsSafeToRestartAfterNetworkReconnectText(text));

    [Theory]
    [InlineData("网络断开 再次连接 确认重连 返回登录 生命值 100")]
    [InlineData("网络断开 再次连接 确认重连 返回登录 撤离 倒计时 17:09")]
    [InlineData("网络断开 再次连接 确认重连 返回登录 击杀 队友")]
    [InlineData("对局 HUD 网络断开 再次连接 确认重连 返回登录")]
    [InlineData("网络断开 再次连接 确认重连 返回登录 价格变动")]
    public void Match_HUD_makes_restart_unsafe(string text)
        => Assert.False(ScreenProbe.IsSafeToRestartAfterNetworkReconnectText(text));

    [Fact]
    public void Restart_is_only_allowed_once_per_recovery_attempt()
    {
        var state = new LaunchFlow.LobbyNavigationState();
        Assert.True(LaunchFlow.ConsumeNetworkRestartBudget(state));
        Assert.False(LaunchFlow.ConsumeNetworkRestartBudget(state));
    }
}
