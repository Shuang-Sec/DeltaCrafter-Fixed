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

    /// <summary>
    /// 默认目录升级时按修订合并：以新版默认目录为基准（采纳最新修正的显示名与 OCR 匹配键），
    /// 同时保留本机原有目录里多出来的自定义/扫描条目，避免升级 revision 时静默丢失玩家扫描出的配方。
    /// </summary>
    internal static (ItemCatalog Merged, int PreservedCustomCount) MergeWithNewerDefault(
        ItemCatalog local, ItemCatalog newerDefault)
    {
        var merged = new ItemCatalog { Revision = newerDefault.Revision };
        int preserved = 0;

        foreach (var (facilityKey, defaultItems) in newerDefault.Facilities)
        {
            var list = defaultItems.Select(CloneItem).ToList();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list)
                RegisterCanonicalKeys(known, item);

            if (local.Facilities.TryGetValue(facilityKey, out var localItems))
            {
                foreach (var localItem in localItems)
                {
                    if (string.IsNullOrWhiteSpace(localItem.Name)) continue;
                    string nameKey = TextMatch.Canonical(localItem.Name);
                    string matchKey = TextMatch.Canonical(localItem.MatchKey);
                    if ((nameKey.Length > 0 && known.Contains(nameKey))
                        || (matchKey.Length > 0 && known.Contains(matchKey)))
                        continue;

                    list.Add(CloneItem(localItem));
                    RegisterCanonicalKeys(known, localItem);
                    preserved++;
                }
            }

            merged.Facilities[facilityKey] = list;
        }

        // 若本机存在默认表之外的额外设施键，原样保留。
        foreach (var (facilityKey, localItems) in local.Facilities)
        {
            if (merged.Facilities.ContainsKey(facilityKey)) continue;
            merged.Facilities[facilityKey] = localItems.Select(CloneItem).ToList();
            preserved += localItems.Count;
        }

        return (merged, preserved);
    }

    private static CatalogItem CloneItem(CatalogItem item) => new()
    {
        Name = item.Name,
        Ocr = item.Ocr,
        Note = item.Note,
    };

    private static void RegisterCanonicalKeys(HashSet<string> known, CatalogItem item)
    {
        string nameKey = TextMatch.Canonical(item.Name);
        if (nameKey.Length > 0) known.Add(nameKey);
        string matchKey = TextMatch.Canonical(item.MatchKey);
        if (matchKey.Length > 0) known.Add(matchKey);
    }
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
