using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class ConfirmationPolicyTests
{
    [Fact]
    public void Spec_ops_entry_selector_requires_unique_top_tab()
    {
        var selected = ScreenProbe.SelectSpecOpsEntry([
            new("开始游戏", 100, 50), new("特勤处", 1120, 72), new("改枪台", 1350, 72)]);
        Assert.Equal((1120, 72), (selected!.CenterX, selected.CenterY));
    }

    [Fact]
    public void Spec_ops_entry_selector_rejects_missing_or_ambiguous_tab()
    {
        Assert.Null(ScreenProbe.SelectSpecOpsEntry([new("进入特勤处", 200, 1380)]));
        Assert.Null(ScreenProbe.SelectSpecOpsEntry([new("特勤处", 900, 72), new("特勤处", 1120, 72)]));
    }

    [Fact]
    public void Duplicate_confirm_buttons_are_rejected() => Assert.Null(ScreenProbe.SelectConfirmationButton(
        ConfirmationKind.Abort, [new("确认", 10, 20), new("确认", 30, 20)]));

    [Theory]
    [InlineData("取消")]
    [InlineData("返回登录")]
    [InlineData("请确认材料")]
    public void Other_text_is_not_a_button(string text) => Assert.Null(ScreenProbe.SelectConfirmationButton(
        ConfirmationKind.Purchase, [new(text, 10, 20)]));

    [Fact]
    public void Purchase_accepts_unique_amount_button() => Assert.NotNull(ScreenProbe.SelectConfirmationButton(
        ConfirmationKind.Purchase, [new("50,872", 100, 100)]));

    [Fact]
    public void Unknown_context_never_accepts_confirm() => Assert.Null(ScreenProbe.SelectConfirmationButton(
        ConfirmationKind.Unknown, [new("确认", 100, 100)]));

    [Theory]
    [InlineData("中止", false)]
    [InlineData("未知文字", false)]
    [InlineData("生产", true)]
    [InlineData("一键补齐", true)]
    public void Abort_requires_positive_idle_button(string label, bool expected) =>
        Assert.Equal(expected, AbortFlow.IsConfirmedIdleLabel(label));
}
