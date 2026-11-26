using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ShotStorageStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-shot-stats-" + Guid.NewGuid().ToString("N"));

    public ShotStorageStatsTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Inspect_returns_zero_for_missing_or_empty_directory()
    {
        Assert.Equal(default, ShotRetention.Inspect(Path.Combine(_dir, "non-existent")));
        Assert.Equal(default, ShotRetention.Inspect(_dir));
    }

    [Fact]
    public void Inspect_counts_only_diagnostic_files_groups_by_stem_and_sums_bytes()
    {
        File.WriteAllBytes(Path.Combine(_dir, "20261126-100000-fail-a.png"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(_dir, "20261126-100000-fail-a.txt"), new byte[256]);
        File.WriteAllBytes(Path.Combine(_dir, "20261126-100000-fail-a.crop.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(_dir, "20261126-100000-fail-a.json"), new byte[128]);
        File.WriteAllBytes(Path.Combine(_dir, "20261126-110000-fail-b.png"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(_dir, "readme.md"), new byte[999]); // 非诊断扩展名应忽略

        var stats = ShotRetention.Inspect(_dir);
        Assert.Equal(5, stats.FileCount);
        Assert.Equal(2, stats.GroupCount);
        Assert.Equal(1024 + 256 + 512 + 128 + 2048, stats.TotalBytes);
    }

    [Fact]
    public void TryPruneCount_returns_exact_number_of_removed_expired_files()
    {
        string oldPng = Path.Combine(_dir, "20260101-000000-old.png");
        string oldTxt = Path.Combine(_dir, "20260101-000000-old.txt");
        string freshPng = Path.Combine(_dir, "20261126-230000-fresh.png");
        File.WriteAllBytes(oldPng, new byte[100]);
        File.WriteAllBytes(oldTxt, new byte[50]);
        File.WriteAllBytes(freshPng, new byte[200]);

        var expired = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(oldPng, expired);
        File.SetLastWriteTimeUtc(oldTxt, expired);

        int removed = ShotRetention.TryPruneCount(_dir, maxRetainedGroups: 80, maxAgeDays: 14);
        Assert.Equal(2, removed);
        Assert.False(File.Exists(oldPng));
        Assert.False(File.Exists(oldTxt));
        Assert.True(File.Exists(freshPng));

        var after = ShotRetention.Inspect(_dir);
        Assert.Equal(1, after.FileCount);
        Assert.Equal(1, after.GroupCount);
        Assert.Equal(200, after.TotalBytes);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
