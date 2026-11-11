using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class PurchaseConfirmationTests
{
    [Theory]
    [InlineData("价格变动提醒 购买成功 盒装挂耳咖啡 低级燃料*2 50,872")]
    [InlineData("价 格 变 动 提 醒\n购 买 成 功\n50,872")]
    public void Real_purchase_confirmation_text_is_detected(string text)
        => Assert.True(ScreenProbe.IsPurchaseConfirmationText(text));

    [Theory]
    [InlineData("一键补齐清单 购买")]
    [InlineData("仓库空间不足")]
    [InlineData("价格参考 50,872")]
    [InlineData("价格变动提醒 返回登录")]
    public void Incomplete_or_other_dialog_text_is_rejected(string text)
        => Assert.False(ScreenProbe.IsPurchaseConfirmationText(text));

    [Fact]
    public void Purchase_confirm_point_matches_the_green_amount_button_in_the_reference_image()
    {
        var point = ScreenProbe.PurchaseConfirmationPoint;
        Assert.InRange(point.X, 0.45, 0.55);
        Assert.InRange(point.Y, 0.65, 0.75);
    }

    [Theory]
    [InlineData("125，2彐ø")] // 2026-11-09 真实读数
    [InlineData("，125，2彐ø")]
    [InlineData("125，230")]
    [InlineData("52，872")] // 2026-10-27 真实读数
    [InlineData("52,872")]
    [InlineData("298,586")] // 2026-10-31 一键补齐清单总价
    [InlineData("980")]
    public void Amount_button_labels_seen_in_real_frames_are_accepted(string label)
        => Assert.True(ScreenProbe.IsPurchaseAmountLabel(label));

    [Theory]
    [InlineData("45,781(+11%)")]
    [InlineData("240,291 30,445")]
    [InlineData("全选")]
    [InlineData("购买")]
    [InlineData("1253k")]
    [InlineData("确认购买")]
    [InlineData("")]
    public void Non_amount_labels_are_rejected(string label)
        => Assert.False(ScreenProbe.IsPurchaseAmountLabel(label));

    [Fact]
    public void Purchase_button_roi_covers_the_measured_green_button()
    {
        var roi = ScreenProbe.PurchaseConfirmationButtonRoi;
        Assert.InRange(roi.X, 0.40, 0.44);
        Assert.InRange(roi.Y, 0.63, 0.67);
        Assert.True(roi.X + roi.W >= 0.57, "按钮右侧应覆盖到 x=0.57");
        Assert.True(roi.Y + roi.H >= 0.71, "按钮下沿应覆盖到 y=0.71");
    }
}
