using System.Diagnostics;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1.Win32;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>
/// 游戏进程启停。只负责"拉起可执行文件"与"按窗口句柄关闭所属进程",
/// 是否到达大厅由流程层用画面判定,这里不猜测启动器的中间过程。
/// </summary>
public sealed class GameProcessBrick
{
    private readonly ILogger _log;

    public GameProcessBrick(ILogger log) => _log = log.ForContext<GameProcessBrick>();

    /// <summary>启动游戏/启动器。路径未配置或不存在直接抛错——这类配置错误必须暴露给用户。</summary>
    public void Launch(string gamePath)
    {
        ValidateLaunchPath(gamePath);

        var psi = new ProcessStartInfo
        {
            FileName = gamePath,
            WorkingDirectory = Path.GetDirectoryName(gamePath)!,
            UseShellExecute = true,
        };
        Process.Start(psi);
        _log.Information("已启动游戏:{Path}", gamePath);
    }

    internal static void ValidateLaunchPath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            throw new InvalidOperationException("尚未设置游戏路径,请先到「设置」页选择游戏可执行文件。");
        if (!File.Exists(gamePath))
            throw new InvalidOperationException($"游戏路径不存在:{gamePath}");

    }

    /// <summary>恢复重连专用：限定窗口所属游戏进程，停止请求可取消等待并阻止强退/重拉。</summary>
    internal async Task CloseForNetworkRecoveryAsync(nint hwnd, int expectedPid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Process process;
        try { process = Process.GetProcessById(expectedPid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint actualPid);
            if (actualPid != expectedPid || process.ProcessName != "DeltaForceClient-Win64-Shipping")
                throw new StepFailedException("断线重启", "窗口与已确认的三角洲进程不一致，未关闭任何进程。");
            _ = process.SafeHandle; // 绑定当前进程实例，避免仅凭名称结束其他进程。
            await CloseWithCancellationAsync(() => process.CloseMainWindow(), async (grace, token) =>
            {
                try { await process.WaitForExitAsync(token).WaitAsync(grace, token); return true; }
                catch (TimeoutException) { return false; }
            }, () => process.Kill(entireProcessTree: true), ct);
        }
        _log.Information("断线恢复：原游戏进程已确认退出。");
    }

    internal static async Task CloseWithCancellationAsync(Action requestClose,
        Func<TimeSpan, CancellationToken, Task<bool>> waitForExit, Action forceClose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        requestClose();
        if (await waitForExit(TimeSpan.FromSeconds(20), ct)) return;
        ct.ThrowIfCancellationRequested();
        forceClose();
        if (!await waitForExit(TimeSpan.FromSeconds(5), ct))
            throw new StepFailedException("断线重启", "旧游戏进程未退出，未继续拉起第二个游戏实例。");
    }

    /// <summary>
    /// 关闭窗口所属进程:先礼后兵(CloseMainWindow → 宽限 → Kill 进程树)。
    /// 窗口/进程已不存在视为目标达成而非错误——本方法的契约是"调用后游戏不再运行"。
    /// </summary>
    public void CloseByWindow(nint hwnd, TimeSpan grace)
    {
        NativeWindowApi.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            _log.Information("关闭游戏:窗口已不存在,视为已关闭。");
            return;
        }

        Process proc;
        try { proc = Process.GetProcessById((int)pid); }
        catch (ArgumentException)
        {
            _log.Information("关闭游戏:进程 {Pid} 已退出。", pid);
            return;
        }

        using (proc)
        {
            proc.CloseMainWindow();
            if (!proc.WaitForExit((int)grace.TotalMilliseconds))
            {
                _log.Warning("游戏未在 {Grace}s 内响应关闭请求,强制结束进程树。", grace.TotalSeconds);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            _log.Information("游戏已关闭。");
        }
    }
}
