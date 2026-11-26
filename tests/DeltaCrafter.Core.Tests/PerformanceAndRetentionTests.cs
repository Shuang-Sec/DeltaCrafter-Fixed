using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class PerformanceAndRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shot-retention-" + Guid.NewGuid());

    [Fact]
    public void Cropped_contrast_enhancement_matches_full_frame_enhancement_pixel_for_pixel()
    {
        const int w = 16, h = 12;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)((i * 13) % 256);
            pixels[i + 1] = (byte)((i * 29) % 256);
            pixels[i + 2] = (byte)((i * 47) % 256);
            pixels[i + 3] = 255;
        }
        var frame = new CapturedFrame(w, h, pixels);
        var roi = new NRect { X = 0.25, Y = 0.25, W = 0.5, H = 0.5 };
        var (cx, cy, cw, ch) = PixelMapper.ToPixelRect(roi, w, h);

        var fullEnhanced = ScreenProbe.EnhanceTimerContrast(frame, [roi], 110);
        var croppedEnhanced = ScreenProbe.CropAndEnhanceContrast(frame, roi, 110);

        Assert.Equal(cw, croppedEnhanced.Width);
        Assert.Equal(ch, croppedEnhanced.Height);
        byte[] expectedResampled = OcrBrick.NearestResample(fullEnhanced, cx, cy, cw, ch, cw * 2, ch * 2);
        byte[] actualResampled = OcrBrick.NearestResample(croppedEnhanced, 0, 0, cw, ch, cw * 2, ch * 2);
        Assert.Equal(expectedResampled, actualResampled);
    }

    [Fact]
    public void Unanimous_slot_name_short_circuit_requires_three_mutually_agreeing_readings()
    {
        Assert.True(ScreenProbe.IsUnanimousSlotName(["M2 肌 肉 注 射 剂", "M2肌肉注射剂", "M2 肌肉注射剂"]));
        Assert.True(ScreenProbe.IsUnanimousSlotName(["4 ． 6 × 30 mm AP SX", "4 、 6 × 3 囗 mm AP SX", "4.6x30mm AP SX"]));
        Assert.False(ScreenProbe.IsUnanimousSlotName(["5.45×39mm BS", "5.45×39mm BS", "5.45×39mm BS ST+"]));
        Assert.False(ScreenProbe.IsUnanimousSlotName(["战地医疗箱", "", "战地医疗箱"]));
        Assert.False(ScreenProbe.IsUnanimousSlotName(["a", "a", "a"]));
    }

    [Fact]
    public void Idle_monitor_restart_safe_screens_match_restart_safety_policy()
    {
        Assert.Equal(
            [AnchorKeys.Lobby, AnchorKeys.SpecOpsHome, AnchorKeys.ModeSelect, AnchorKeys.Safehouse],
            ScreenProbe.RestartSafeScreens);
        foreach (string screen in ScreenProbe.RestartSafeScreens)
            Assert.True(ScreenProbe.IsRecentRestartSafeScene(screen, 1000));
    }

    [Fact]
    public void Shot_retention_prunes_by_group_count_and_age_while_keeping_paired_artifacts_and_ignoring_unrelated_files()
    {
        Directory.CreateDirectory(_dir);
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        void CreateGroup(string stem, int daysAgo, bool withCrop = false)
        {
            var stamp = now.AddDays(-daysAgo).UtcDateTime;
            foreach (string ext in withCrop ? new[] { ".png", ".txt", ".crop.png", ".json" } : new[] { ".png", ".txt" })
            {
                string path = Path.Combine(_dir, stem + ext);
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, stamp);
            }
        }

        CreateGroup("20261126-100000-fail-a", daysAgo: 0, withCrop: true); // newest -> keep
        CreateGroup("20261125-100000-fail-b", daysAgo: 1);                // 2nd -> keep
        CreateGroup("20261124-100000-fail-c", daysAgo: 2);                // 3rd -> exceeds maxGroups=2 -> prune
        CreateGroup("20261101-100000-fail-old", daysAgo: 25);             // too old -> prune

        // Unrelated file (not .png/.txt/.json) must never be deleted.
        string unrelated = Path.Combine(_dir, "notes.md");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(unrelated, now.AddDays(-100).UtcDateTime);

        int deleted = ShotRetention.Prune(_dir, now, maxRetainedGroups: 2, maxAgeDays: 14);

        Assert.Equal(4, deleted); // fail-c (.png,.txt) + fail-old (.png,.txt)
        Assert.True(File.Exists(Path.Combine(_dir, "20261126-100000-fail-a.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "20261126-100000-fail-a.crop.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "20261126-100000-fail-a.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "20261125-100000-fail-b.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "20261124-100000-fail-c.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "20261124-100000-fail-c.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "20261101-100000-fail-old.png")));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
