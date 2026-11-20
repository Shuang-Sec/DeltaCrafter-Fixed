using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L3;
using Microsoft.UI.Dispatching;
using Serilog;

namespace DeltaCrafter.App.Services;

/// <summary>
/// 行情时间与缓存由核心管理，本服务将已验证的快照显示在计划页。
/// 计划写入回到 UI 线程，并等待当前制造轮结束。
/// </summary>
public sealed class ProfitPlanService
{
    private readonly AppHost _host;
    private readonly ProfitPlanCoordinator _coordinator;
    private readonly ILogger _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly ProfitPlanApplier _applier;

    public string LastStatus { get; private set; } = "";

    public ProfitPlanService(AppHost host, ProfitPlanCoordinator coordinator, ILogger log)
    {
        _host = host;
        _coordinator = coordinator;
        _log = log;
        _applier = new ProfitPlanApplier(host, log);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            bool startup = true;
            while (true)
            {
                bool attempted = await _coordinator.RefreshIfDueAsync(ct);
                var state = _coordinator.State;
                var next = _coordinator.NextAttemptAt.ToLocalTime();
                if (attempted)
                {
                    if (state.LastError is { } error)
                        _log.Warning("利润行情获取失败：{Reason}；{Cache}下次尝试 {Next:yyyy-MM-dd HH:mm:ss}。",
                            error, state.Latest is null ? "保留当前配方。" : "保留上次行情与当前配方。", next);
                    else if (state.Latest is { } latest)
                        _log.Information("利润行情已更新：{Source} 更新于 {MarketAt:yyyy-MM-dd HH:mm:ss}，"
                            + "本机读取 {FetchedAt:HH:mm:ss}；下次尝试 {Next:yyyy-MM-dd HH:mm:ss}（12 小时后）。",
                            latest.SourceHost, latest.MarketUpdatedAt.ToLocalTime(), latest.FetchedAt.ToLocalTime(), next);
                }
                else if (startup)
                {
                    _log.Information("利润行情仍在 12 小时刷新周期内，沿用本机记录；下次尝试 {Next:yyyy-MM-dd HH:mm:ss}。",
                        next);
                }

                if (startup || attempted)
                {
                    if (state.Latest is not null && state.LastError is null && HasProfitFacilities())
                        await ApplyLatestCacheBetweenRoundsAsync(targetKeys: null, ct);
                    else
                        await RunOnUiAsync(() => SetStatus(DescribeMarketState(state)));
                }
                startup = false;

                // 分段等待，电脑睡眠唤醒或本地时间改变时也会重新核对到期时间。
                var delay = _coordinator.NextAttemptAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay < TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 应用退出 */ }
        catch (Exception ex)
        {
            _log.Error(ex, "利润行情循环异常终止；重启助手可恢复。");
        }
    }

    /// <summary>切换利润模式直接使用当前快照；所有网络读取均由同一个刷新循环执行。</summary>
    public void OnFacilityModeChanged(FacilityKey key, CraftMode mode)
    {
        if (mode == CraftMode.Custom)
        {
            _log.Information("{Facility} 已切换为自定义物品，恢复原先的选择。", FacilityKeys.DisplayName(key));
            if (!HasProfitFacilities()) SetStatus("");
            else SetStatus(DescribeMarketState(_coordinator.State));
            return;
        }

        var state = _coordinator.State;
        if (state.Latest is null)
        {
            SetStatus(DescribeMarketState(state));
            return;
        }

        SetStatus($"正在应用行情缓存…\n{DescribeMarketState(state)}");
        _ = Task.Run(() => ApplyLatestCacheBetweenRoundsAsync([key], CancellationToken.None));
    }

    private bool HasProfitFacilities() => _host.Plan.Facilities.Any(f => f.Mode != CraftMode.Custom);

    private string DescribeMarketState(ProfitMarketState state)
    {
        if (state.LastAttemptAt is null) return "正在读取首次行情；取得推荐前保留当前配方。";
        string next = $"下次读取：{_coordinator.NextAttemptAt.ToLocalTime():MM-dd HH:mm}。";
        if (state.Latest is not { } latest)
            return $"当前无可用行情：{state.LastError} 保留当前配方。{next}";

        string times = $"行情来源：{latest.SourceHost}；上游 {latest.MarketUpdatedAt.ToLocalTime():MM-dd HH:mm} 更新"
            + $"，本机 {latest.FetchedAt.ToLocalTime():MM-dd HH:mm} 读取。{next}";
        return state.LastError is null
            ? times
            : $"最近刷新失败：{state.LastError} 以下为上次可用行情。\n{times}";
    }

    private async Task ApplyLatestCacheBetweenRoundsAsync(
        IReadOnlyCollection<FacilityKey>? targetKeys, CancellationToken ct)
    {
        try
        {
            await _host.Coordinator.RunBetweenRoundsAsync(
                () => RunOnUiAsync(() =>
                {
                    // 取得闸门后读取当前快照和模式，避免较早排队的操作覆盖新行情或自定义选择。
                    var state = _coordinator.State;
                    if (!HasProfitFacilities() || state.Latest is not { } latest) return;
                    string applied = _applier.Apply(latest.Recommendations, latest.FetchedAt.ToLocalTime(),
                        targetKeys, refreshError: null);
                    SetStatus(string.IsNullOrEmpty(applied)
                        ? DescribeMarketState(state)
                        : $"{DescribeMarketState(state)}\n{applied}");
                }), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "行情缓存已取得，但写入制造计划失败。");
            _dispatcher.TryEnqueue(() => SetStatus($"行情缓存已取得，但写入制造计划失败：{ex.Message}"));
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
            throw new InvalidOperationException("应用界面派发队列不可用，利润推荐未写入计划。");
        return completion.Task;
    }

    private void SetStatus(string status)
    {
        LastStatus = status;
        _host.PlanVm.NotifyProfitStatusChanged();
    }
}
