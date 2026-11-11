namespace DeltaCrafter.Core.L0;

/// <summary>
/// 物品目录条目。Name 用于界面显示(可手工改成正确写法);
/// Ocr 存扫描时的识别原文,是运行期匹配键——空则退回用 Name 匹配。
/// </summary>
public sealed class CatalogItem
{
    public string Name { get; set; } = "";
    public string Ocr { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>运行期在游戏列表里搜索用的名称。</summary>
    public string MatchKey => Ocr.Length > 0 ? Ocr : Name;
}

/// <summary>
/// 可制造物品目录(items.json,键为设施 kebab 名)。仅用于制造计划页下拉候选,
/// 不参与运行判定;允许用户直接填目录之外的名称。
/// </summary>
public sealed class ItemCatalog
{
    /// <summary>默认表修订号。程序启动时若默认表比本地副本新,自动备份并替换本地副本。</summary>
    public int Revision { get; set; }
    public Dictionary<string, List<CatalogItem>> Facilities { get; set; } = [];

    public IReadOnlyList<CatalogItem> For(FacilityKey key) =>
        Facilities.TryGetValue(FacilityKeys.JsonKey(key), out var list) ? list : [];
}

/// <summary>
/// 槽位 OCR 物品名 → 目录规范显示名(纯函数)。仅接受完整名称或显式 OCR 别名，
/// 容忍已验证的同形误读；不按编辑距离猜相邻型号。目录不完整时，
/// BT 与 BS 即使只差一个字符，也必须保留读到的 BT。多项指向不同显示名时返回 null。
/// </summary>
public static class CatalogNameResolver
{
    public static string? Resolve(IReadOnlyList<CatalogItem> items, string ocrName)
    {
        string target = TextMatch.Canonical(ocrName);
        if (target.Length < 3 || items.Count == 0) return null; // 过短读数做不了可靠判定

        string? matchedName = null;
        foreach (var item in items)
        {
            bool matches = TextMatch.MatchesRecipeName(ocrName, item.Name) ||
                (item.Ocr.Length > 0 && TextMatch.MatchesRecipeName(ocrName, item.Ocr));
            if (!matches) continue;
            if (matchedName is not null && !string.Equals(matchedName, item.Name, StringComparison.Ordinal))
                return null; // 目录别名冲突时，不按条目顺序选择其中一个。
            matchedName = item.Name;
        }
        return matchedName;
    }
}
