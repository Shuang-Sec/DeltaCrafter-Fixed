using System.Text;
using System.Text.RegularExpressions;

namespace DeltaCrafter.Core.L2;

/// <summary>
/// 总览槽位“独立计时 ROI”专用归一化。作用域只有这一行：该 ROI 里只可能出现倒计时，
/// 因此允许把 2026-10-28 至 2026-11-10 实测的形近字折回数字（J→0、&amp;→7、口/〇→0…）、
/// 把各种分隔符折成冒号，从而救回“只差一个字符”的读数（实测「0J58：09」= 00:58:09）。
///
/// 边界：必须恰好还原成三段合法时分秒（或“四位数字+冒号+两位”等丢失一个分隔符的形式），
/// 分秒 0-59、小时 0-99。丢位、含残余字母、三位小时（时钟图标粘连的 902:10:24）一律拒绝。
/// 六位纯数字也接受：计时 ROI 里它只可能是 HHMMSS（实测「00:22:57」被读成「002257」）。
/// 物品名、价格、清单行永远不会经过本方法——它只在计时 ROI 的返回值上执行。
/// </summary>
internal static class SlotTimerCanonicalizer
{
    private static readonly Regex FullForm =
        new(@"^(?<h>[0-9]{1,2}):(?<m>[0-9]{2}):(?<s>[0-9]{2})$", RegexOptions.Compiled);
    private static readonly Regex CompactedHead =
        new(@"^(?<h>[0-9]{2})(?<m>[0-9]{2}):(?<s>[0-9]{2})$", RegexOptions.Compiled);
    private static readonly Regex CompactedTail =
        new(@"^(?<h>[0-9]{2}):(?<m>[0-9]{2})(?<s>[0-9]{2})$", RegexOptions.Compiled);

    public static bool TryCanonicalize(string? raw, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // 「:0」两个字符常被合并读成摄氏度符号（实测 00:58:09 → 00：58℃9）；
        // 先做字符串级还原，与 CountdownParser 的既有约定保持一致。
        string folded = raw.Replace("℃", ":0", StringComparison.Ordinal);

        var sb = new StringBuilder(folded.Length);
        int digits = 0;
        foreach (char c in folded)
        {
            if (c is >= '0' and <= '9') { sb.Append(c); digits++; continue; }
            if (IsSeparator(c)) { sb.Append(':'); continue; }
            if (TryFoldDigit(c, out char digit)) { sb.Append(digit); digits++; continue; }
            // 其余字符（图标残片、结尾 '-'、空格、字母）直接丢弃：
            // 能否成立最终只由下面的整串形状与字段范围决定。
        }

        // 丢掉首尾多余冒号（图标与文字之间常夹一个冒号状残片）。
        string value = sb.ToString().Trim(':');
        if (digits != 6) return false;

        int hours, minutes, seconds;
        if (!value.Contains(':'))
        {
            // 计时 ROI 里六位纯数字只可能是 HHMMSS：实测防具台「00:22:57」被读成「002257」，
            // 两个分隔符都丢掉；旧实现一律拒绝，四遍读数白白作废。物品名与价格不会进这个 ROI。
            if (value.Length != 6) return false;
            hours = int.Parse(value[..2]);
            minutes = int.Parse(value[2..4]);
            seconds = int.Parse(value[4..]);
        }
        else
        {
            if (value.Length is < 7 or > 8) return false;
            Match match;
            if ((match = FullForm.Match(value)).Success)
            {
                hours = int.Parse(match.Groups["h"].Value);
                minutes = int.Parse(match.Groups["m"].Value);
                seconds = int.Parse(match.Groups["s"].Value);
            }
            else if ((match = CompactedHead.Match(value)).Success ||
                     (match = CompactedTail.Match(value)).Success)
            {
                // 丢失一个分隔符时只有一种合法切分：前四位是时分、后两位是秒（或倒过来）。
                hours = int.Parse(match.Groups["h"].Value);
                minutes = int.Parse(match.Groups["m"].Value);
                seconds = int.Parse(match.Groups["s"].Value);
            }
            else
            {
                return false;
            }
        }

        if (hours > 99 || minutes > 59 || seconds > 59) return false;
        canonical = $"{hours}:{minutes:00}:{seconds:00}";
        return true;
    }

    private static bool IsSeparator(char c) =>
        c is ':' or '：' or ',' or '，' or '．' or '。' or '·' or '∙' or '•' or '‧' or ';' or '、' or '/' or '.';

    /// <summary>只收录运行日志中真实出现过、且在计时 ROI 内不会被误当字母的形近字。</summary>
    private static bool TryFoldDigit(char c, out char digit)
    {
        digit = c switch
        {
            'O' or 'o' or 'О' or 'о' or '口' or '囗' or '〇' or '○' => '0',
            'J' => '0', // 实测「00:58:09」被读成「0J58：09」
            'a' or 'A' => '0', // 实测「00:21:18」被读成「0a21：18」：0 与冒号粘成一个字形
            'l' or 'I' or '|' => '1',
            'S' or 's' => '5',
            'B' => '8',
            'Z' or 'z' => '2',
            '&' => '7', // 实测「00:57:00」被读成「OO：5&OO」
            _ => '\0',
        };
        return digit != '\0';
    }
}
