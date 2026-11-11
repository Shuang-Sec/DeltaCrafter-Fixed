using System.Reflection;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class CompletedSlotFrameTests
{
    [Theory]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public async Task Completed_workbench_frame_provides_a_collectible_vote_at_each_production_scale(double scale)
    {
        // 2026-11-04 实际失败图：整槽 2x 读空，独立标题却稳定可读，导致五遍仅一票。
        // 夹具只遮去顶部用户信息；槽位像素和位置保持原样。
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory))
        {
            writer.WriteBytes(await File.ReadAllBytesAsync(Path.Combine(fixture, "completed-specops.png")));
            await writer.StoreAsync();
            writer.DetachStream();
        }
        memory.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(memory);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var frame = new CapturedFrame((int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData());
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(fixture, "completed-specops-anchors.json"));
        using var logger = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(null!, null!, OcrBrick.CreateSimplifiedChinese(), null!, () => anchors, "", logger);
        var spec = probe.Screen(AnchorKeys.SpecOpsHome);
        var slots = FacilityKeys.All.Select(key => spec.Roi(AnchorKeys.FacilitySlot(key))).ToArray();
        var timers = slots.Select(roi => new NRect
        {
            X=roi.X+roi.W*.18, Y=roi.Y+roi.H*.68, W=roi.W*.82, H=roi.H*.28,
        }).ToArray();
        var texts = await probe.ReadSlotRoisFromFrameAsync(frame, slots, timers, scale);
        var flow = new CollectFlow(probe, null!, null!, null!, logger);
        var classify = typeof(CollectFlow).GetMethod("Classify", BindingFlags.Instance|BindingFlags.NonPublic)!;
        var observation = (FacilityObservation)classify.Invoke(flow, new object[]{texts[slots.Length]+"\n"+texts[0]})!;

        Assert.Equal(FacilityPhase.ReadyToCollect, observation.Phase);
        Assert.Contains("BLK", observation.ItemName);
        Assert.Null(observation.Remaining);
    }
}
