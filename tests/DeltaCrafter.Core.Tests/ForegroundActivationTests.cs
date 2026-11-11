using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>实际前台等待边界的离线回归，不调用Win32或发送激活按键。</summary>
public sealed class ForegroundActivationTests
{
    [Theory]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, true, true, 1600, 900)]
    [InlineData(true, false, true, 0, 900)]
    [InlineData(true, false, true, 1600, 0)]
    [InlineData(false, false, true, 1600, 900)]
    [InlineData(true, false, false, 1600, 900)]
    public void Foreground_requires_existing_restored_target_and_positive_client_area(
        bool exists, bool minimized, bool targetMatches, int width, int height)
    {
        var runtime = new ForegroundHarness
        {
            Observe = _ => (exists && !minimized && targetMatches, width > 0 && height > 0)
        };

        Assert.False(runtime.Run(250));
        Assert.Equal(1, runtime.Activations);
    }

    [Fact]
    public void Foreground_hwnd_waits_until_client_area_becomes_available()
    {
        var runtime = new ForegroundHarness { Observe = now => (true, now >= 200) };

        Assert.True(runtime.Run(500));
        Assert.True(runtime.Now >= 200, "前台句柄匹配但客户区仍不可用时不能提前返回成功。");
        Assert.Equal(1, runtime.Activations);
    }

    [Fact]
    public void Final_sample_also_rejects_foreground_target_without_client_area()
    {
        var runtime = new ForegroundHarness { Observe = _ => (true, false) };

        Assert.False(runtime.Run(0));
        Assert.Equal(1, runtime.Samples);
    }

    [Fact]
    public void Final_sample_rejects_target_that_loses_client_area_at_deadline()
    {
        var runtime = new ForegroundHarness { Observe = now => (now >= 100, now < 100) };

        Assert.False(runtime.Run(100));
        Assert.True(runtime.Now >= 100);
    }

    [Fact]
    public void Final_sample_accepts_a_restored_foreground_target_with_client_area()
    {
        var runtime = new ForegroundHarness { Observe = _ => (true, true) };

        Assert.True(runtime.Run(0));
        Assert.Equal(1, runtime.Activations);
        Assert.Equal(1, runtime.Samples);
    }

    private sealed class ForegroundHarness
    {
        internal long Now;
        internal int Activations, Samples;
        internal Func<long, (bool IsForeground, bool HasClientArea)> Observe = _ => (false, false);

        internal bool Run(int patienceMs) => GameWindowBrick.WaitForForeground(
            () => Activations++,
            () => { Samples++; return Observe(Now); },
            TimeSpan.FromMilliseconds(patienceMs), () => Now, milliseconds => Now += milliseconds);
    }
}
