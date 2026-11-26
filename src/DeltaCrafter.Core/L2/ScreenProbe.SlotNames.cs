using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;

namespace DeltaCrafter.Core.L2;

public sealed partial class ScreenProbe
{
    /// <summary>只使用同帧标题区域的实读名称投票，不用制造计划或目录猜测字符和型号。</summary>
    internal static string SelectSlotName(IReadOnlyList<string> readings)
    {
        var entries = readings
            .Select(raw => (Raw: raw, Clean: CleanSlotName(raw)))
            .Where(entry => entry.Clean.Length >= 2)
            .ToArray();
        if (entries.Length == 0) return "";
        var ranked = entries.Select(entry => new
        {
            Value = entry.Clean,
            Votes = entries.Count(other => TextMatch.MatchesRecipeName(entry.Clean, other.Clean)),
            ExactVotes = entries.Count(other => string.Equals(entry.Clean, other.Clean, StringComparison.Ordinal)),
            Penalty = RawOcrArtifactPenalty(entry.Raw),
        })
        .OrderByDescending(entry => entry.Votes)
        .ThenBy(entry => entry.Penalty)
        .ThenByDescending(entry => entry.ExactVotes)
        .ToArray();
        var best = ranked[0];
        // 至少三个候选对完整名称一致；其他完整名称票数相同时保留旧读数。
        if (best.Votes < 3 || ranked.Any(entry => entry.Votes == best.Votes
            && !TextMatch.MatchesRecipeName(entry.Value, best.Value))) return "";
        return best.Value;
    }

    internal static string CleanSlotName(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";
        value = value
            .Replace('．', '.')
            .Replace('·', '.')
            .Replace('囗', '0')
            .Replace('〇', '0')
            .Replace('○', '0');
        value = Regex.Replace(value, @"\s+", " ");
        // 仅在数字前（行首或数字间）把顿号/逗号还原为小数点（如「、 300」「4、6」「3、5」）。
        value = Regex.Replace(value, @"(?:^|(?<=[0-9]))\s*[、，,]\s*(?=[0-9])", ".");
        value = Regex.Replace(value, @"(?<=[\p{IsCJKUnifiedIdeographs}])\s+|\s+(?=[\p{IsCJKUnifiedIdeographs}])", "");
        value = Regex.Replace(value, @"(?<=[0-9])\s*\.\s*(?=[0-9])", ".");
        value = Regex.Replace(value, @"(?<=^|\s)\.\s+(?=[0-9])", ".");
        value = Regex.Replace(value, @"(?<=[0-9])\s+(?=[0-9])", "");
        value = Regex.Replace(value, @"(?<=[0-9])\s*×\s*(?=[0-9])", "×");
        value = Regex.Replace(value, @"(?<=[0-9])\s+mm\b", "mm", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"^×\s*(?=[A-Za-z])", "X");
        value = Regex.Replace(value, @"(?<=[A-Za-z0-9])一(?=[A-Za-z0-9])", "-");
        return value
            .Replace("氵敫", "激", StringComparison.Ordinal)
            .Replace("喵准镜", "瞄准镜", StringComparison.Ordinal)
            .Replace("描准镜", "瞄准镜", StringComparison.Ordinal)
            .Replace("瞄准锯", "瞄准镜", StringComparison.Ordinal);
    }

    /// <summary>评估原始 OCR 名称中的已知误读痕迹，用于同票/双票合并时优先保留更干净的原读数。</summary>
    internal static int RawOcrArtifactPenalty(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        int penalty = 0;
        foreach (char c in raw)
        {
            if (c is '囗' or '〇' or '○') penalty += 3;
            else if (c is '、' or '，') penalty += 1;
        }
        string compact = Normalize(raw);
        if (compact.Contains("喵准镜", StringComparison.Ordinal)) penalty += 3;
        if (compact.Contains("描准镜", StringComparison.Ordinal)) penalty += 3;
        if (compact.Contains("瞄准锯", StringComparison.Ordinal)) penalty += 3;
        if (compact.Contains("氵敫", StringComparison.Ordinal)) penalty += 3;
        if (Regex.IsMatch(compact, @"^×[A-Za-z]")) penalty += 1;
        if (Regex.IsMatch(compact, @"(?<=[A-Za-z0-9])一(?=[A-Za-z0-9])")) penalty += 1;
        return penalty;
    }

    /// <summary>原始画面 3 个倍率（2x/1x/1.5x）全部读出有效名称且一致时，跳过后续二值化增强与 OCR。</summary>
    internal static bool IsUnanimousSlotName(IReadOnlyList<string> readings)
    {
        if (readings.Count < 3) return false;
        string first = CleanSlotName(readings[0]);
        if (first.Length < 2) return false;
        for (int i = 1; i < readings.Count; i++)
        {
            string current = CleanSlotName(readings[i]);
            if (current.Length < 2 || !TextMatch.MatchesRecipeName(first, current))
                return false;
        }
        return true;
    }

    internal async Task<string[]> ReadSlotNamesFromFrameAsync(CapturedFrame frame, IReadOnlyList<NRect> slots)
    {
        // 卡片上部标题与中央物品贴图分开，避免医疗箱的 AID 等贴图字母进入名称。
        var rois = slots.Select(slot => new NRect
        {
            X=slot.X+slot.W*.04, Y=slot.Y+slot.H*.03, W=slot.W*.92, H=slot.H*.22
        }).ToArray();
        var result = new string[slots.Count];
        for (int index=0; index<slots.Count; index++)
        {
            var roi=rois[index];
            var candidates = new List<string>(6);
            foreach(double scale in new[]{2.0,1.0,1.5})
                candidates.Add((await _ocr.ReadAsync(frame,roi,scale)).FullText);
            // 三个原始倍率全票一致时直接定案，免去二值化增强与 3 次额外 OCR，且避免阈值抹掉小数点。
            if (IsUnanimousSlotName(candidates))
            {
                result[index] = SelectSlotName(candidates);
                continue;
            }
            // 仅裁剪并二值化当前标题小区域（约数十 KB），避免为单个 ROI 整帧克隆 14MB+ 像素缓冲。
            var low = CropAndEnhanceContrast(frame, roi, 100);
            var high = CropAndEnhanceContrast(frame, roi, 130);
            candidates.Add((await _ocr.ReadAsync(low,null,1.5)).FullText);
            candidates.Add((await _ocr.ReadAsync(low,null,2.0)).FullText);
            candidates.Add((await _ocr.ReadAsync(high,null,2.0)).FullText);
            result[index]=SelectSlotName(candidates);
        }
        return result;
    }
}
