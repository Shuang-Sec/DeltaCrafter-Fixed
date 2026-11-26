using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class PersistenceAndDisplayHardeningTests : IDisposable
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 22, 0, 0, TimeSpan.FromHours(8));
    }

    private sealed class CatalogAdapter(ItemCatalog catalog) : ICatalogLookup
    {
        public string? ResolveDisplayName(FacilityKey key, string ocrName) =>
            CatalogNameResolver.Resolve(catalog.For(key), ocrName);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-persist-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();

    public PersistenceAndDisplayHardeningTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task JsonStoreBrick_handles_concurrent_saves_and_loads_without_temp_collisions()
    {
        string path = Path.Combine(_dir, "concurrent.json");
        var store = new JsonStoreBrick();
        store.Save(path, new AppSettings { FailureRetryMinutes = 1 });

        var tasks = Enumerable.Range(0, 12).Select(worker => Task.Run(() =>
        {
            var localStore = new JsonStoreBrick();
            for (int i = 1; i <= 15; i++)
            {
                localStore.Save(path, new AppSettings { FailureRetryMinutes = worker * 100 + i });
                var loaded = localStore.Load<AppSettings>(path);
                Assert.True(loaded.FailureRetryMinutes > 0);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void JsonStoreBrick_cleans_temp_file_when_target_move_fails()
    {
        // 用同名目录占位目标路径，强制触发 File.Move 失败，确认不遗留 .tmp 垃圾文件。
        string blockedTarget = Path.Combine(_dir, "occupied-target");
        Directory.CreateDirectory(blockedTarget);

        var store = new JsonStoreBrick();
        Assert.ThrowsAny<Exception>(() => store.Save(blockedTarget, new AppSettings()));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task ScheduleEngine_concurrent_mutations_and_snapshots_do_not_throw_or_tear()
    {
        string statePath = Path.Combine(_dir, "state.json");
        var clock = new FakeClock();
        var engine = new ScheduleEngine(new JsonStoreBrick(), statePath, clock, _log);
        var plan = CraftPlanConfig.CreateDefault();
        foreach (var fp in plan.Facilities) fp.Enabled = true;
        var settings = new AppSettings { RunBufferSeconds = 60, FailureRetryMinutes = 30 };

        using var cts = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                var key = FacilityKeys.All[i % FacilityKeys.All.Length];
                engine.RecordObservation(key, FacilityPhase.Crafting, $"物品{i}", clock.Now.AddHours(1), null);
                engine.MarkRunStarted();
                engine.MarkRunFinished($"第{i}轮", failed: i % 5 == 0, failureRetryMinutes: 30);
            }
            cts.Cancel();
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var facilities = engine.SnapshotFacilities();
                Assert.Equal(4, facilities.Count);
                _ = engine.SnapshotLastRunInfo();
                _ = engine.ComputeNextRunAt(plan, settings);
                _ = engine.EarliestCraftCompletionAt(plan);
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void FacilitySnapshot_cleans_legacy_persisted_garbled_item_names_for_display()
    {
        string statePath = Path.Combine(_dir, "legacy-state.json");
        var clock = new FakeClock();
        var store = new JsonStoreBrick();
        var defaultCatalog = store.Load<ItemCatalog>(Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));

        // 模拟旧版本已落盘的脏 state.json（包含框形零、空格拆字、喵准镜等历史 OCR 原文）。
        var legacy = ScheduleState.CreateDefault();
        legacy.For(FacilityKey.Workbench).Phase = FacilityPhase.Crafting;
        legacy.For(FacilityKey.Workbench).ItemName = "4 ． 6 × 3 囗 mm A P SX";
        legacy.For(FacilityKey.TechCenter).Phase = FacilityPhase.Crafting;
        legacy.For(FacilityKey.TechCenter).ItemName = "× COG 突 击 3 ． 5 倍 喵 准 镜";
        legacy.For(FacilityKey.PharmacyLab).Phase = FacilityPhase.Crafting;
        legacy.For(FacilityKey.PharmacyLab).ItemName = "5 ． 45 × 39 mm BT"; // 目录外物品：仅清洗格式不猜型号
        legacy.For(FacilityKey.ArmorStation).Phase = FacilityPhase.Idle;
        legacy.For(FacilityKey.ArmorStation).ItemName = "";
        store.Save(statePath, legacy);

        var engine = new ScheduleEngine(store, statePath, clock, _log);
        using var coordinator = new AutomationCoordinator(null!, null!, null!, null!, null!, null!, null!,
            new CatalogAdapter(defaultCatalog), null!, null!, engine, null!, null!, null!,
            () => new AppSettings(), CraftPlanConfig.CreateDefault, null!, clock, _log);

        var snapshot = coordinator.FacilitySnapshot().ToDictionary(f => f.Key);
        Assert.Equal("4.6×30mm AP SX", snapshot[FacilityKey.Workbench].ItemName);
        Assert.Equal("XCOG突击3.5倍瞄准镜", snapshot[FacilityKey.TechCenter].ItemName);
        Assert.Equal("5.45×39mm BT", snapshot[FacilityKey.PharmacyLab].ItemName);
        Assert.Equal("", snapshot[FacilityKey.ArmorStation].ItemName);
    }

    [Fact]
    public void ItemCatalog_MergeWithNewerDefault_adopts_default_updates_and_preserves_custom_entries()
    {
        var local = new ItemCatalog
        {
            Revision = 3,
            Facilities = new()
            {
                ["tech-center"] =
                [
                    new CatalogItem { Name = "OLIGHT WARRIOR 3S战术手电", Ocr = "旧匹配键" },
                    new CatalogItem { Name = "玩家扫描出的自定义配件", Ocr = "玩家扫描出的自定义配件" },
                ],
                ["workbench"] =
                [
                    new CatalogItem { Name = "4.6×30mm AP SX" },
                    new CatalogItem { Name = "5.45×39mm BT", Ocr = "5.45×39mm BT" },
                ],
            },
        };

        var newerDefault = new ItemCatalog
        {
            Revision = 4,
            Facilities = new()
            {
                ["tech-center"] =
                [
                    new CatalogItem { Name = "OLIGHT WARRIOR 3S战术手电", Ocr = "WARRIOR3S战术手电" },
                    new CatalogItem { Name = "PEQ-2红色激光镭指" },
                ],
                ["workbench"] =
                [
                    new CatalogItem { Name = "4.6×30mm AP SX" },
                    new CatalogItem { Name = ".300 BLK TAC-TX" },
                ],
            },
        };

        var (merged, preserved) = ItemCatalog.MergeWithNewerDefault(local, newerDefault);

        Assert.Equal(4, merged.Revision);
        Assert.Equal(2, preserved);
        Assert.Equal("WARRIOR3S战术手电",
            merged.For(FacilityKey.TechCenter).Single(i => i.Name == "OLIGHT WARRIOR 3S战术手电").Ocr);
        Assert.Contains(merged.For(FacilityKey.TechCenter), i => i.Name == "PEQ-2红色激光镭指");
        Assert.Contains(merged.For(FacilityKey.TechCenter), i => i.Name == "玩家扫描出的自定义配件");
        Assert.Contains(merged.For(FacilityKey.Workbench), i => i.Name == ".300 BLK TAC-TX");
        Assert.Contains(merged.For(FacilityKey.Workbench), i => i.Name == "5.45×39mm BT");
        Assert.Single(merged.For(FacilityKey.Workbench), i => i.Name == "4.6×30mm AP SX");
    }

    public void Dispose()
    {
        (_log as IDisposable)?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
