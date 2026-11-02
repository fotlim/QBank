using QBank.Core.Models;

namespace QBank.Core.Search;

/// <summary>
/// 题干相似度打分。中文题目主要靠“二元组 Dice 系数 + 最长包含 + 编辑距离”三者取优，
/// 既能命中只差几个字的同一道题，也不会把无关题目算高分。
/// </summary>
public static class SimilarityScorer
{
    /// <summary>比较两段文本（内部会先归一化）。</summary>
    public static double Score(string? a, string? b)
        => ScoreNormalized(QuestionNormalizer.Normalize(a), QuestionNormalizer.Normalize(b));

    /// <summary>比较两段已经归一化的文本。</summary>
    public static double ScoreNormalized(string? normalizedA, string? normalizedB)
    {
        var a = normalizedA ?? string.Empty;
        var b = normalizedB ?? string.Empty;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        var (shortText, longText) = a.Length <= b.Length ? (a, b) : (b, a);
        var lengthRatio = (double)shortText.Length / longText.Length;

        // 包含关系：短题干被长题干完全包含（含题型标注、多余前缀等）
        var containment = longText.Contains(shortText, StringComparison.Ordinal)
            ? 0.6 + 0.4 * lengthRatio
            : 0.0;

        var dice = DiceCoefficient(a, b);
        var lev = LevenshteinRatio(a, b);

        var mixed = 0.7 * dice + 0.3 * lev * lengthRatio;

        var score = Math.Max(containment, mixed);
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>二元组（bigram）Dice 系数；文本过短时退化为字符集合。</summary>
    public static double DiceCoefficient(string a, string b)
    {
        var setA = Bigrams(a);
        var setB = Bigrams(b);
        if (setA.Count == 0 || setB.Count == 0)
        {
            var charsA = new HashSet<char>(a);
            var charsB = new HashSet<char>(b);
            var inter0 = charsA.Count(c => charsB.Contains(c));
            return charsA.Count + charsB.Count == 0 ? 0 : 2.0 * inter0 / (charsA.Count + charsB.Count);
        }

        var inter = setA.Count(x => setB.Contains(x));
        return 2.0 * inter / (setA.Count + setB.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Length < 2)
        {
            if (s.Length == 1) set.Add(s);
            return set;
        }
        for (var i = 0; i + 1 < s.Length; i++)
            set.Add(s.Substring(i, 2));
        return set;
    }

    /// <summary>基于编辑距离的相似度（0~1）。文本过长时截断，避免耗时。</summary>
    public static double LevenshteinRatio(string a, string b)
    {
        const int maxLen = 400;
        if (a.Length > maxLen) a = a[..maxLen];
        if (b.Length > maxLen) b = b[..maxLen];

        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1;

        var distance = Levenshtein(a, b);
        return Math.Clamp(1.0 - (double)distance / max, 0, 1);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}