using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L2;

/// <summary>进入特勤处(点大厅顶栏「特勤处」页签)。四个设施槽位在特勤处总览一屏可见,
/// 无需再逐设施导航——观察与操作都从总览页出发。</summary>
public sealed class SpecOpsNavFlow
{
    private readonly ScreenProbe _probe;
    private readonly StepRunner _runner;

    public SpecOpsNavFlow(ScreenProbe probe, StepRunner runner)
    {
        _probe = probe;
        _runner = runner;
    }

    public async Task EnterSpecOpsAsync(nint hwnd, CancellationToken ct)
    {
        if (await _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome)) return; // 游戏恢复在特勤处页

        if (!await _probe.ClickSpecOpsEntryAsync(hwnd, ct))
            throw new StepFailedException("进入特勤处",
                "大厅当前帧未识别到唯一“特勤处”顶栏标签，未点击旧固定坐标。");

        // 点击已经由同一帧 OCR 完成；后续只等待并验证页面，避免校验超时后再次点入错误页面。
        await _runner.RunAsync(hwnd, new Step(
            "等待特勤处",
            () => { },
            () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
            TimeSpan.FromSeconds(15), RetryOnce: false), ct);
    }
}
