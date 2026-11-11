using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;

namespace DeltaCrafter.Core.L2;

public sealed partial class ScreenProbe
{
    /// <summary>
    /// 藏品/容量页面都带左下角「Esc 返回」。分类栏会随客户端版本变化，
    /// 因此同时接受当前实机看到的「收藏室/常规/限定」布局和旧版分类栏布局。
    /// </summary>
    internal static bool IsCollectionMenuText(string header, string footer)
    {
        string top = Normalize(header);
        string bottom = Normalize(footer).ToUpperInvariant();
        string[] categories = ["武器", "近战", "挂饰", "载具", "道具", "特殊物品"];
        bool oldCategoryBar = categories.All(label => top.Contains(label, StringComparison.Ordinal));
        bool currentCollectionHeader = top.Contains("收藏室", StringComparison.Ordinal)
            && (top.Contains("常规", StringComparison.Ordinal)
                || top.Contains("限定", StringComparison.Ordinal)
                || top.Contains("收藏柜", StringComparison.Ordinal));
        return (oldCategoryBar || currentCollectionHeader)
            && HasEscapeReturnHint(bottom);
    }

    /// <summary>
    /// 「增加容量」页面是收藏入口下的另一层安全返回页。它没有稳定的分类栏，
    /// 但实机同时显示「增加容量」和「武器架容量」等唯一文案。
    /// </summary>
    internal static bool IsCollectionCapacityText(string content, string footer)
    {
        string body = Normalize(content);
        string bottom = Normalize(footer).ToUpperInvariant();
        bool capacityMarker = body.Contains("增加容量", StringComparison.Ordinal)
            && body.Contains("武器架容量", StringComparison.Ordinal);
        return capacityMarker && HasEscapeReturnHint(bottom);
    }

    private static bool HasEscapeReturnHint(string footer)
    {
        // 实机 OCR 偶尔把 Esc 的末字母吞掉，允许「ES 返回」但仍要求同一左下角区域。
        return footer.Contains("返回", StringComparison.Ordinal)
            && (footer.Contains("ESC", StringComparison.Ordinal)
                || footer.Contains("ES", StringComparison.Ordinal));
    }

    internal async Task<bool> ReadCollectionMenuFromFrameAsync(CapturedFrame frame, CancellationToken ct)
    {
        // 当前 2560×1440 实机的顶部标题在 x≈0.06~0.45，
        // 收藏卡片/容量说明在左中部，底部提示比旧标定宽。
        var header = new NRect { X = .045, Y = .035, W = .52, H = .135 };
        var body = new NRect { X = .045, Y = .125, W = .82, H = .76 };
        var footer = new NRect { X = .038, Y = .945, W = .095, H = .055 };
        foreach (double scale in new[] { 2.0, 1.5, 1.0 })
        {
            ct.ThrowIfCancellationRequested();
            string top = (await _ocr.ReadAsync(frame, header, scale)).FullText;
            string content = (await _ocr.ReadAsync(frame, body, scale)).FullText;
            string bottom = (await _ocr.ReadAsync(frame, footer, scale)).FullText;
            ct.ThrowIfCancellationRequested();
            if (IsCollectionMenuText(top + "\n" + content, bottom)
                || IsCollectionCapacityText(top + "\n" + content, bottom))
            {
                _log.Debug("界面判定 collection-menu:true(标题:{Header},底部:{Footer})",
                    Compact(top), Compact(bottom));
                return true;
            }
        }
        _log.Debug("界面判定 collection-menu:false");
        return false;
    }
}
