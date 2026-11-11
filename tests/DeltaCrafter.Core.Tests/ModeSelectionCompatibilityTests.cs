using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ModeSelectionCompatibilityTests
{
    [Theory]
    [InlineData("烽 火 地 带\n全 面 战 场\n黑潮爆破\n黑鹰坠落", true)]
    [InlineData("烽火地带 全面战场", true)]
    [InlineData("烽火地带 撤离 生命值100", false)]
    [InlineData("开始游戏 仓库 特勤处", false)]
    [InlineData("全面战场", false)]
    public void Current_mode_picker_requires_both_distinct_cards(string text, bool expected)
        => Assert.Equal(expected, ScreenProbe.IsModeSelectionMenuText(text));
}
