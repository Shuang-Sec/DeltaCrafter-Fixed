using System.Reflection;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class WeGameLaunchCompatibilityTests
{
    [Theory]
    [InlineData("wegame", "WeGame", true)]
    [InlineData("wegame", "Wegame", true)]
    [InlineData("DeltaForceClient-Win64-Shipping", "WeGame", false)]
    [InlineData("wegame", "no_active_wnd", false)]
    [InlineData("wegame", "三角洲行动", false)]
    public void Platform_launcher_is_distinct_from_game_and_hidden_helper(string process, string title, bool expected)
        => Assert.Equal(expected, (bool)Invoke(typeof(GameWindowBrick), "IsWeGameLauncherCandidate", process, title)!);

    [Theory]
    [InlineData("三角洲行动 烽火地带 全面战场", true)]
    [InlineData("三 角 洲 行 动\n烽 火 地 带\n全 面 战 场", true)]
    [InlineData("三角洲行动 英雄联盟 启动", false)]
    [InlineData("烽火地带 全面战场", false)]
    [InlineData("三角洲行动 烽火地带", false)]
    public void Platform_start_requires_delta_detail_page(string text, bool expected)
        => Assert.Equal(expected, (bool)Invoke(typeof(ScreenProbe), "IsWeGameDeltaPageText", text)!);

    [Theory]
    [InlineData("启 动", true)]
    [InlineData("开始游戏", true)]
    [InlineData("启动中", false)]
    [InlineData("更新", false)]
    public void Only_ready_start_label_can_launch(string text, bool expected)
    {
        OcrLine[] lines = [new(text, 1382, 849)];
        var actual = Invoke(typeof(ScreenProbe), "SelectWeGameStartButton", (object)lines);
        Assert.Equal(expected, actual is OcrLine);
    }

    [Fact]
    public void Duplicate_start_labels_do_not_choose_arbitrarily()
        => Assert.Null(Invoke(typeof(ScreenProbe), "SelectWeGameStartButton", (object)new OcrLine[]{new("启动", 10, 20),new("启动", 40, 20)}));

    [Theory]
    [InlineData("browser","WeGame",@"F:\game\WeGame\QBBlinkTrial\browser.exe",true)]
    [InlineData("browser","WeGame",@"C:\Other\browser.exe",false)]
    [InlineData("chrome","WeGame",@"F:\game\WeGame\QBBlinkTrial\browser.exe",false)]
    [InlineData("browser","其他页面",@"F:\game\WeGame\QBBlinkTrial\browser.exe",false)]
    public void Actual_platform_browser_is_bound_to_configured_installation(string process,string title,string actual,bool expected)
        => Assert.Equal(expected,(bool)Invoke(typeof(GameWindowBrick),"IsWeGameBrowserCandidate",process,title,actual,@"F:\game\WeGame\wegame.exe")!);

    private const string ConfiguredWeGamePath = @"F:\game\WeGame\wegame.exe";
    private const string VerifiedBrowserPath = @"F:\game\WeGame\QBBlinkTrial\browser.exe";

    [Theory]
    [InlineData(1150, 720, false)]
    [InlineData(1150, 720, true)]
    [InlineData(1600, 900, false)]
    [InlineData(1600, 900, true)]
    public void Visible_browser_precedes_visible_shell_even_when_browser_is_smaller_or_equal(
        int browserWidth, int browserHeight, bool reverseOrder)
    {
        var shell = Candidate(1, "wegame", true, 1600, 900);
        var browser = Candidate(2, "browser", true, browserWidth, browserHeight);
        var windows = reverseOrder ? new[] { browser, shell } : new[] { shell, browser };

        Assert.Equal(browser.Window, GameWindowBrick.SelectWeGameLauncher(windows, ConfiguredWeGamePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Visible_login_shell_precedes_hidden_browser(bool reverseOrder)
    {
        var shell = Candidate(1, "wegame", true, 1150, 720);
        var browser = Candidate(2, "browser", false, 1600, 900);
        var windows = reverseOrder ? new[] { browser, shell } : new[] { shell, browser };

        Assert.Equal(shell.Window, GameWindowBrick.SelectWeGameLauncher(windows, ConfiguredWeGamePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tray_state_prefers_verified_browser_when_every_window_is_hidden(bool reverseOrder)
    {
        var shell = Candidate(1, "wegame", false, 1600, 900);
        var browser = Candidate(2, "browser", false, 1150, 720);
        var windows = reverseOrder ? new[] { browser, shell } : new[] { shell, browser };

        Assert.Equal(browser.Window, GameWindowBrick.SelectWeGameLauncher(windows, ConfiguredWeGamePath));
    }

    [Theory]
    [InlineData("wegame", true)]
    [InlineData("wegame", false)]
    [InlineData("browser", true)]
    [InlineData("browser", false)]
    public void Same_kind_and_visibility_keep_largest_client_area(string process, bool visible)
    {
        var small = Candidate(1, process, visible, 1150, 720);
        var large = Candidate(2, process, visible, 1600, 900);

        Assert.Equal(large.Window, GameWindowBrick.SelectWeGameLauncher([small, large], ConfiguredWeGamePath));
        Assert.Equal(large.Window, GameWindowBrick.SelectWeGameLauncher([large, small], ConfiguredWeGamePath));
    }

    [Theory]
    [InlineData("browser", "WeGame", @"C:\Other\browser.exe")]
    [InlineData("chrome", "WeGame", VerifiedBrowserPath)]
    [InlineData("browser", "其他页面", VerifiedBrowserPath)]
    public void Unrelated_visible_browser_cannot_outrank_a_valid_hidden_shell(
        string process, string title, string actualPath)
    {
        var shell = Candidate(1, "wegame", false, 1150, 720);
        var unrelated = Candidate(2, process, true, 3840, 2160) with
        {
            Window = new GameWindowInfo(2, title, "CefTopWindow", 2),
            ProcessPath = actualPath
        };

        Assert.Equal(shell.Window, GameWindowBrick.SelectWeGameLauncher([unrelated, shell], ConfiguredWeGamePath));
        Assert.Null(GameWindowBrick.SelectWeGameLauncher([unrelated], ConfiguredWeGamePath));
    }

    [Theory]
    [InlineData(959, 720)]
    [InlineData(1150, 499)]
    public void Undersized_verified_browser_is_not_a_launcher(int width, int height)
    {
        var browser = Candidate(1, "browser", true, width, height);

        Assert.Null(GameWindowBrick.SelectWeGameLauncher([browser], ConfiguredWeGamePath));
    }

    [Fact]
    public void Empty_launcher_candidates_return_null()
        => Assert.Null(GameWindowBrick.SelectWeGameLauncher([], ConfiguredWeGamePath));

    // 纯窗口快照与生产枚举共用选择函数，测试不调用Win32或还原任何真实窗口。
    private static GameWindowBrick.WeGameLauncherWindow Candidate(
        int id, string process, bool visible, int width, int height) =>
        new(new GameWindowInfo(id, "WeGame", process == "browser" ? "CefTopWindow" : "TWINCONTROL", id),
            process, process == "browser" ? VerifiedBrowserPath : null,
            new PixelRect(0, 0, width, height), visible);

    private static object? Invoke(Type type, string name, params object[] values)
    {
        var method=type.GetMethod(name, BindingFlags.NonPublic|BindingFlags.Static);
        Assert.True(method is not null, "缺少WeGame实际启动兼容行为："+name);
        return method!.Invoke(null, values);
    }
}
