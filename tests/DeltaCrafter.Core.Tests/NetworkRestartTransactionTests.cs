using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NetworkRestartTransactionTests
{
    private static readonly GameWindowInfo NewWindow = new((nint)200, "三角洲行动", "UnrealWindow", 201);

    [Theory]
    [InlineData(AnchorKeys.Lobby, 0, true)]
    [InlineData(AnchorKeys.SpecOpsHome, 29999, true)]
    [InlineData(AnchorKeys.Lobby, 30001, false)]
    [InlineData(AnchorKeys.Production, 100, false)]
    [InlineData(AnchorKeys.ReplenishPopup, 100, false)]
    [InlineData(null, 0, false)]
    public void An_opaque_dialog_needs_recent_positive_non_match_scene_evidence(string? scene, long age, bool expected)
        => Assert.Equal(expected, ScreenProbe.IsRecentRestartSafeScene(scene, age));

    [Fact]
    public void Opening_a_business_page_revokes_old_home_evidence_until_a_new_safe_scene_is_verified()
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(null!, null!, null!, null!, () => new AnchorTable(), "", log);
        probe.RecordRestartSceneEvidence((nint)100, AnchorKeys.SpecOpsHome, 1000);
        Assert.True(probe.HasRecentRestartSceneEvidence((nint)100, 1001));
        probe.InvalidateRestartSceneForInput();
        Assert.False(probe.HasRecentRestartSceneEvidence((nint)100, 1002));
        probe.RecordRestartSceneEvidence((nint)100, AnchorKeys.Production, 1003);
        Assert.False(probe.HasRecentRestartSceneEvidence((nint)100, 1004));
        probe.RecordRestartSceneEvidence((nint)100, AnchorKeys.Lobby, 1005);
        Assert.True(probe.HasRecentRestartSceneEvidence((nint)100, 1006));
    }

    [Fact]
    public void Evidence_from_an_old_window_or_an_unknown_frame_does_not_authorize_restart()
    {
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(null!, null!, null!, null!, () => new AnchorTable(), "", log);
        probe.RecordRestartSceneEvidence((nint)100, AnchorKeys.Lobby, 1000);
        Assert.False(probe.HasRecentRestartSceneEvidence((nint)101, 1001));
        probe.RecordRestartSceneEvidence((nint)100, null, 1002);
        Assert.False(probe.HasRecentRestartSceneEvidence((nint)100, 1003));
    }

    [Fact]
    public async Task One_recovery_cannot_close_or_launch_twice()
    {
        var state = new LaunchFlow.LobbyNavigationState(); int closes = 0, launches = 0;
        Task Close(CancellationToken _) { closes++; return Task.CompletedTask; }
        Task<GameWindowInfo> Launch(CancellationToken _) { launches++; return Task.FromResult(NewWindow); }
        Assert.Equal(NewWindow, await LaunchFlow.RestartNetworkOnceAsync(state, () => Task.FromResult(true), Close, Launch, default));
        await Assert.ThrowsAsync<StepFailedException>(() => LaunchFlow.RestartNetworkOnceAsync(state, () => Task.FromResult(true), Close, Launch, default));
        Assert.Equal(1, closes); Assert.Equal(1, launches);
    }

    [Fact]
    public async Task Unknown_or_unsafe_scene_does_not_close_anything()
    {
        var state = new LaunchFlow.LobbyNavigationState(); int closes = 0, launches = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => LaunchFlow.RestartNetworkOnceAsync(state, () => Task.FromResult(false),
            _ => { closes++; return Task.CompletedTask; }, _ => { launches++; return Task.FromResult(NewWindow); }, default));
        Assert.Equal(0, closes); Assert.Equal(0, launches); Assert.False(state.NetworkRestartUsed);
    }

    [Fact]
    public async Task Stop_requested_during_close_prevents_relaunch()
    {
        using var stop = new CancellationTokenSource(); int launches = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LaunchFlow.RestartNetworkOnceAsync(
            new(), () => Task.FromResult(true), _ => { stop.Cancel(); return Task.CompletedTask; },
            _ => { launches++; return Task.FromResult(NewWindow); }, stop.Token));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Old_process_that_did_not_exit_prevents_a_second_instance()
    {
        int closes = 0, kills = 0, launches = 0;
        await Assert.ThrowsAsync<StepFailedException>(() => LaunchFlow.RestartNetworkOnceAsync(new(), () => Task.FromResult(true),
            token => GameProcessBrick.CloseWithCancellationAsync(() => closes++, (_, _) => Task.FromResult(false), () => kills++, token),
            _ => { launches++; return Task.FromResult(NewWindow); }, default));
        Assert.Equal(1, closes); Assert.Equal(1, kills); Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Stop_during_grace_period_prevents_forced_kill()
    {
        using var stop = new CancellationTokenSource(); int kills = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameProcessBrick.CloseWithCancellationAsync(() => { },
            (_, _) => { stop.Cancel(); return Task.FromResult(false); }, () => kills++, stop.Token));
        Assert.Equal(0, kills);
    }

    [Fact]
    public async Task Failed_relaunch_does_not_reset_the_restart_budget()
    {
        var state = new LaunchFlow.LobbyNavigationState(); int closes = 0;
        await Assert.ThrowsAsync<IOException>(() => LaunchFlow.RestartNetworkOnceAsync(state, () => Task.FromResult(true),
            _ => { closes++; return Task.CompletedTask; }, _ => throw new IOException("启动器不可用"), default));
        await Assert.ThrowsAsync<StepFailedException>(() => LaunchFlow.RestartNetworkOnceAsync(state, () => Task.FromResult(true),
            _ => { closes++; return Task.CompletedTask; }, _ => Task.FromResult(NewWindow), default));
        Assert.Equal(1, closes);
    }
}
