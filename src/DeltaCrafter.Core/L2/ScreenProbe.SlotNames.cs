using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;

namespace DeltaCrafter.Core.L2;

public sealed partial class ScreenProbe
{
    /// <summary>只使用同帧标题区域的实读名称投票，不用制造计划或目录猜测字符和型号。</summary>
    internal static string SelectSlotName(IReadOnlyList<string> readings)
    {
        var values = readings.Select(CleanSlotName).Where(value => value.Length >= 2).ToArray();
        if (values.Length == 0) return "";
        var ranked = values.Select(value => new
        {
            Value = value, Votes = values.Count(other => TextMatch.MatchesRecipeName(value, other))
        }).OrderByDescending(entry => entry.Votes).ToArray();
        var best = ranked[0];
        // 至少三个候选对完整名称一致；其他完整名称票数相同时保留旧读数。
        if (best.Votes < 3 || ranked.Any(entry => entry.Votes == best.Votes
            && !TextMatch.MatchesRecipeName(entry.Value, best.Value))) return "";
        return best.Value;
    }

    private static string CleanSlotName(string value)
    {
        value = value.Trim().Replace('．', '.').Replace('·', '.');
        value = Regex.Replace(value, @"\s+", " ");
        value = Regex.Replace(value, @"(?<=[\p{IsCJKUnifiedIdeographs}])\s+|\s+(?=[\p{IsCJKUnifiedIdeographs}])", "");
        return Regex.Replace(value, @"\.\s+(?=[0-9])", ".");
    }

    internal async Task<string[]> ReadSlotNamesFromFrameAsync(CapturedFrame frame, IReadOnlyList<NRect> slots)
    {
        // 卡片上部标题与中央物品贴图分开，避免医疗箱的 AID 等贴图字母进入名称。
        var rois = slots.Select(slot => new NRect
        {
            X=slot.X+slot.W*.04, Y=slot.Y+slot.H*.03, W=slot.W*.92, H=slot.H*.22
        }).ToArray();
        var low = EnhanceTimerContrast(frame, rois, 100);
        var high = EnhanceTimerContrast(frame, rois, 130);
        var result = new string[slots.Count];
        for (int index=0; index<slots.Count; index++)
        {
            var roi=rois[index];
            var candidates = new List<string>();
            foreach(double scale in new[]{2.0,1.0,1.5})
                candidates.Add((await _ocr.ReadAsync(frame,roi,scale)).FullText);
            candidates.Add((await _ocr.ReadAsync(low,roi,1.5)).FullText);
            candidates.Add((await _ocr.ReadAsync(low,roi,2.0)).FullText);
            candidates.Add((await _ocr.ReadAsync(high,roi,2.0)).FullText);
            result[index]=SelectSlotName(candidates);
        }
        return result;
    }
}
