using System.Net;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>
/// 从 DFMInfo 公布的只读国服行情接口取得四个设施的两种利润排名。
/// 先校验上游行情时间，再读取完整排行；超时、缺数据或过期的答复都不发布。
/// </summary>
public sealed class ProfitAdvisorBrick
{
    private const string PrimaryHost = "dfapi1.eleven.icu";
    private const string BackupHost = "dfapi.eleven.icu";
    private static readonly TimeSpan MaxSourceAge = TimeSpan.FromHours(3);

    /// <summary>推荐分组内的网站设施键 → 本项目设施键。四个键必须全部出现。</summary>
    private static readonly (string Key, FacilityKey Facility)[] FacilityMap =
    [
        ("tech", FacilityKey.TechCenter),
        ("workbench", FacilityKey.Workbench),
        ("pharmacy", FacilityKey.PharmacyLab),
        ("armory", FacilityKey.ArmorStation),
    ];

    private readonly TimeSpan _timeout;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ProfitAdvisorBrick() : this(TimeSpan.FromSeconds(20), handlerFactory: null) { }

    /// <summary>测试可替换传输与等待时间，以复现 429、超时和故障转移。</summary>
    internal ProfitAdvisorBrick(TimeSpan timeout, Func<HttpMessageHandler>? handlerFactory,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _timeout = timeout;
        _handlerFactory = handlerFactory;
        _delay = delay ?? Task.Delay;
    }

    public async Task<ProfitRecommendationSet> FetchRecommendationsAsync(CancellationToken ct) =>
        (await FetchSnapshotAsync(ct)).Recommendations;

    /// <summary>通常读取一次状态和排行；一次失败最多追加一次尝试。</summary>
    internal async Task<ProfitMarketSnapshot> FetchSnapshotAsync(CancellationToken ct)
    {
        using var handler = _handlerFactory?.Invoke()
            ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            return await FetchHostAsync(http, PrimaryHost, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 限流属于同一服务，等待后只重试原地址。
            await _delay(TimeSpan.FromSeconds(20), ct);
            return await FetchHostAsync(http, PrimaryHost, ct);
        }
        catch (Exception ex) when (ex is TimeoutException ||
            ex is HttpRequestException httpError &&
            (httpError.StatusCode is null || (int)httpError.StatusCode.Value >= 500))
        {
            await _delay(TimeSpan.FromSeconds(2), ct);
            return await FetchHostAsync(http, BackupHost, ct);
        }
    }

    private async Task<ProfitMarketSnapshot> FetchHostAsync(HttpClient http, string host, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var status = await GetJsonAsync(http, host, "/status", cts.Token);
            var updatedAt = ParseSourceUpdatedAt(status.RootElement, DateTimeOffset.UtcNow);
            using var data = await GetJsonAsync(http, host, "/getOVData", cts.Token);
            return new ProfitMarketSnapshot(
                ParseOverviewData(data.RootElement), updatedAt, DateTimeOffset.UtcNow, host);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"行情接口 {host} 在 {_timeout.TotalSeconds:0} 秒内没有响应。");
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient http, string host, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://{host}{path}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"行情接口 {host}{path} 返回 HTTP {(int)response.StatusCode}。",
                null, response.StatusCode);

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"行情接口 {host}{path} 未返回有效 JSON。", ex);
        }
    }

    private static DateTimeOffset ParseSourceUpdatedAt(JsonElement status, DateTimeOffset now)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("status", out var result) || result.ValueKind != JsonValueKind.String
            || result.GetString() != "success"
            || !status.TryGetProperty("has_data", out var hasData) || hasData.ValueKind != JsonValueKind.True
            || !status.TryGetProperty("last_update", out var stamp) || stamp.ValueKind != JsonValueKind.Number
            || !stamp.TryGetDouble(out double seconds) || !double.IsFinite(seconds) || seconds <= 0
            || seconds > now.AddMinutes(5).ToUnixTimeSeconds())
            throw new InvalidOperationException("行情站点没有提供有效的更新时间或可用数据。");

        var updatedAt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        if (now - updatedAt > MaxSourceAge)
            throw new InvalidOperationException($"行情源数据已过期（上游 {updatedAt.ToLocalTime():MM-dd HH:mm} 更新）。");
        return updatedAt;
    }

    /// <summary>
    /// 解析 getOVData 的两套设施摘要:spData 是单次总利润最高,
    /// sphData 是每小时利润最高。两套对象的 profit 字段分别承载对应口径,
    /// 且推荐物品可以不同。任一分组缺设施或字段即拒绝整批数据。
    /// </summary>
    internal static ProfitRecommendationSet ParseOverviewData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("推荐数据缺少 data 字段,站点数据结构可能已变化。");

        var total = ParseRecommendationGroup(data, "spData", "总利润");
        var hourly = ParseRecommendationGroup(data, "sphData", "每小时利润");
        return new ProfitRecommendationSet(total, hourly);
    }

    private static IReadOnlyList<ProfitRecommendation> ParseRecommendationGroup(
        JsonElement data, string groupName, string metricName)
    {
        if (!data.TryGetProperty(groupName, out var group)
            || group.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"推荐数据缺少 {groupName}({metricName})字段,站点数据结构可能已变化。");

        var result = new List<ProfitRecommendation>(FacilityMap.Length);
        foreach (var (key, facility) in FacilityMap)
        {
            if (!group.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"{metricName}推荐缺少设施「{FacilityKeys.DisplayName(facility)}」({key}),拒绝按残缺数据改计划。");
            string itemName = entry.TryGetProperty("itemName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()?.Trim() ?? "" : "";
            if (itemName.Length == 0)
                throw new InvalidOperationException(
                    $"{metricName}推荐中「{FacilityKeys.DisplayName(facility)}」缺少物品名,站点数据结构可能已变化。");

            if (!entry.TryGetProperty("profit", out var p) || p.ValueKind != JsonValueKind.Number
                || !p.TryGetDouble(out double profit) || !double.IsFinite(profit))
                throw new InvalidOperationException(
                    $"{metricName}推荐中「{FacilityKeys.DisplayName(facility)}」缺少数值 profit 字段,站点数据结构可能已变化。");

            result.Add(new ProfitRecommendation(facility, itemName, profit));
        }
        return result.AsReadOnly();
    }
}
