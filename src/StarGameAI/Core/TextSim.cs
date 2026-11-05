using System.Text;

namespace StarGameAI.Core;

/// <summary>
/// 文本相似度工具。全系统只有这一份实现
/// </summary>
public static class TextSim
{
    /// <summary>
    /// 只保留字母与汉字并转小写，去掉标点噪声
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public static HashSet<string> Bigrams(string normalized)
    {
        var set = new HashSet<string>();
        if (normalized.Length == 0) return set;
        if (normalized.Length == 1) { set.Add(normalized); return set; }
        for (var i = 0; i < normalized.Length - 1; i++) set.Add(normalized.Substring(i, 2));
        return set;
    }

    /// <summary>
    /// 0~1 的相似度。1 表示去掉标点后完全相同，0 表示毫无共同二元组
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return na.Length == nb.Length ? 1 : 0;
        if (na == nb) return 1;

        var sa = Bigrams(na);
        var sb = Bigrams(nb);
        var inter = 0;
        foreach (var g in sa)
            if (sb.Contains(g)) inter++;

        var union = sa.Count + sb.Count - inter;
        return union > 0 ? (double)inter / union : 0;
    }

    /// <summary>
    /// 两段文字有没有共同的具体词：共享一个 ≥2 字的连续片段就算有
    ///
    /// 只用于「记忆层前置」这种粗筛——有共同词就值得把记忆拿出来给模型看，
    /// 没有就跳过。不做语义判断，也不决定最终选什么。
    /// </summary>
    public static bool SharesTerm(string query, string text, int minLen = 2)
    {
        var q = Normalize(query);
        var t = Normalize(text);
        if (q.Length < minLen || t.Length < minLen) return false;
        if (q == t) return true;

        // 二元组就够：中文里两个连续字已经是很强的信号
        var grams = Bigrams(t);
        for (var i = 0; i + minLen <= q.Length; i++)
            if (grams.Contains(q.Substring(i, minLen))) return true;

        return false;
    }

    /// <summary>
    /// 行级增删统计，用于衡量改写幅度
    /// </summary>
    public static (int added, int removed) LineDelta(string before, string after)
    {
        var a = (before ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var b = (after ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var beforeSet = new HashSet<string>(a, StringComparer.Ordinal);
        var afterSet = new HashSet<string>(b, StringComparer.Ordinal);

        var added = b.Count(l => !beforeSet.Contains(l));
        var removed = a.Count(l => !afterSet.Contains(l));
        return (added, removed);
    }
}
