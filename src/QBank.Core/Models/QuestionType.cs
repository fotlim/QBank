namespace QBank.Core.Models;

/// <summary>题目类型。字符串取值与 OCS 保持一致：single / multiple / judgement / completion。</summary>
public enum QuestionType
{
    Unknown = 0,
    Single,
    Multiple,
    Judgement,
    Completion,
}

public static class QuestionTypeExtensions
{
    /// <summary>转成 OCS 使用的题型字符串（Unknown 转成空串，OCS 里即“未传”）。</summary>
    public static string ToOcs(this QuestionType type) => type switch
    {
        QuestionType.Single => "single",
        QuestionType.Multiple => "multiple",
        QuestionType.Judgement => "judgement",
        QuestionType.Completion => "completion",
        _ => string.Empty,
    };

    public static string ToChinese(this QuestionType type) => type switch
    {
        QuestionType.Single => "单选题",
        QuestionType.Multiple => "多选题",
        QuestionType.Judgement => "判断题",
        QuestionType.Completion => "填空题",
        _ => "未知题型",
    };

    public static QuestionType ParseOcs(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "single" or "单选" or "单选题" => QuestionType.Single,
            "multiple" or "多选" or "多选题" => QuestionType.Multiple,
            "judgement" or "judge" or "判断" or "判断题" => QuestionType.Judgement,
            "completion" or "填空" or "填空题" or "简答" or "简答题" => QuestionType.Completion,
            _ => QuestionType.Unknown,
        };
    }
}

/// <summary>依据题干与选项文本猜测题型（用户在界面上粘贴题目时自动判定）。</summary>
public static class QuestionTypeInference
{
    public static QuestionType Infer(string? title, string? options)
    {
        var t = (title ?? string.Empty);
        var opt = (options ?? string.Empty);

        // 1. 题干里的显式标记优先
        if (t.Contains("多选") || t.Contains("多项选择")) return QuestionType.Multiple;
        if (t.Contains("判断") || t.Contains("对错")) return QuestionType.Judgement;
        if (t.Contains("填空") || t.Contains("简答") || t.Contains("论述")) return QuestionType.Completion;
        if (t.Contains("单选")) return QuestionType.Single;

        var lines = OptionLines(opt);

        // 2. 两个选项且是“对/错”一类 → 判断题
        if (lines.Count == 2)
        {
            var joined = string.Join("", lines.Select(StripOptionPrefix));
            if (IsJudgementPair(joined)) return QuestionType.Judgement;
        }

        // 3. 有选项 → 单选；无选项 → 填空/简答
        if (lines.Count >= 2) return QuestionType.Single;
        if (lines.Count == 1) return QuestionType.Single;

        return string.IsNullOrWhiteSpace(opt) ? QuestionType.Completion : QuestionType.Single;
    }

    public static List<string> OptionLines(string? options) =>
        (options ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

    private static string StripOptionPrefix(string line)
    {
        if (line.Length >= 2 && char.IsLetter(line[0]) && "．.、,，)）:：]".Contains(line[1]))
            return line[2..].Trim();
        return line.Trim();
    }

    private static bool IsJudgementPair(string text)
    {
        var t = text.Replace(" ", string.Empty);
        string[] pairs =
        {
            "对错", "正确错误", "是否", "真假", "√×", "√X", "TX", "TF", "yn", "yesno",
            "trufalse", "truefalse",
        };
        var lower = t.ToLowerInvariant();
        return pairs.Any(p => t.Contains(p) || lower.Contains(p.ToLowerInvariant()));
    }
}