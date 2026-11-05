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
    /// 两段文字有没有共同的具体词：共享一个 ≥3 字的连续片段才算有
    ///
    /// 门槛不能低到 2 个字：查「EB」会撞上「Li-ebe」里的 eb，
    /// 于是把一条完全无关的群聊记录当成"你记得"注入私聊。
    /// 查询本身很短时（两三个字母）要求它不落在别的单词里面。
    /// </summary>
    public static bool SharesTerm(string query, string text, int minLen = 3)
    {
        var q = Normalize(query);
        var t = Normalize(text);
        if (q.Length == 0 || t.Length == 0) return false;
        if (q == t) return true;

        if (q.Length < minLen) return WholeWordHit(t, q);

        // 取 t 里所有长度 minLen 的片段，看 q 里有没有一段落在里面
        var windows = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + minLen <= t.Length; i++) windows.Add(t.Substring(i, minLen));

        for (var i = 0; i + minLen <= q.Length; i++)
            if (windows.Contains(q.Substring(i, minLen))) return true;

        return false;
    }

    /// <summary>
    /// 短查询：中文两字就算一个词，包含即可；
    /// 拉丁字母太容易撞进别的单词（EB 撞 Liebe），必须跨词边界
    /// </summary>
    private static bool WholeWordHit(string haystack, string needle)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var before = idx > 0 ? haystack[idx - 1] : ' ';
            var after = idx + needle.Length < haystack.Length ? haystack[idx + needle.Length] : ' ';

            var leftOk = !IsLatin(before) || !IsLatin(needle[0]);
            var rightOk = !IsLatin(after) || !IsLatin(needle[^1]);
            if (leftOk && rightOk) return true;

            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsLatin(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9';

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
