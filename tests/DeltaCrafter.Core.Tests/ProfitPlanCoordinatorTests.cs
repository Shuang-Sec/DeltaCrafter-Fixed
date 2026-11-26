using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class ProfitPlanCoordinatorTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(AppContext.BaseDirectory, "行情测试",
        Guid.NewGuid().ToString("N"), "profit-market.json");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Refresh_attempt_is_followed_by_a_three_hour_interval(bool lastFetchSucceeded)
    {
        var now = new DateTimeOffset(
            2026, 11, 20, 22, 17, 25, TimeSpan.FromHours(8));

        var next = ProfitPlanCoordinator.NextRefreshAttemptAt(
            now, lastFetchSucceeded);

        Assert.Equal(new DateTimeOffset(2026, 11, 21, 1, 17, 25, now.Offset), next);
    }

    [Fact]
    public async Task Clean_start_reads_once_and_restart_reuses_the_snapshot_until_exactly_three_hours()
    {
        var clock = new TestClock();
        int fetches = 0;
        using var log = new LoggerConfiguration().CreateLogger();
        Task<ProfitMarketSnapshot> Fetch(CancellationToken _) =>
            Task.FromResult(Snapshot(clock.GetUtcNow(), ++fetches));

        var first = Coordinator(clock, log, Fetch);
        Assert.True(await first.RefreshIfDueAsync(default));
        Assert.Equal(1, fetches);
        var firstAttempt = first.State.LastAttemptAt!.Value;
        var due = firstAttempt.AddHours(3);
        Assert.Equal(due, first.NextAttemptAt);

        clock.Now = due.AddSeconds(-1);
        var restarted = Coordinator(clock, log, Fetch);
        Assert.False(await restarted.RefreshIfDueAsync(default));
        Assert.Equal(1, fetches);
        Assert.Equal("时均1", restarted.State.Latest!.Recommendations
            .ForFacility(FacilityKey.TechCenter, CraftMode.HourlyProfit).ItemName);
        Assert.Equal("单次1", restarted.State.Latest.Recommendations
            .ForFacility(FacilityKey.TechCenter, CraftMode.TotalProfit).ItemName);

        clock.Now = due;
        var outcomes = await Task.WhenAll(restarted.RefreshIfDueAsync(default), restarted.RefreshIfDueAsync(default));
        Assert.Single(outcomes, tried => tried);
        Assert.Equal(2, fetches);
        Assert.Equal("时均2", restarted.State.Latest!.Recommendations
            .ForFacility(FacilityKey.TechCenter, CraftMode.HourlyProfit).ItemName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_fetch_preserves_last_good_data_and_keeps_the_schedule_after_restart(bool hasPriorData)
    {
        var clock = new TestClock();
        bool failing = !hasPriorData;
        using var log = new LoggerConfiguration().CreateLogger();
        Task<ProfitMarketSnapshot> Fetch(CancellationToken _) => failing
            ? Task.FromException<ProfitMarketSnapshot>(new HttpRequestException("模拟断线"))
            : Task.FromResult(Snapshot(clock.GetUtcNow(), 1));

        var coordinator = Coordinator(clock, log, Fetch);
        if (hasPriorData)
        {
            Assert.True(await coordinator.RefreshIfDueAsync(default));
            clock.Now += TimeSpan.FromHours(3);
            failing = true;
        }

        var good = coordinator.State.Latest;
        Assert.True(await coordinator.RefreshIfDueAsync(default));
        Assert.Same(good, coordinator.State.Latest);
        Assert.Equal("模拟断线", coordinator.State.LastError);
        Assert.Equal(clock.Now.AddHours(3), coordinator.NextAttemptAt);

        var restarted = Coordinator(clock, log,
            _ => throw new InvalidOperationException("未到期不应请求"));
        Assert.False(await restarted.RefreshIfDueAsync(default));
        Assert.Equal("模拟断线", restarted.State.LastError);
        Assert.Equal(hasPriorData, restarted.State.Latest is not null);
        Assert.Equal(good?.FetchedAt, restarted.State.Latest?.FetchedAt);
    }

    [Fact]
    public async Task Broken_local_cache_is_ignored_and_rebuilt_from_complete_live_data()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        File.WriteAllText(_cachePath, "{");
        using var log = new LoggerConfiguration().CreateLogger();
        var clock = new TestClock();
        var coordinator = Coordinator(clock, log, _ => Task.FromResult(Snapshot(clock.Now, 1)));
        Assert.True(await coordinator.RefreshIfDueAsync(default));
        var saved = new JsonStoreBrick().Load<ProfitMarketState>(_cachePath);
        Assert.Equal(4, saved.Latest!.Recommendations.HourlyProfitRecommendations.Count);
        Assert.Equal(4, saved.Latest.Recommendations.TotalProfitRecommendations.Count);
    }

    private ProfitPlanCoordinator Coordinator(TestClock clock, ILogger log,
        Func<CancellationToken, Task<ProfitMarketSnapshot>> fetch) =>
        new(_cachePath, new JsonStoreBrick(), log, clock, fetch);

    private static ProfitMarketSnapshot Snapshot(DateTimeOffset now, int id) =>
        new(new ProfitRecommendationSet(
            FacilityKeys.All.Select(f => new ProfitRecommendation(f, $"单次{id}", 4000 + id)).ToArray(),
            FacilityKeys.All.Select(f => new ProfitRecommendation(f, $"时均{id}", 1000 + id)).ToArray()),
            now.AddMinutes(-20), now, "dfapi1.eleven.icu");

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_cachePath)!;
        if (!Directory.Exists(dir)) return;
        File.Delete(_cachePath);
        Directory.Delete(dir);
    }
}
