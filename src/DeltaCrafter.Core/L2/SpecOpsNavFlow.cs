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

    private const int MaxEntryAttempts = 3;

    public async Task EnterSpecOpsAsync(nint hwnd, CancellationToken ct)
    {
        bool clicked = await TryEnterWithRetryAsync(
            () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
            () => _probe.ClickSpecOpsEntryAsync(hwnd, ct),
            (ms, token) => Task.Delay(ms, token),
            ct);
        if (!clicked)
            return; // 已处于特勤处总览页

        // 点击已经由同一帧 OCR 完成；后续只等待并验证页面，避免校验超时后再次点入错误页面。
        await _runner.RunAsync(hwnd, new Step(
            "等待特勤处",
            () => { },
            () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
            TimeSpan.FromSeconds(15), RetryOnce: false), ct);
    }

    /// <summary>
    /// 进入特勤处前最多尝试 3 帧定位顶栏「特勤处」标签（吸收弹窗刚关闭时的顶栏渐入动画），
    /// 返回 false 表示已在特勤处总览页无需等待，返回 true 表示已成功点击，全部未命中则抛错且不点旧坐标。
    /// </summary>
    internal static async Task<bool> TryEnterWithRetryAsync(
        Func<Task<bool>> isAlreadySpecOps,
        Func<Task<bool>> tryClickEntry,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxEntryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await isAlreadySpecOps()) return false;
            if (await tryClickEntry()) return true;
            if (attempt < MaxEntryAttempts)
                await delay(400, ct);
        }
        throw new StepFailedException("进入特勤处",
            "大厅当前帧未识别到唯一“特勤处”顶栏标签，未点击旧固定坐标。");
    }
}
