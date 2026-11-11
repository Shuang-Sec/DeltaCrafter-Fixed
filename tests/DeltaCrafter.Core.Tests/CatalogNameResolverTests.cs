using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class CatalogNameResolverTests
{
    [Fact]
    public void Default_catalog_preserves_the_real_bt_observation_and_distinct_suffixes()
    {
        var catalog = new JsonStoreBrick().Load<ItemCatalog>(
            Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));
        var items = catalog.For(FacilityKey.Workbench);

        Assert.Null(CatalogNameResolver.Resolve(items, "5．45×39mmBT"));
        Assert.Equal("5.45×39mm BS", CatalogNameResolver.Resolve(items, "5.45×39mm BS"));
        Assert.Equal("5.45×39mm BS ST+", CatalogNameResolver.Resolve(items, "5.45×39mm BS ST+"));
        Assert.Equal("5.8×42mm DVC12", CatalogNameResolver.Resolve(items, "5.8×42mm DVC12"));
        Assert.Equal("5.8×42mm DVC12+P", CatalogNameResolver.Resolve(items, "5.8×42mm DVC12+P"));
    }

    [Fact]
    public void Real_bt_observation_is_not_renamed_to_the_only_catalog_bs_variant()
    {
        var items = new[] { new CatalogItem { Name = "5.45×39mm BS" } };

        Assert.Null(CatalogNameResolver.Resolve(items, "5．45×39mmBT"));
    }

    [Theory]
    [InlineData("5.45×39mm BS ST+", "5.45×39mm BS")]
    [InlineData("5.45×39mm BS", "5.45×39mm BS ST+")]
    [InlineData("5.8×42mm DVC12+P", "5.8×42mm DVC12")]
    [InlineData("5.8×42mm DVC12", "5.8×42mm DVC12+P")]
    [InlineData("WARRIOR3S战术手电", "WARRIOR3战术手电")]
    [InlineData("体能激活针", "体能强化针")]
    [InlineData("体能激活针", "体能激活剂")]
    public void Readable_name_outside_catalog_keeps_its_identity(string catalogName, string observed)
    {
        Assert.Null(CatalogNameResolver.Resolve([new() { Name = catalogName }], observed));
    }

    [Theory]
    [InlineData("5.45×39mm BS", "5．45x39mmBS")]
    [InlineData("7.62×39mm AP", "7毛2x39mmAP")]
    [InlineData("体能激活针", "体能氵敫活针")]
    [InlineData("视点3倍瞄准镜", "视点3倍喵准镜")]
    public void Verified_ocr_confusions_keep_working(string catalogName, string observed)
    {
        Assert.Equal(catalogName,
            CatalogNameResolver.Resolve([new() { Name = catalogName }], observed));
    }

    [Fact]
    public void Explicit_ocr_alias_can_resolve_to_the_display_name()
    {
        var item = new CatalogItem { Name = "规范显示名", Ocr = "经过确认的扫描原文" };

        Assert.Equal(item.Name, CatalogNameResolver.Resolve([item], item.Ocr));
    }

    [Fact]
    public void Scanned_ocr_alias_with_an_explicit_quantity_suffix_remains_usable()
    {
        var item = new CatalogItem { Name = "规范显示名", Ocr = "5.45×39mm BT *180" };

        Assert.Equal(item.Name, CatalogNameResolver.Resolve([item], "5.45×39mm BT"));
    }

    [Fact]
    public void Conflicting_exact_ocr_aliases_are_ambiguous()
    {
        CatalogItem[] items =
        [
            new() { Name = "物品甲", Ocr = "相同扫描原文" },
            new() { Name = "物品乙", Ocr = "相同扫描原文" },
        ];

        Assert.Null(CatalogNameResolver.Resolve(items, "相同扫描原文"));
    }

    [Fact]
    public void Repeated_catalog_entries_with_the_same_display_name_are_not_ambiguous()
    {
        CatalogItem[] items = [new() { Name = "体能激活针" }, new() { Name = "体能激活针" }];

        Assert.Equal("体能激活针", CatalogNameResolver.Resolve(items, "体能激活针"));
    }
}
