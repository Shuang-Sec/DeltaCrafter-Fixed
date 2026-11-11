using System.Text;

namespace DeltaCrafter.Core.L0;

/// <summary>
/// 文本匹配的规范形(纯函数)。规则:去空白与弱标点 → 同形字折叠(O/o→0,I/l→1)→ 转大写。
/// 折叠同时作用于比较双方,专治 OCR 对拉丁字符的 0/O、1/I/l 误读
/// (如「OE2战斗兴奋剂」被读成「0E2战斗兴奋剂」仍可对上)。
/// 弱标点(./`/·/引号)一律忽略:游戏字体的小数点常被读成 ` 等
/// (实测「7.62×51mm BPZ」读作「7`62×51mmBPZ」),忽略后仍可对上。
/// 仅用于比较与去重;显示文本永不经过此函数。
/// </summary>
public static class TextMatch
{
    public static string Canonical(string text) => Canonical(text, preserveDecimalPoints: false);

    private static string Canonical(string text, bool preserveDecimalPoints)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '.' or '．' or '`' or '·' or '\'' or '’' or '‘' or '´' or ',' or '，')
            {
                if (preserveDecimalPoints) sb.Append('.'); // 名称中的 3.5 与 35 是不同规格。
                continue;
            }
            if (preserveDecimalPoints && c == '毛')
            {
                sb.Append(".6"); // 只在配方全名中恢复实测 .6 连体误读。
                continue;
            }
            char folded = c switch
            {
                'O' or 'o' or 'О' or 'о' => '0', // 含西里尔 O
                'I' or 'l' => '1',
                '×' => 'X', // 弹药口径写法:规范名用乘号,游戏/OCR 常用字母 x
                '毛' => '6', // 实测「.6」被 OCR 连体误读成「毛」(7.62 → 7毛2);目录无含「毛」物品
                _ => c,
            };
            sb.Append(char.ToUpperInvariant(folded));
        }
        return sb.ToString();
    }

    public static bool LineContains(string lineText, string target) =>
        Canonical(lineText).Contains(Canonical(target), StringComparison.Ordinal);

    /// <summary>
    /// 配方名称必须全字段一致，保留 BT/BS、ST+、+P 等型号差异。
    /// 仅忽略独立数量尾标(*180/×180)，并容忍实测中文同形误读及 WARRIOR 3S 品牌前缀。
    /// 页面关键词仍使用 LineContains；此方法不做通用包含或编辑距离猜测。
    /// </summary>
    public static bool MatchesRecipeName(string observed, string expected)
    {
        string actualName = RecipeCanonical(observed);
        string expectedName = RecipeCanonical(expected);
        return actualName.Length > 0 && string.Equals(actualName, expectedName, StringComparison.Ordinal);
    }

    private static string RecipeCanonical(string text)
    {
        // 数量符号必须明确，不能把型号里的 X180 当作数量吞掉；扫描保存的匹配键也可能含数量。
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[*×]\s*[0-9]+\s*$", "");
        // 实图的标题品质图标会被识别成边缘中文双引号；只去该装饰，不删名称内部字符。
        text = text.Trim().Trim('“', '”');
        // 详情标题左侧的品质图标偶尔会粘成前导字符：实测为「0/》/卜」+数字，
        // 且只在紧接着的数字小数分隔符前出现。仅剥离这组已验证的装饰，
        // 不放宽普通名称前缀，避免把相似型号误认成目标配方。
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"^(?:0|O|»|》|卜|>|僉)\s*(?=[0-9]\s*[\.．])", "");
        // 「0凵GHT」属于已验证的 OLIGHT 品牌误读，首位0不是品质图标。
        if (!Canonical(text).StartsWith("0凵GHT", StringComparison.Ordinal))
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"^(?:0|O|»|》|卜|>|僉)\s*(?=[\p{IsCJKUnifiedIdeographs}])", "");
        string canonical = Canonical(text.Replace("氵敫", "激", StringComparison.Ordinal)
            .Replace("喵准镜", "瞄准镜", StringComparison.Ordinal), preserveDecimalPoints: true);

        // 默认目录的 OCR 键特意省略此品牌；只接受已验证的这一个完整型号别名。
        string warrior = Canonical("WARRIOR3S战术手电");
        if (canonical == Canonical("OLIGHT") + warrior || canonical == Canonical("0凵GHT") + warrior)
            return warrior;
        return canonical;
    }

    /// <summary>子串近似匹配距离:needle 与 haystack 任意子串的最小编辑距离
    /// (半全局对齐,haystack 头尾多余字符免费;双方先取规范形)。
    /// 用于生产列表行内找物品名:行首价格/行尾角标不计罚,单点误读计 1
    /// (实测「7.62」会被读成「7毛2」或吞掉「7.」,规范形包含匹配对此无能为力)。</summary>
    public static int SubstringDistance(string haystack, string needle)
    {
        string h = Canonical(haystack), n = Canonical(needle);
        if (n.Length == 0) return 0;
        if (h.Length == 0) return n.Length;
        var prev = new int[h.Length + 1]; // 全 0:允许从 haystack 任意位置开始
        var cur = new int[h.Length + 1];
        for (int i = 1; i <= n.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= h.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1),
                                  prev[j - 1] + (n[i - 1] == h[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev.Min(); // 任意位置结束:尾部多余字符免费
    }
}
