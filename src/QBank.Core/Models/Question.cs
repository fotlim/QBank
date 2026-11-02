using System.Security.Cryptography;
using System.Text;

namespace QBank.Core.Models;

/// <summary>本地题库里的一条题目。</summary>
public sealed class Question
{
    public long Id { get; set; }

    /// <summary>题干原文。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>归一化后的题干（去标点空白、全角转半角、小写），用于匹配与去重。</summary>
    public string NormalizedTitle { get; set; } = string.Empty;

    /// <summary>归一化题干的前 8 个字符，作为索引前缀加速候选筛选。</summary>
    public string NormalizedPrefix { get; set; } = string.Empty;

    public QuestionType Type { get; set; } = QuestionType.Unknown;

    /// <summary>选项原文，每行一个选项。</summary>
    public string Options { get; set; } = string.Empty;

    /// <summary>答案。多选用 OCS 分隔符（默认 #，也兼容 ABCD 连写）。</summary>
    public string Answer { get; set; } = string.Empty;

    public string? ExtraJson { get; set; }

    /// <summary>来源描述，例如 "本地录入" / "AI:deepseek-chat" / "局域网:张三的电脑"。</summary>
    public string? Source { get; set; }

    /// <summary>空格分隔的标签。</summary>
    public string? Tags { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public int HitCount { get; set; }

    public DateTimeOffset? LastHitAt { get; set; }

    /// <summary>指纹：归一化题干 + 题型的哈希，用于判重。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public static string ComputeFingerprint(string normalizedTitle, QuestionType type)
    {
        var raw = normalizedTitle + "||" + type.ToOcs();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    public void Recompute()
    {
        NormalizedTitle = QuestionNormalizer.Normalize(Title);
        NormalizedPrefix = QuestionNormalizer.Prefix(NormalizedTitle);
        Fingerprint = ComputeFingerprint(NormalizedTitle, Type);
    }

    public Question Clone() => (Question)MemberwiseClone();
}

/// <summary>题干归一化工具。</summary>
public static class QuestionNormalizer
{
    /// <summary>去空白、去常见标点、全角转半角、转小写。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch;
            // 全角 → 半角
            if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);
            if (c == '\u3000') c = ' ';
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            // 其余标点、符号、汉字以外符号全部丢弃（中文汉字 char.IsLetter 为 true，会保留）
        }
        return sb.ToString();
    }

    /// <summary>去掉题干里 OCS 常见的段落编号与题型前缀，例如 "1."、"（1）"、"【单选】"。</summary>
    public static string StripDecorations(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = title.Trim();
        // 【单选】/（多选题）之类的题型标注
        foreach (var marker in new[] { "【单选】", "【多选】", "【判断】", "【填空】", "【简答】", "（单选）", "（多选）", "（判断）", "（填空）", "(单选)", "(多选)", "(判断)" })
            t = t.Replace(marker, string.Empty);
        return t.Trim();
    }

    public static string Prefix(string normalized, int len = 8) =>
        len <= 0 ? string.Empty : (normalized.Length <= len ? normalized : normalized[..len]);
}