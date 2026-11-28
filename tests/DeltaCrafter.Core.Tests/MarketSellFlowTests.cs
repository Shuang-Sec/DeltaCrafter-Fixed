using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class MarketSellFlowTests
{
    private static CapturedFrame CreateSolidFrame(int width, int height, byte r, byte g, byte b)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return new CapturedFrame(width, height, bgra);
    }

    private static void SetPixel(CapturedFrame frame, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
        int o = (y * frame.Width + x) * 4;
        frame.Bgra[o] = b;
        frame.Bgra[o + 1] = g;
        frame.Bgra[o + 2] = r;
        frame.Bgra[o + 3] = 255;
    }

    private static void DrawBoundBadgeInBottomLeft(CapturedFrame frame, int tileLeft, int tileTop, int tileW, int tileH)
    {
        int x0 = tileLeft + (int)(tileW * 0.06);
        int y0 = tileTop + (int)(tileH * 0.78);
        int size = Math.Max(8, (int)(Math.Min(tileW, tileH) * 0.16));
        // 白色方框 + 左上到右下斜线（模拟游戏实机绑定禁售图标）
        for (int d = 0; d < size; d++)
        {
            SetPixel(frame, x0 + d, y0, 235, 235, 235);
            SetPixel(frame, x0 + d, y0 + size - 1, 235, 235, 235);
            SetPixel(frame, x0, y0 + d, 235, 235, 235);
            SetPixel(frame, x0 + size - 1, y0 + d, 235, 235, 235);
            SetPixel(frame, x0 + d, y0 + d, 240, 240, 240);
            SetPixel(frame, x0 + d, y0 + Math.Min(size - 1, d + 1), 230, 230, 230);
        }
    }

    [Fact]
    public void BindingBadgeDetector_detects_bottom_left_white_slashed_box_on_dark_tile()
    {
        var frame = CreateSolidFrame(200, 200, 38, 42, 52);
        DrawBoundBadgeInBottomLeft(frame, 40, 40, 86, 86);

        Assert.True(ItemBindingBadgeDetector.HasBoundBadge(frame, 40, 40, 86, 86));
    }

    [Fact]
    public void BindingBadgeDetector_returns_false_for_clean_unbound_tile()
    {
        var frame = CreateSolidFrame(200, 200, 38, 42, 52);

        Assert.False(ItemBindingBadgeDetector.HasBoundBadge(frame, 40, 40, 86, 86));
    }

    [Fact]
    public void BindingBadgeDetector_does_not_false_positive_on_uniformly_bright_white_tile()
    {
        var frame = CreateSolidFrame(200, 200, 235, 235, 235);

        Assert.False(ItemBindingBadgeDetector.HasBoundBadge(frame, 40, 40, 86, 86));
    }

    [Fact]
    public void BindingBadgeDetector_does_not_false_positive_on_saturated_colored_corner()
    {
        var frame = CreateSolidFrame(200, 200, 38, 42, 52);
        for (int y = 105; y < 120; y++)
            for (int x = 44; x < 60; x++)
                SetPixel(frame, x, y, 240, 120, 40); // 高饱和橙色，非白色绑定标志

        Assert.False(ItemBindingBadgeDetector.HasBoundBadge(frame, 40, 40, 86, 86));
    }

    [Fact]
    public void MeasureTileHighlightScore_ranks_bright_sellable_tile_above_dimmed_tile()
    {
        var dimmed = CreateSolidFrame(120, 120, 45, 48, 55);
        var bright = CreateSolidFrame(120, 120, 52, 58, 68);
        for (int y = 20; y < 80; y++)
            for (int x = 20; x < 80; x++)
                SetPixel(bright, x, y, 165, 175, 190);

        double dimmedScore = ItemBindingBadgeDetector.MeasureTileHighlightScore(dimmed, 10, 10, 90, 90);
        double brightScore = ItemBindingBadgeDetector.MeasureTileHighlightScore(bright, 10, 10, 90, 90);

        Assert.True(brightScore > dimmedScore + 30.0);
    }

    [Fact]
    public void DetectEquippedBoxTabs_discovers_main_plus_equipped_boxes_and_active_highlight()
    {
        var frame = CreateSolidFrame(1024, 576, 74, 76, 80); // 平滑空槽背景
        // 绘制槽位 0(主仓库)、1、2、3(当前白色高亮)
        for (int slot = 0; slot <= 3; slot++)
        {
            int cy = (int)Math.Round(576 * (ItemBindingBadgeDetector.BoxSlot0CenterYNorm + slot * ItemBindingBadgeDetector.BoxSlotPitchYNorm));
            int cx = (int)Math.Round(1024 * ItemBindingBadgeDetector.BoxStripCenterXNorm);
            for (int y = cy - 6; y <= cy + 6; y++)
            for (int x = cx - 6; x <= cx + 6; x++)
            {
                if (slot == 3)
                    SetPixel(frame, x, y, 220, 220, 220); // 当前高亮选中
                else if ((x + y) % 2 == 0)
                    SetPixel(frame, x, y, 140, 145, 150); // 未选中但有图标细节
                else
                    SetPixel(frame, x, y, 45, 48, 52);
            }
        }

        var tabs = ItemBindingBadgeDetector.DetectEquippedBoxTabs(frame);
        Assert.Equal(4, tabs.Count);
        Assert.Equal([0, 1, 2, 3], tabs.Select(t => t.Index).ToArray());
        Assert.False(tabs[0].IsHighlighted);
        Assert.True(tabs[3].IsHighlighted);
        Assert.True(ItemBindingBadgeDetector.IsBoxTabHighlighted(frame, tabs[3].CenterX, tabs[3].CenterY));
    }

    [Theory]
    [InlineData("交易行", true)]
    [InlineData("交 男 行", true)]
    [InlineData("特勤处", false)]
    public void SelectMarketEntry_matches_exact_or_known_ocr_typo(string text, bool shouldMatch)
    {
        var hit = ScreenProbe.SelectMarketEntry([new OcrLine(text, 400, 40)]);
        Assert.Equal(shouldMatch, hit is not null);
    }

    [Fact]
    public void SelectMarketEntryFromWords_extracts_market_tab_from_merged_topbar_line()
    {
        IReadOnlyList<OcrWordBox> words =
        [
            new("开始", 120, 20, 160, 40),
            new("游戏", 165, 20, 205, 40),
            new("仓库", 240, 20, 285, 40),
            new("交易", 520, 20, 560, 40),
            new("行", 562, 20, 580, 40),
            new("特勤处", 640, 20, 700, 40),
        ];

        var hit = ScreenProbe.SelectMarketEntryFromWords([words]);
        Assert.NotNull(hit);
        Assert.Equal(550, hit.CenterX, precision: 1);
        Assert.Equal(30, hit.CenterY, precision: 1);
    }

    [Fact]
    public void SelectMarketSellSubTabFromWords_extracts_sell_tab_from_merged_subbar()
    {
        IReadOnlyList<OcrWordBox> words =
        [
            new("购买", 90, 52, 130, 68),
            new("出售", 160, 52, 200, 68),
            new("交易记录", 230, 52, 290, 68),
        ];

        var hit = ScreenProbe.SelectMarketSellSubTabFromWords([words]);
        Assert.NotNull(hit);
        Assert.Equal(180, hit.CenterX, precision: 1);
    }

    [Theory]
    [InlineData("请选择要上架的道具", "上架数量 7/15", true)]
    [InlineData("请 选 要 上 架 的 0 目", "", true)]
    [InlineData("上架", "上架数量 0/15", true)]
    [InlineData("军需处回收", "上架数量 7/15", false)]
    [InlineData("仓库", "装备价值 0", false)]
    public void IsMarketSellPageText_validates_sell_page_and_blocks_vendor_text(string right, string left, bool expected)
    {
        Assert.Equal(expected, ScreenProbe.IsMarketSellPageText(right, left));
    }

    [Theory]
    [InlineData("上架数量 7/15", true, 7, 15)]
    [InlineData("上架数量 15 / 15", true, 15, 15)]
    [InlineData("0/8", true, 0, 8)]
    [InlineData("18/15", false, 0, 0)]
    [InlineData("0/0", false, 0, 0)]
    [InlineData("上架数量", false, 0, 0)]
    public void TryParseMarketSlotUsage_parses_valid_fractions_only(string text, bool ok, int used, int max)
    {
        Assert.Equal(ok, ScreenProbe.TryParseMarketSlotUsage(text, out int u, out int m));
        if (ok)
        {
            Assert.Equal(used, u);
            Assert.Equal(max, m);
        }
    }

    [Fact]
    public void ParseListingMeta_parses_ammo_quantity_and_slot_usage()
    {
        var (sellQty, totalQty, willUse, available) = ScreenProbe.ParseListingMeta("出售数量: 60/90   使用售位: 1/8");
        Assert.Equal(60, sellQty);
        Assert.Equal(90, totalQty);
        Assert.Equal(1, willUse);
        Assert.Equal(8, available);
    }

    [Fact]
    public void ValidateListingModal_accepts_valid_auction_house_listing_modal()
    {
        var (ok, reason) = ScreenProbe.ValidateListingModal(
            "△ 上架物品",
            "出售数量: 60/90 使用售位: 1/8",
            "4547",
            "预期收入: 237,335",
            "上架");

        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData("△ 出售", "军需处回收价 54,517", "54517", "交易行税后价 59,218", "上架", "军需处")]
    [InlineData("△ 上架物品", "出售数量: 1/1 使用售位: 1/8", "1000", "预期收入: 900", "出售", "非上架")]
    [InlineData("△ 上架物品", "出售数量: 1/1 使用售位: 1/8", "1000", "预期收入: 900", "下架", "非上架")]
    [InlineData("△ 上架物品", "该物品已绑定", "1000", "预期收入: 900", "上架", "绑定")]
    [InlineData("△ 上架物品", "该物品不可上架", "1000", "预期收入: 900", "上架", "不可上架")]
    [InlineData("△ 上架物品", "出售数量: 60/90 使用售位: 1/0", "4547", "预期收入: 237,335", "上架", "售位")]
    [InlineData("△ 上架物品", "出售数量: 60/90 使用售位: 2/1", "4547", "预期收入: 237,335", "上架", "售位")]
    [InlineData("仓库详情", "出售数量: 1/1 使用售位: 1/8", "1000", "预期收入: 900", "上架", "不是上架物品")]
    public void ValidateListingModal_rejects_vendor_bound_full_slot_or_wrong_button(
        string title, string meta, string price, string income, string button, string expectedReasonFragment)
    {
        var (ok, reason) = ScreenProbe.ValidateListingModal(title, meta, price, income, button);
        Assert.False(ok);
        Assert.Contains(expectedReasonFragment, reason);
    }

    [Theory]
    [InlineData("5.7×28mm SS190", "5.7×28mm SS190", "", 100)]
    [InlineData("精密护甲", "精密护甲维修包", "", 80)]
    [InlineData("高级护甲", "高级护甲维修组合", "", 80)]
    [InlineData("视点3倍", "视点3倍瞄准镜", "", 80)]
    [InlineData("SS190", "5.7×28mm SS190", "", 70)]
    [InlineData("精密户甲", "精密护甲维修包", "", 55)]
    [InlineData("战地医疗箱", "5.7×28mm SS190", "", 0)]
    public void ScoreMarketGridCandidate_handles_truncated_labels_without_false_matches(
        string tileText, string displayName, string matchName, int minExpectedScore)
    {
        int score = ScreenProbe.ScoreMarketGridCandidate(tileText, displayName, matchName);
        if (minExpectedScore == 0) Assert.Equal(0, score);
        else Assert.True(score >= minExpectedScore, $"Expected >= {minExpectedScore} for '{tileText}' vs '{displayName}', got {score}");
    }

    [Fact]
    public void SplitLineWordsIntoTileClusters_splits_horizontally_merged_adjacent_tiles()
    {
        // 2560px 宽画面下，同一格内词间距 ~4px，相邻格间距 ~60px (> 2560 * 0.011 = 28.16px)
        IReadOnlyList<OcrWordBox> row =
        [
            new("精密", 1680, 300, 1710, 316),
            new("护甲", 1714, 300, 1744, 316),
            new("高级", 1840, 300, 1870, 316),
            new("护甲", 1874, 300, 1904, 316),
            new("高级", 2000, 300, 2030, 316),
            new("护甲", 2034, 300, 2064, 316),
        ];

        var clusters = ScreenProbe.SplitLineWordsIntoTileClusters(row, 2560);
        Assert.Equal(3, clusters.Count);
        Assert.Equal("精密护甲", clusters[0].Text);
        Assert.Equal("高级护甲", clusters[1].Text);
        Assert.Equal("高级护甲", clusters[2].Text);
        Assert.True(clusters[1].Left > clusters[0].Right + 50);
    }

    [Fact]
    public async Task ConfirmListingOnceAsync_clicks_exactly_once_and_never_retries_on_unconfirmed_close()
    {
        int clicks = 0;
        bool result = await MarketSellFlow.ConfirmListingOnceAsync(
            () => Task.FromResult(true),
            () => clicks++,
            () => Task.FromResult(false),
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task ConfirmListingOnceAsync_never_clicks_when_precheck_fails_or_cancelled()
    {
        int clicks = 0;
        bool failedCheck = await MarketSellFlow.ConfirmListingOnceAsync(
            () => Task.FromResult(false),
            () => clicks++,
            () => Task.FromResult(true),
            CancellationToken.None);
        Assert.False(failedCheck);
        Assert.Equal(0, clicks);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MarketSellFlow.ConfirmListingOnceAsync(
                () => Task.FromResult(true),
                () => clicks++,
                () => Task.FromResult(true),
                cts.Token));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task TryEnterMarketWithRetryAsync_skips_click_when_already_on_market_sell()
    {
        int clicks = 0;
        bool clicked = await MarketSellFlow.TryEnterMarketWithRetryAsync(
            () => Task.FromResult(true),
            () => { clicks++; return Task.FromResult(true); },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.False(clicked);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task RunAutoSellStageAsync_does_not_call_market_when_master_switch_is_off()
    {
        var settings = new AppSettings { AutoSellCraftedItems = false };
        var coordinator = CreateMinimalCoordinator(settings);
        var report = new RunReport("测试");
        bool called = false;

        await coordinator.RunAutoSellStageAsync(
            (_, _, _) => { called = true; return Task.FromResult(new MarketSellBatchResult([])); },
            [new MarketSellRequest(FacilityKey.Workbench, "5.7×28mm SS190", "5.7×28mm SS190")],
            report, CancellationToken.None);

        Assert.False(called);
        Assert.DoesNotContain("交易行", report.Summary());
    }

    [Fact]
    public async Task RunAutoSellStageAsync_clamps_price_steps_and_isolates_non_cancellation_errors()
    {
        var settings = new AppSettings { AutoSellCraftedItems = true, AutoSellPriceAdjustSteps = 9 };
        var coordinator = CreateMinimalCoordinator(settings);
        var report = new RunReport("测试");
        int observedSteps = -1;

        await coordinator.RunAutoSellStageAsync(
            (items, steps, _) =>
            {
                observedSteps = steps;
                return Task.FromResult(new MarketSellBatchResult(
                [
                    new MarketSellItemOutcome(items[0], MarketSellStatus.Listed, "已上架(数量:60/150,降价3档)"),
                ]));
            },
            [new MarketSellRequest(FacilityKey.Workbench, "5.7×28mm SS190", "5.7×28mm SS190")],
            report, CancellationToken.None);

        Assert.Equal(3, observedSteps);
        Assert.Contains("交易行已上架「5.7×28mm SS190」", report.Summary());

        var report2 = new RunReport("异常隔离测试");
        await coordinator.RunAutoSellStageAsync(
            (_, _, _) => throw new StepFailedException("交易行", "模拟识别超时"),
            [new MarketSellRequest(FacilityKey.ArmorStation, "H09防暴头盔", "H09防暴头盔")],
            report2, CancellationToken.None);

        Assert.False(report2.HasFailure);
        Assert.Contains("不影响本轮制造调度", report2.Summary());
    }

    [Fact]
    public void TryCreateAutoSellRequest_blocks_manually_crafted_precious_item_that_differs_from_preset()
    {
        var plan = new FacilityPlan
        {
            Key = FacilityKey.ArmorStation,
            Enabled = true,
            AutoSell = true,
            ItemName = "重型突击背心",
            MatchName = "重型突击背心",
        };

        var req = AutomationCoordinator.TryCreateAutoSellRequest(plan, "泰坦防弹装甲", "泰坦防弹装甲", out string reason);

        Assert.Null(req);
        Assert.Contains("不一致", reason);
        Assert.Contains("保护留仓", reason);
    }

    [Theory]
    [InlineData("5.45×39mm BT", "5.45×39mm BS")]
    [InlineData("5.45×39mm BS", "5.45×39mm BS ST+")]
    [InlineData("精密护甲维修包", "高级护甲维修组合")]
    [InlineData("XCOG突击3.5倍瞄准镜", "视点3倍瞄准镜")]
    [InlineData("4.6×30mm AP SX", "")]
    public void TryCreateAutoSellRequest_rejects_sibling_models_and_unrecognized_items(string preset, string collected)
    {
        var plan = new FacilityPlan
        {
            Key = FacilityKey.Workbench,
            Enabled = true,
            AutoSell = true,
            ItemName = preset,
            MatchName = preset,
        };

        var req = AutomationCoordinator.TryCreateAutoSellRequest(plan, collected, collected, out string reason);

        Assert.Null(req);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryCreateAutoSellRequest_allows_exact_preset_match_and_carries_preset_guard_into_L2()
    {
        var plan = new FacilityPlan
        {
            Key = FacilityKey.Workbench,
            Enabled = true,
            AutoSell = true,
            ItemName = "4.6x30mm AP SX",
            MatchName = "4.6×30mm AP SX",
        };

        var req = AutomationCoordinator.TryCreateAutoSellRequest(plan, "4.6×30mm AP SX", "4.6x30mm AP SX", out string reason);

        Assert.NotNull(req);
        Assert.Equal("", reason);
        Assert.Equal("4.6x30mm AP SX", req.ExpectedPresetName);
        Assert.Equal("4.6×30mm AP SX", req.ExpectedPresetMatchName);
        Assert.True(MarketSellFlow.IsRequestAllowedByPreset(req, out _));
    }

    [Fact]
    public void TryCreateAutoSellRequest_blocks_when_facility_autoSell_is_disabled_or_preset_empty()
    {
        var selfKeep = new FacilityPlan { Key = FacilityKey.PharmacyLab, AutoSell = false, ItemName = "战地医疗箱" };
        Assert.Null(AutomationCoordinator.TryCreateAutoSellRequest(selfKeep, "战地医疗箱", "战地医疗箱", out string r1));
        Assert.Equal("", r1);

        var emptyPreset = new FacilityPlan { Key = FacilityKey.PharmacyLab, AutoSell = true, ItemName = "" };
        Assert.Null(AutomationCoordinator.TryCreateAutoSellRequest(emptyPreset, "战地医疗箱", "战地医疗箱", out string r2));
        Assert.Contains("未配置制造预设", r2);
    }

    [Fact]
    public void MarketSellFlow_IsRequestAllowedByPreset_rejects_mismatched_item_as_L2_defense_in_depth()
    {
        var mismatched = new MarketSellRequest(
            FacilityKey.TechCenter,
            DisplayName: "AWM狙击步枪",
            MatchName: "AWM狙击步枪",
            ExpectedPresetName: "XCOG突击3.5倍瞄准镜",
            ExpectedPresetMatchName: "XCOG突击3.5倍瞄准镜");

        Assert.False(MarketSellFlow.IsRequestAllowedByPreset(mismatched, out string reason));
        Assert.Contains("不一致", reason);
    }

    [Fact]
    public void IsGridRegionUnchanged_distinguishes_identical_and_scrolled_grid_frames()
    {
        var f1 = CreateSolidFrame(400, 300, 40, 44, 52);
        var f2 = CreateSolidFrame(400, 300, 40, 44, 52);
        var grid = new NRect { X = 0.60, Y = 0.15, W = 0.35, H = 0.75 };

        Assert.True(ScreenProbe.IsGridRegionUnchanged(f1, f2, grid));

        for (int y = 60; y < 220; y++)
            for (int x = 250; x < 360; x++)
                SetPixel(f2, x, y, 160, 170, 180);

        Assert.False(ScreenProbe.IsGridRegionUnchanged(f1, f2, grid));
    }

    [Fact]
    public void BuildMarketGridLabelStrips_covers_all_rows_and_any_scrolled_label_position()
    {
        var grid = new NRect { X = 0.642, Y = 0.163, W = 0.308, H = 0.744 };
        var topStrips = ScreenProbe.BuildMarketGridLabelStrips(grid, scrolled: false);
        Assert.InRange(topStrips.Count, 12, 13);
        Assert.All(topStrips, s => Assert.InRange(s.H, 0.014, 0.030));

        // 滚动后任意 y 位置处的 0.013H 高物品标签，都必须完整落在至少一条窄条带内部。
        var scrolledStrips = ScreenProbe.BuildMarketGridLabelStrips(grid, scrolled: true);
        for (double labelY = grid.Y; labelY <= grid.Y + grid.H - 0.016; labelY += 0.004)
        {
            double labelBottom = labelY + 0.013;
            Assert.Contains(scrolledStrips, s => s.Y <= labelY + 0.0001 && s.Y + s.H >= labelBottom - 0.0001);
        }
    }

    [Fact]
    public void HasStripVisualContent_skips_flat_empty_rows_and_keeps_labeled_rows()
    {
        var flat = CreateSolidFrame(400, 300, 62, 66, 72);
        var strip = new NRect { X = 0.64, Y = 0.20, W = 0.30, H = 0.03 };
        Assert.False(ScreenProbe.HasStripVisualContent(flat, strip));

        for (int x = 270; x < 310; x++)
            SetPixel(flat, x, 64, 210, 210, 210);
        Assert.True(ScreenProbe.HasStripVisualContent(flat, strip));
    }

    [Fact]
    public void InnerActionButtonRoi_insets_past_green_button_border_while_preserving_center()
    {
        var roi = new NRect { X = 0.82, Y = 0.85, W = 0.15, H = 0.062 };
        var inner = ScreenProbe.InnerActionButtonRoi(roi);

        // 2560×1440 实测亮绿边框位于 y=0.854 与 y=0.907，去边框内部 ROI 必须严格落入其内并覆盖文字中心 y≈0.880。
        Assert.True(inner.Y > 0.855);
        Assert.True(inner.Y + inner.H < 0.906);
        Assert.True(inner.Y < 0.880 && inner.Y + inner.H > 0.880);
        Assert.True(inner.X > roi.X && inner.X + inner.W < roi.X + roi.W);
    }

    [Fact]
    public void Main_warehouse_allows_deep_scroll_pages_and_expansion_box_has_bounded_pages()
    {
        Assert.True(MarketSellFlow.MaxScrollPagesForContainer(0) >= 20);
        Assert.True(MarketSellFlow.MaxScrollPagesForContainer(1) >= 10);
        Assert.True(MarketSellFlow.MaxScrollPagesForContainer(0) > MarketSellFlow.MaxScrollPagesForContainer(1));
        Assert.True(MarketSellFlow.ScrollNotchesPerPage <= -8);
    }

    private static AutomationCoordinator CreateMinimalCoordinator(AppSettings settings)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "dc-market-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var log = new LoggerConfiguration().CreateLogger();
        var store = new JsonStoreBrick();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var engine = new ScheduleEngine(store, Path.Combine(tempDir, "state.json"), clock, log);
        var windowBrick = new GameWindowBrick();
        var sleepGuard = new SleepGuardBrick(log);
        return new AutomationCoordinator(
            launch: null!, nav: null!, collect: null!, craft: null!, abort: null!, scan: null!,
            catalogSink: null!, catalogLookup: null!, windowGuard: null!, shutdown: null!,
            engine: engine, probe: null!, windowBrick: windowBrick, sleepGuard: sleepGuard,
            settings: () => settings, plan: CraftPlanConfig.CreateDefault, notifier: new NullNotifier(),
            clock: clock, log: log, setSleepActive: _ => { });
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
    }

    private sealed class NullNotifier : INotifier
    {
        public void Notify(string title, string body) { }
    }
}
