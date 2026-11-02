using System.Text.Json;
using System.Text.RegularExpressions;
using QBank.Core.Models;

namespace QBank.Core.Ocs;

/// <summary>
/// 答案文本处理：忠实移植 OCS 4.x <c>packages/core/src/core/worker/utils.ts</c> 里的
/// <c>splitAnswer</c> / <c>isPlainAnswer</c> / <c>resolvePlainAnswer</c> / <c>isProgramAnswer</c>，
/// 让本程序对题库返回的答案切分结果与 OCS 脚本完全一致。
/// </summary>
public static partial class AnswerSplitter
{
    public static readonly string[] DefaultSeparators = { "===", "#", "---", "###", "|", ";", "；" };

    /// <summary>判断答案是否为 A-Z 的文本，并且字符序号依次递增、每个字符只出现一次（与 OCS 一致，只认大写）。</summary>
    public static bool IsPlainAnswer(string? answer)
    {
        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 8 || !Regex.IsMatch(text, "[A-Z]")) return false;

        var counter = new Dictionary<int, int>();
        var min = 0;
        foreach (var ch in text)
        {
            var code = (int)ch;
            if (code < min) return false;
            min = code;
            counter[code] = counter.TryGetValue(code, out var c) ? c + 1 : 1;
        }
        return counter.Values.All(v => v == 1);
    }

    /// <summary>判断是否为纯 ABCD 多选答案（中间可能夹着分隔符），是则返回去掉分隔符的答案。</summary>
    public static string? ResolvePlainAnswer(string? answer)
    {
        var resolve = PunctuationRegex().Replace((answer ?? string.Empty).Trim(), string.Empty).Trim();
        return IsPlainAnswer(resolve) ? resolve : null;
    }

    [GeneratedRegex(@"[,，.。．、 #]")]
    private static partial Regex PunctuationRegex();

    /// <summary>
    /// 检测答案是否为一段程序代码。程序题的题库答案是代码，其中 <c>;</c> <c>|</c> <c>；</c>
    /// 是语法而非答案分隔符，命中则取消这些分隔符，避免代码被错误拆分。
    /// </summary>
    public static bool IsProgramAnswer(string? answer)
    {
        var code = (answer ?? string.Empty).Trim();
        if (code.Length == 0) return false;
        if (!Regex.IsMatch(code, @"[;|；]")) return false;

        var score = 0;
        string[] strong =
        {
            @"#include\s*[<""]", @"#define\s+\w", @"#!/", @"\b(?:int|void|float|double|char|long|short)\s+main\s*\(",
            @"\bfunction\s+\w+\s*\(", @"\bdef\s+\w+\s*\(", @"\bpublic\s+(?:class|static|void|int|float|double|String|final)\b",
            @"\bprivate\s+(?:class|static|void|int|float|double|String|final)\b",
            @"\bprotected\s+(?:class|static|void|int|float|double|String|final)\b",
            @"\bprintf\s*\(", @"\bscanf\s*\(", @"\bcout\s*<<", @"\bcin\s*>>", @"System\.out\.", @"console\.log\s*\(",
            @"\bpackage\s+[\w.]+\s*;", @"\bimport\s+[\w.*]+\s*;", @"\bclass\s+\w+\s*\{", @"\bstruct\s+\w+\s*\{",
        };
        foreach (var re in strong)
            if (Regex.IsMatch(code, re)) score += 2;

        string[] medium =
        {
            @"=>", @"->", @"::", @"\+\+", @"--", @"[+\-*/%]=", @"\bnew\s+\w+\s*\(", @"\bfor\s*\(", @"\bwhile\s*\(",
            @"\bif\s*\(", @"\bswitch\s*\(", @"\btypedef\s+",
            @"\b(?:int|float|double|char|void|bool|boolean|long|short|String|auto|const|let|var)\s+\w+",
            @"\bsizeof\s*\(", @"\bmalloc\s*\(", @"\bfree\s*\(", @"\bcout\b", @"\bcin\b",
        };
        foreach (var re in medium)
            if (Regex.IsMatch(code, re)) score += 1;

        if (code.Contains('{') && code.Contains('}')) score += 1;
        if (code.Contains(';')) score += 1;

        return score >= 2;
    }

    /// <summary>分割答案（与 OCS splitAnswer 行为一致）。</summary>
    public static List<string> Split(string? answer, string[]? separators = null)
    {
        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0) return new List<string>();

        var seps = (separators is null || separators.Length == 0 ? DefaultSeparators : separators)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (IsProgramAnswer(text))
            seps = seps.Where(s => s != ";" && s != "|" && s != "；").ToList();

        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // 不是 JSON，继续按分隔符走
            }
        }

        foreach (var sep in seps)
        {
            var parts = text.Split(new[] { sep }, StringSplitOptions.None);
            if (parts.Length > 1)
                return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        return new List<string> { text };
    }

    /// <summary>
    /// 把选择题答案整理成大写字母串（多选用连写），例如 "a,b d" → "ABD"。
    /// 不是纯字母答案时原样返回。
    /// </summary>
    public static string NormalizeChoiceAnswer(string? answer, QuestionType type = QuestionType.Unknown)
    {
        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        // 字母答案先统一转大写再判纯字母（OCS 的 isPlainAnswer 只认大写，
        // 但题库常返回 "a,b d" 这种小写写法，这里先归一化更实用）
        var plain = ResolvePlainAnswer(text.ToUpperInvariant());
        if (plain is not null) return plain;

        var parts = Split(text);
        if (parts.Count == 0) return text;
        if (parts.Count == 1) return parts[0];
        if (parts.All(p => p.Length <= 6 && Regex.IsMatch(p, @"^[A-Za-z]+$")))
            return string.Concat(parts.Select(p => p.ToUpperInvariant()));

        return string.Join("#", parts);
    }

    /// <summary>从答案里提取选项字母（用于和选项列表比对、判断 AI 是否答得像选择题）。</summary>
    public static string ExtractLetters(string? answer)
    {
        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        var plain = ResolvePlainAnswer(text);
        if (plain is not null) return plain;
        var letters = new string(text.Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z').ToArray()).ToUpperInvariant();
        return letters.Length is > 0 and <= 8 ? letters : string.Empty;
    }
}