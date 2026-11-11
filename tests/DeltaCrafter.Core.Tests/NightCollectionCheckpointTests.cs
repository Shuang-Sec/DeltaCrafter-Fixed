using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class NightCollectionCheckpointTests : IDisposable
{
    private sealed class Clock : IClock { public DateTimeOffset Now => new(2026,10,26,23,13,0,TimeSpan.FromHours(8)); }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "collected-checkpoint-" + Guid.NewGuid());
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private readonly AutomationCoordinator _coordinator;
    private readonly string _path;
    public NightCollectionCheckpointTests()
    {
        Directory.CreateDirectory(_dir); _path = Path.Combine(_dir, "state.json");
        var clock = new Clock(); var engine = new ScheduleEngine(new JsonStoreBrick(), _path, clock, _log);
        engine.RecordObservation(FacilityKey.PharmacyLab, FacilityPhase.ReadyToCollect, "体能强化剂", clock.Now.AddHours(-1), null);
        _coordinator = new AutomationCoordinator(null!,null!,null!,null!,null!,null!,null!,null!,null!,null!,
            engine,null!,null!,null!,() => new AppSettings(),CraftPlanConfig.CreateDefault,null!,clock,_log);
    }

    [Fact]
    public async Task Collected_item_is_saved_as_idle_before_later_warehouse_failure()
    {
        await Assert.ThrowsAsync<WarehouseFullException>(() => _coordinator.CompleteCollectionAsync(FacilityKey.PharmacyLab,
            () => Task.CompletedTask, () =>
            {
                AssertIdle();
                throw new WarehouseFullException("战地医疗箱补料");
            }, default));
        AssertIdle();
    }

    [Fact]
    public async Task Cancellation_after_a_successful_collection_preserves_the_fact_and_skips_crafting()
    {
        using var cancellation = new CancellationTokenSource(); int starts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _coordinator.CompleteCollectionAsync(FacilityKey.PharmacyLab,
            () => { cancellation.Cancel(); return Task.CompletedTask; },
            () => { starts++; return Task.CompletedTask; }, cancellation.Token));
        AssertIdle(); Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Failed_collection_does_not_clear_the_existing_reward()
    {
        int starts = 0;
        await Assert.ThrowsAsync<WarehouseFullException>(() => _coordinator.CompleteCollectionAsync(FacilityKey.PharmacyLab,
            () => throw new WarehouseFullException("领取"), () => { starts++; return Task.CompletedTask; }, default));
        Assert.Equal(0, starts);
        var saved = Reload(); Assert.Equal(FacilityPhase.ReadyToCollect, saved.Phase); Assert.Equal("体能强化剂", saved.ItemName);
    }

    [Fact]
    public async Task Disk_failure_after_collection_does_not_continue_to_purchase_or_start()
    {
        int starts = 0;
        using (var exclusive = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Record.ExceptionAsync(() => _coordinator.CompleteCollectionAsync(FacilityKey.PharmacyLab,
                () => Task.CompletedTask, () => { starts++; return Task.CompletedTask; }, default));
            // Windows原子替换已锁文件可能返回共享冲突或拒绝访问；两者都必须阻止后续动作。
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        Assert.Equal(0, starts);
    }

    private FacilityRuntime Reload() => new JsonStoreBrick().Load<ScheduleState>(_path).For(FacilityKey.PharmacyLab);
    private void AssertIdle() { var saved = Reload(); Assert.Equal(FacilityPhase.Idle, saved.Phase); Assert.Empty(saved.ItemName); Assert.Null(saved.ReadyAt); }
    public void Dispose() { _coordinator.Dispose(); (_log as IDisposable)?.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
