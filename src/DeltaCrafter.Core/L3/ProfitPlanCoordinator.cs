using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L3;

/// <summary>
/// 管理发布给计划页的完整行情快照与 12 小时刷新节奏。
/// 最近一次尝试和上次成功数据一同落盘，跨重启保留；网络失败不清空快照。
/// </summary>
public sealed class ProfitPlanCoordinator
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    // 保留旧核心公开成员；失败与成功现在共用刷新间隔。
    public static readonly TimeSpan FailureRetryDelay = RefreshInterval;

    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task<ProfitMarketSnapshot>> _fetch;
    private readonly JsonStoreBrick _store;
    private readonly string? _cachePath;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ProfitMarketState _state;

    public ProfitPlanCoordinator()
        : this(null, new JsonStoreBrick(), Log.Logger, TimeProvider.System, new ProfitAdvisorBrick().FetchSnapshotAsync) { }

    public ProfitPlanCoordinator(string cachePath, JsonStoreBrick store, ILogger log)
        : this(cachePath, store, log, TimeProvider.System, new ProfitAdvisorBrick().FetchSnapshotAsync) { }

    internal ProfitPlanCoordinator(string? cachePath, JsonStoreBrick store, ILogger log,
        TimeProvider clock, Func<CancellationToken, Task<ProfitMarketSnapshot>> fetch)
    {
        _cachePath = cachePath;
        _store = store;
        _log = log;
        _clock = clock;
        _fetch = fetch;
        _state = LoadState();
    }

    public ProfitMarketState State => Volatile.Read(ref _state);

    public DateTimeOffset NextAttemptAt =>
        State.LastAttemptAt is { } last ? last.Add(RefreshInterval) : _clock.GetUtcNow();

    public async Task<ProfitRecommendationSet> FetchRecommendationsAsync(CancellationToken ct) =>
        (await _fetch(ct)).Recommendations;

    public static DateTimeOffset NextRefreshAttemptAt(DateTimeOffset now, bool lastFetchSucceeded) =>
        now.Add(RefreshInterval);

    /// <summary>未到期只读缓存；返回 true 表示确实尝试了一轮，而非抓取成功。</summary>
    public async Task<bool> RefreshIfDueAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (State.LastAttemptAt is { } last && _clock.GetUtcNow() < last.Add(RefreshInterval))
                return false;
            var latest = State.Latest;
            string? error = null;
            try
            {
                var candidate = await _fetch(ct);
                if (!candidate.Recommendations.IsComplete)
                    throw new InvalidOperationException("行情不包含四个工位的完整利润排名。");
                latest = candidate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex.Message;
            }

            var state = new ProfitMarketState(_clock.GetUtcNow(), latest, error);
            if (_cachePath is not null)
            {
                try
                {
                    _store.Save(_cachePath, state);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warning(ex, "利润行情缓存未能保存到本机，本次启动仍可使用内存缓存。");
                }
            }
            Volatile.Write(ref _state, state);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private ProfitMarketState LoadState()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return new(null, null, null);
        try
        {
            var state = _store.Load<ProfitMarketState>(_cachePath);
            if (state.LastAttemptAt is not { } last || last == default || last > _clock.GetUtcNow().AddMinutes(5)
                || (state.Latest is null && string.IsNullOrWhiteSpace(state.LastError))
                || (state.Latest is { } latest &&
                    (latest.Recommendations?.IsComplete != true
                    || string.IsNullOrWhiteSpace(latest.SourceHost)
                    || latest.MarketUpdatedAt == default || latest.FetchedAt == default
                    || latest.FetchedAt > last.AddMinutes(5)
                    || latest.MarketUpdatedAt > latest.FetchedAt.AddMinutes(5))))
                throw new InvalidDataException("本地行情快照缺少数据或时间无效。");
            return state;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "未能读取本地利润行情缓存，将重新获取。");
            return new(null, null, null);
        }
    }
}
