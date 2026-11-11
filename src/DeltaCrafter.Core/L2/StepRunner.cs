using DeltaCrafter.Core.L0;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>一个自动化步骤 = 动作 + 后置校验 + 超时。校验函数必须基于新截帧判断。</summary>
public sealed record Step(
    string Name,
    Action Act,
    Func<Task<bool>> Verify,
    TimeSpan Timeout,
    bool RetryOnce = true);

/// <summary>
/// 步骤执行器,唯一的失败出口。策略:
/// 动作后轮询校验直至超时;允许一次显式重试(记 Warning);再失败即保存现场
/// (截图 + OCR 转储)并抛 StepFailedException 中止本轮。没有静默续跑路径。
/// </summary>
public sealed class StepRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private readonly ScreenProbe _probe;
    private readonly ILogger _log;

    public StepRunner(ScreenProbe probe, ILogger log)
    {
        _probe = probe;
        _log = log.ForContext<StepRunner>();
    }

    public async Task RunAsync(nint hwnd, Step step, CancellationToken ct)
    {
        int attempts = step.RetryOnce ? 2 : 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await StopOnNetworkInterruptionAsync(hwnd, ct);
            _log.Debug("执行步骤[{Name}](第 {Attempt} 次)", step.Name, attempt);
            _probe?.InvalidateRestartSceneForInput();
            step.Act();
            if (await PollVerifyAsync(hwnd, step, ct))
            {
                _log.Debug("步骤[{Name}]完成。", step.Name);
                return;
            }
            if (attempt < attempts)
                _log.Warning("步骤[{Name}]在 {Timeout}s 内未通过校验,重试一次。",
                    step.Name, step.Timeout.TotalSeconds);
        }

        string png = "", dump = "";
        try
        {
            if (_probe is not null)
                (png, dump) = await _probe.DumpAsync(hwnd, "fail-" + Sanitize(step.Name));
        }
        catch (Exception ex)
        {
            // 现场保存失败只削弱诊断,不改变"步骤已失败"这一事实。
            _log.Error(ex, "保存步骤[{Name}]失败现场时出错。", step.Name);
        }
        throw new StepFailedException(step.Name,
            $"{step.Timeout.TotalSeconds:F0}s 内未到达预期状态。诊断截图:{(png.Length > 0 ? png : "保存失败")}",
            png.Length > 0 ? png : null, dump.Length > 0 ? dump : null);
    }

    private async Task<bool> PollVerifyAsync(nint hwnd, Step step, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)step.Timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await StopOnNetworkInterruptionAsync(hwnd, ct);
            bool verified = await step.Verify();
            // OCR 本身可能不接受取消令牌；等待期间已停止时，不能继续采信返回的成功。
            ct.ThrowIfCancellationRequested();
            if (verified) return true;
            await Task.Delay(PollInterval, ct);
        }
        ct.ThrowIfCancellationRequested();
        bool finalVerified = await step.Verify();
        ct.ThrowIfCancellationRequested();
        return finalVerified;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c)));
    }

    private async Task StopOnNetworkInterruptionAsync(nint hwnd, CancellationToken ct)
    {
        if (_probe is null || !await _probe.IsNetworkReconnectVisibleAsync(hwnd, ct)) return;
        await _probe.TryHandleNetworkReconnectAsync(hwnd, ct);
        // 重连会改变页面与交易状态，不能重试原来的领取/购买/生产动作。
        throw new StepFailedException("流程中网络断开", "已发送重连确认并停止当前步骤；下一轮将重新观察工位，不重复执行旧动作。");
    }
}
