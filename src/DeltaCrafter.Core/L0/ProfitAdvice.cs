namespace DeltaCrafter.Core.L0;

/// <summary>
/// 单个设施、单一利润口径下的制造推荐。
/// Profit 的单位由所在集合决定:TotalProfitRecommendations 为单次总利润,
/// HourlyProfitRecommendations 为每小时利润;两种口径允许推荐不同物品。
/// </summary>
public sealed record ProfitRecommendation(
    FacilityKey Facility,
    string ItemName,
    double Profit);

/// <summary>
/// 同一份行情响应里的两套完整推荐。两套数据必须各自包含四个设施,
/// 调用方按每个设施当前的制造模式选择,不能从总利润物品反推每小时利润推荐。
/// </summary>
public sealed record ProfitRecommendationSet(
    IReadOnlyList<ProfitRecommendation> TotalProfitRecommendations,
    IReadOnlyList<ProfitRecommendation> HourlyProfitRecommendations)
{
    public IReadOnlyList<ProfitRecommendation> ForMode(CraftMode mode) => mode switch
    {
        CraftMode.TotalProfit => TotalProfitRecommendations,
        CraftMode.HourlyProfit => HourlyProfitRecommendations,
        _ => throw new InvalidOperationException("自定义制造模式没有自动利润推荐。"),
    };

    public ProfitRecommendation ForFacility(FacilityKey facility, CraftMode mode) =>
        ForMode(mode).Single(r => r.Facility == facility);

    internal bool IsComplete =>
        IsCompleteGroup(TotalProfitRecommendations) && IsCompleteGroup(HourlyProfitRecommendations);

    private static bool IsCompleteGroup(IReadOnlyList<ProfitRecommendation>? group) =>
        group is not null && group.Count == FacilityKeys.All.Length
        && group.All(r => r is not null && !string.IsNullOrWhiteSpace(r.ItemName) && double.IsFinite(r.Profit))
        && FacilityKeys.All.All(key => group.Count(r => r.Facility == key) == 1);
}

/// <summary>以同一份快照保存推荐、上游更新时间与本机读取时间。</summary>
public sealed record ProfitMarketSnapshot(
    ProfitRecommendationSet Recommendations,
    DateTimeOffset MarketUpdatedAt,
    DateTimeOffset FetchedAt,
    string SourceHost);

/// <summary>本地行情缓存；失败也记录尝试时间，避免重启重复请求。</summary>
public sealed record ProfitMarketState(
    DateTimeOffset? LastAttemptAt,
    ProfitMarketSnapshot? Latest,
    string? LastError);
