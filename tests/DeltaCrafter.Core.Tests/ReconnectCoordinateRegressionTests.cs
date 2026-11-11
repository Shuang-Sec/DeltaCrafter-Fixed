using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ReconnectCoordinateRegressionTests
{
    // 用户原图2560x1440中的绿色确认按钮边界，按原图而非2048x1152预览量取。
    [Theory]
    [InlineData(2560, 1440, 1293, 1693, 862, 941)]
    [InlineData(1920, 1080, 970, 1270, 646, 706)]
    public void Reconnect_click_is_inside_the_right_green_button(int width, int height,
        int left, int right, int top, int bottom)
    {
        var (x, y) = PixelMapper.ToPixel(ScreenProbe.NetworkReconnectConfirmPoint, 0, 0, width, height);
        Assert.InRange(x, left, right);
        Assert.InRange(y, top, bottom);
    }

    [Theory]
    [InlineData("与三角洲行动网络断开，是否尝试再次连接")]
    [InlineData("网络断开 再次连接 返回登录")]
    public void Message_without_a_visible_confirm_button_does_not_authorize_clicking(string raw)
        => Assert.False(ScreenProbe.IsNetworkReconnectText(raw));
}
