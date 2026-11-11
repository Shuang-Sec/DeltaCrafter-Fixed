using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class DeepObservedIdentityTests : IDisposable
{
    private sealed class Clock : IClock { public DateTimeOffset Now => new(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(8)); }
    private sealed class Catalog : ICatalogLookup { public string? ResolveDisplayName(FacilityKey key, string text) => null; }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "observed-identity-" + Guid.NewGuid());
    private readonly Clock _clock = new();
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData(FacilityPhase.Crafting)]
    [InlineData(FacilityPhase.ReadyToCollect)]
    [InlineData(FacilityPhase.Idle)]
    public void Unread_item_is_not_replaced_with_a_future_recipe_when_observation_is_persisted(FacilityPhase phase)
    {
        RecordAndAssert(phase, "", "");
    }

    [Fact]
    public void Visible_current_item_survives_a_different_future_recipe()
    {
        RecordAndAssert(FacilityPhase.Crafting, "视点3倍瞄准镜", "视点3倍瞄准镜");
    }

    private void RecordAndAssert(FacilityPhase phase, string observedName, string expectedName)
    {
        Directory.CreateDirectory(_dir);
        string statePath = Path.Combine(_dir, "state.json");
        var engine = new ScheduleEngine(new JsonStoreBrick(), statePath, _clock, _log);
        var config = CraftPlanConfig.CreateDefault();
        var nextPlan = config.For(FacilityKey.TechCenter);
        nextPlan.ItemName = "SVD狙击步枪";
        using var coordinator = new AutomationCoordinator(null!, null!, null!, null!, null!, null!, null!, new Catalog(), null!, null!,
            engine, null!, null!, null!, () => new AppSettings(), () => config, null!, _clock, _log);
        TimeSpan? remaining = phase == FacilityPhase.Crafting ? TimeSpan.FromHours(1) : null;
        coordinator.RecordFinalObservation(nextPlan, new FacilityObservation(phase, remaining, observedName, observedName));
        var reloaded = new JsonStoreBrick().Load<ScheduleState>(statePath).For(FacilityKey.TechCenter);
        Assert.Equal(expectedName, reloaded.ItemName);
        Assert.Equal(phase, reloaded.Phase);
        Assert.Equal(remaining is { } duration ? _clock.Now + duration : (DateTimeOffset?)null, reloaded.ReadyAt);
        Assert.Equal("SVD狙击步枪", nextPlan.ItemName);
    }

    public void Dispose()
    {
        (_log as IDisposable)?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
