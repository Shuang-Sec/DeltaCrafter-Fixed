using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>实际客户端恢复/启动决策边界的回归，不启动、还原或关闭真实窗口。</summary>
public sealed class MinimizedClientRecoveryTests
{
    private static readonly GameWindowInfo Existing = new(11, "三角洲行动", "GameWindow", 101);
    private static readonly GameWindowInfo Started = new(22, "三角洲行动", "GameWindow", 202);

    [Fact]
    public async Task Existing_client_bypasses_restore_minimized_probe_and_launcher()
    {
        var runtime = new ClientHarness();

        Assert.Same(Existing, await runtime.Run(Existing));
        Assert.Equal((0, 0, 0, 0, 0),
            (runtime.Restores, runtime.Finds, runtime.MinimizedChecks, runtime.Starts, runtime.Waits));
    }

    [Fact]
    public async Task Restore_that_leaves_a_matching_candidate_minimized_stops_before_launcher()
    {
        var runtime = new ClientHarness { RestoreAttempted = true, StillMinimized = true };

        var error = await Assert.ThrowsAsync<StepFailedException>(() => runtime.Run());

        Assert.Contains("最小化", error.Message);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(1, runtime.Finds);
    }

    [Fact]
    public async Task Still_minimized_candidate_prevents_launch_even_when_restore_did_not_attempt()
    {
        var runtime = new ClientHarness { StillMinimized = true };

        await Assert.ThrowsAsync<StepFailedException>(() => runtime.Run());

        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Waits);
    }

    [Fact]
    public async Task Successfully_restored_client_returns_without_platform_start()
    {
        var runtime = new ClientHarness { RestoreAttempted = true, FoundClient = Existing };

        Assert.Same(Existing, await runtime.Run());
        Assert.Equal(1, runtime.Waits);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.MinimizedChecks);
    }

    [Fact]
    public async Task Restored_same_name_launcher_without_a_minimized_candidate_can_start_game()
    {
        var runtime = new ClientHarness { RestoreAttempted = true };

        Assert.Same(Started, await runtime.Run());
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, runtime.MinimizedChecks);
    }

    [Fact]
    public async Task No_client_or_minimized_candidate_keeps_the_normal_start_path()
    {
        var runtime = new ClientHarness();

        Assert.Same(Started, await runtime.Run());
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(0, runtime.Waits);
    }

    [Fact]
    public async Task Cancellation_before_resolution_skips_restore_and_start()
    {
        var runtime = new ClientHarness();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Run(ct: new CancellationToken(true)));

        Assert.Equal(0, runtime.Restores);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Cancellation_during_restore_wait_prevents_start()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new ClientHarness
        {
            RestoreAttempted = true,
            Delay = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Run(ct: cancellation.Token));

        Assert.Equal(0, runtime.Finds);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Cancellation_after_restore_attempt_prevents_wait_and_start()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new ClientHarness { RestoreAttempted = true, OnRestore = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Run(ct: cancellation.Token));

        Assert.Equal(0, runtime.Waits);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Cancellation_after_client_recheck_prevents_start()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new ClientHarness { RestoreAttempted = true, OnFind = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Run(ct: cancellation.Token));

        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task Cancellation_during_minimized_probe_is_not_reported_as_restore_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new ClientHarness { StillMinimized = true, OnMinimizedCheck = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Run(ct: cancellation.Token));

        Assert.Equal(0, runtime.Starts);
    }

    private sealed class ClientHarness
    {
        internal bool RestoreAttempted, StillMinimized;
        internal GameWindowInfo? FoundClient;
        internal int Restores, Finds, MinimizedChecks, Starts, Waits;
        internal Action? OnRestore, OnFind, OnMinimizedCheck;
        internal Func<int, CancellationToken, Task> Delay = (_, _) => Task.CompletedTask;

        internal Task<GameWindowInfo> Run(GameWindowInfo? existing = null, CancellationToken ct = default) =>
            LaunchFlow.FindOrStartClientAsync(existing,
                () => { Restores++; OnRestore?.Invoke(); return RestoreAttempted; },
                () => { Finds++; OnFind?.Invoke(); return FoundClient; },
                () => { MinimizedChecks++; OnMinimizedCheck?.Invoke(); return StillMinimized; },
                _ => { Starts++; return Task.FromResult(Started); },
                (milliseconds, token) => { Waits++; return Delay(milliseconds, token); }, ct);
    }
}
