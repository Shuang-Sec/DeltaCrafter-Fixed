using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using System.Reflection;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class TimerContrastTests
{
    [Fact]
    public void Enhances_only_timer_pixels_without_modifying_the_original_frame()
    {
        byte[] pixels = [42, 43, 44, 255, 149, 149, 149, 255, 150, 150, 150, 255, 210, 211, 212, 255];
        var frame = new CapturedFrame(4, 1, pixels);
        var result = ScreenProbe.EnhanceTimerContrast(frame,
            [new NRect { X = 0.25, Y = 0, W = 0.5, H = 1 }]);
        Assert.Equal(new byte[] { 42, 43, 44, 255, 0, 0, 0, 255, 255, 255, 255, 255, 210, 211, 212, 255 }, result.Bgra);
        Assert.Equal(new byte[] { 42, 43, 44, 255, 149, 149, 149, 255, 150, 150, 150, 255, 210, 211, 212, 255 }, pixels);
        Assert.Equal(4, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Fact]
    public void Lower_timer_threshold_preserves_faint_separator_pixels_only_inside_timer_roi()
    {
        byte[] pixels = [42, 43, 44, 255, 79, 79, 79, 255, 80, 80, 80, 255, 210, 211, 212, 255];
        var frame = new CapturedFrame(4, 1, pixels);
        var method = typeof(ScreenProbe).GetMethod("EnhanceTimerContrast", BindingFlags.Static | BindingFlags.NonPublic)!;
        // 原实现固定150，无参数可用80恢复真实截图中消失的冒号。
        Assert.Equal(3, method.GetParameters().Length);
        var result = (CapturedFrame)method.Invoke(null, new object[]
        {
            frame, new[] { new NRect { X = 0.25, Y = 0, W = 0.5, H = 1 } }, 80,
        })!;
        Assert.Equal(new byte[] { 42, 43, 44, 255, 0, 0, 0, 255, 255, 255, 255, 255, 210, 211, 212, 255 }, result.Bgra);
        Assert.Equal(new byte[] { 42, 43, 44, 255, 79, 79, 79, 255, 80, 80, 80, 255, 210, 211, 212, 255 }, pixels);
    }
}
