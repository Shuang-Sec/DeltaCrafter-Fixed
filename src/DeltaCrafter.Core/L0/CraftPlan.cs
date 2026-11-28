namespace DeltaCrafter.Core.L0;

/// <summary>特勤处四类制造设施。枚举值同时作为锚点/数据文件中的稳定键,不得随意改名。</summary>
public enum FacilityKey
{
    Workbench,     // 工作台
    PharmacyLab,   // 制药台
    ArmorStation,  // 防具台
    TechCenter,    // 技术中心
}

public static class FacilityKeys
{
    public static readonly FacilityKey[] All =
        [FacilityKey.Workbench, FacilityKey.PharmacyLab, FacilityKey.ArmorStation, FacilityKey.TechCenter];

    /// <summary>界面显示名。以游戏内称呼为准,校准阶段如与游戏不一致须同步修改。</summary>
    public static string DisplayName(FacilityKey key) => key switch
    {
        FacilityKey.Workbench => "工作台",
        FacilityKey.PharmacyLab => "制药台",
        FacilityKey.ArmorStation => "防具台",
        FacilityKey.TechCenter => "技术中心",
        _ => key.ToString(),
    };

    /// <summary>anchors.json / items.json 中使用的 kebab 键。与枚举一一对应,两侧同步维护。</summary>
    public static string JsonKey(FacilityKey key) => key switch
    {
        FacilityKey.Workbench => "workbench",
        FacilityKey.PharmacyLab => "pharmacy-lab",
        FacilityKey.ArmorStation => "armor-station",
        FacilityKey.TechCenter => "tech-center",
        _ => key.ToString(),
    };
}

/// <summary>
/// 单个设施的制造物品选择方式。利润模式由国服制造行情自动填充,
/// 自定义模式允许用户在计划页手选物品。
/// </summary>
public enum CraftMode
{
    Custom,
    HourlyProfit,
    TotalProfit,
}

/// <summary>
/// 单个设施的制造计划。ItemName 为显示名;MatchName 为运行期匹配名
/// (从目录选中时写入该条目的 OCR 原文),空则直接用 ItemName 匹配;
/// CustomItemName/CustomMatchName 独立保留最后一次自定义选择,利润推荐不得改写。
/// </summary>
public sealed class FacilityPlan
{
    /// <summary>利润模式下每个设施保留的最近推荐物品历史条数上限，用于跨行情轮换后仍允许自动上架刚造出的历史推荐物。</summary>
    public const int MaxRecentRecommendedItems = 5;

    public FacilityKey Key { get; set; }
    public bool Enabled { get; set; }
    /// <summary>全局开启「领取后自动上架交易行」时,是否上架本设施刚领取的非绑定产物。默认开启。</summary>
    public bool AutoSell { get; set; } = true;
    public CraftMode Mode { get; set; }
    public string ItemName { get; set; } = "";
    public string MatchName { get; set; } = "";
    public string CustomItemName { get; set; } = "";
    public string CustomMatchName { get; set; } = "";
    /// <summary>最近最多 5 轮利润推荐物品历史（最新在前，去重），跨重启保存在 plan.json 中。</summary>
    public List<string> RecentRecommendedItems { get; set; } = [];

    /// <summary>运行期在游戏列表里搜索用的名称。</summary>
    public string SearchName => MatchName.Length > 0 ? MatchName : ItemName;

    /// <summary>将一条利润推荐物品压入最近 5 轮滚动白名单（按配方规范形去重，最新在前）。</summary>
    public bool RecordRecommendedItem(string itemName)
    {
        string trimmed = itemName?.Trim() ?? "";
        if (trimmed.Length == 0) return false;

        int existingIndex = RecentRecommendedItems.FindIndex(
            x => TextMatch.MatchesRecipeName(x, trimmed));
        if (existingIndex == 0 && string.Equals(RecentRecommendedItems[0], trimmed, StringComparison.Ordinal))
            return false;

        if (existingIndex >= 0)
            RecentRecommendedItems.RemoveAt(existingIndex);
        RecentRecommendedItems.Insert(0, trimmed);
        if (RecentRecommendedItems.Count > MaxRecentRecommendedItems)
            RecentRecommendedItems.RemoveRange(MaxRecentRecommendedItems, RecentRecommendedItems.Count - MaxRecentRecommendedItems);
        return true;
    }

    public void SetCustomSelection(string itemName, string matchName)
    {
        if (Mode != CraftMode.Custom)
            throw new InvalidOperationException("只有自定义模式可以修改自定义物品。");

        ItemName = itemName;
        MatchName = matchName;
        CustomItemName = itemName;
        CustomMatchName = matchName;
    }

    /// <summary>离开自定义时封存选择;返回自定义时原子恢复显示名与 OCR 匹配名。</summary>
    public void ChangeMode(CraftMode mode)
    {
        if (Mode == mode) return;
        if (Mode == CraftMode.Custom)
        {
            CustomItemName = ItemName;
            CustomMatchName = MatchName;
        }

        Mode = mode;
        if (mode == CraftMode.Custom)
        {
            ItemName = CustomItemName;
            MatchName = CustomMatchName;
        }
    }
}

/// <summary>制造计划(持久化于 plan.json)。四个设施各一条,顺序即处理顺序。</summary>
public sealed class CraftPlanConfig
{
    public const int CurrentSchemaVersion = 3;

    /// <summary>缺失代表 v0.3.x 及更早计划;默认计划由工厂显式写入当前版本。</summary>
    public int SchemaVersion { get; set; }

    public List<FacilityPlan> Facilities { get; set; } = [];

    public FacilityPlan For(FacilityKey key) =>
        Facilities.FirstOrDefault(f => f.Key == key)
        ?? throw new InvalidOperationException($"制造计划缺少设施 {key},plan.json 已损坏或被手改出错。");

    public static CraftPlanConfig CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Facilities = FacilityKeys.All.Select(k => new FacilityPlan { Key = k }).ToList(),
    };

    /// <summary>
    /// 建立与全局计划完全隔离的执行快照。一轮开始后只消费该副本,
    /// 后台推荐或用户编辑不会让同一轮混用新旧物品与 OCR 匹配名。
    /// </summary>
    public CraftPlanConfig CreateExecutionSnapshot() => new()
    {
        SchemaVersion = SchemaVersion,
        Facilities = Facilities.Select(f => new FacilityPlan
        {
            Key = f.Key,
            Enabled = f.Enabled,
            AutoSell = f.AutoSell,
            Mode = f.Mode,
            ItemName = f.ItemName,
            MatchName = f.MatchName,
            CustomItemName = f.CustomItemName,
            CustomMatchName = f.CustomMatchName,
            RecentRecommendedItems = [.. f.RecentRecommendedItems],
        }).ToList(),
    };
}
