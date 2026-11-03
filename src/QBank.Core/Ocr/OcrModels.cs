using System.Text;
using System.Text.RegularExpressions;
using QBank.Core.Models;

namespace QBank.Core.Ocr;

public enum OcrEngineKind
{
    None,
    Umi,
    Windows,
    Vision,
    Ollama,
    Custom,
}

public sealed class OcrTextLine
{
    public string Text { get; set; } = string.Empty;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

public sealed class OcrResult
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public List<OcrTextLine> Lines { get; set; } = new();
    public double Confidence { get; set; } = 0.8;
    public long ElapsedMs { get; set; }
    public List<string> Warnings { get; set; } = new();

    public static OcrResult Fail(string engine, string error) => new()
    {
        Ok = false,
        Engine = engine,
        Error = error,
        Confidence = 0,
    };
}

/// <summary>OCR 文本 → 题目（题干 + 选项）解析。</summary>
public static class OcrQuestionParser
{
    private static readonly Regex OptionPattern = new(
        @"^[\s│|]*[（(【\[]?\s*([A-Ha-h])\s*[）)】\]]?\s*[.．、，,:：·]\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex QuestionNumberPattern = new(
        @"^\s*(?:第\s*)?\d{1,3}\s*[.．、)）:：]?\s*",
        RegexOptions.Compiled);

    public sealed class ParsedQuestion
    {
        public string Title { get; set; } = string.Empty;
        public string Options { get; set; } = string.Empty;
        public QuestionType Type { get; set; }
        public List<string> OptionLines { get; set; } = new();

        public bool HasOptions => OptionLines.Count >= 2;
    }

    /// <summary>把 OCR 出来的整段文字拆成题干与选项。</summary>
    public static ParsedQuestion Parse(string? rawText, bool stripQuestionNumber = true)
    {
        var result = new ParsedQuestion();
        if (string.IsNullOrWhiteSpace(rawText)) return result;

        var lines = rawText.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var titleBuilder = new List<string>();
        var optionLines = new List<string>();
        var firstOptionIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var match = OptionPattern.Match(lines[i]);
            if (match.Success && match.Groups[2].Value.Trim().Length > 0)
            {
                var letter = match.Groups[1].Value.ToUpperInvariant();
                var text = match.Groups[2].Value.Trim();
                var expected = (char)('A' + optionLines.Count);

                // 选项序号必须按 A、B、C… 递增，否则多半是题干里的普通字母
                if (letter == expected.ToString() || optionLines.Count == 0 && letter == "A")
                {
                    if (firstOptionIndex < 0) firstOptionIndex = i;
                    optionLines.Add($"{letter}. {text}");
                    continue;
                }
            }

            if (firstOptionIndex < 0) titleBuilder.Add(lines[i]);
        }

        // 选项后面残留的文字（多行选项的续行）先丢弃，避免污染题干
        var title = string.Join(" ", titleBuilder).Trim();
        if (stripQuestionNumber) title = StripQuestionNumber(title);

        result.Title = title;
        result.OptionLines = optionLines;
        result.Options = optionLines.Count >= 2 ? string.Join("\n", optionLines) : string.Empty;
        result.Type = QuestionTypeInference.Infer(result.Title, result.Options);

        return result;
    }

    public static string StripQuestionNumber(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? string.Empty;
        var stripped = QuestionNumberPattern.Replace(title, string.Empty, 1).Trim();
        return stripped.Length >= 2 ? stripped : title;
    }

    /// <summary>把原图文字整体压成一行，用作题干兜底。</summary>
    public static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }
}

public static class OcrEngineKindExtensions
{
    public static OcrEngineKind ParseEngine(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "umi" or "umiocr" or "umi-ocr" or "paddle" or "paddleocr" or "umi-ocr(默认)" => OcrEngineKind.Umi,
        "windows" or "win" or "系统" => OcrEngineKind.Windows,
        "vision" or "vlm" or "gpt4v" or "视觉大模型" => OcrEngineKind.Vision,
        "ollama" or "local" or "本地模型" => OcrEngineKind.Ollama,
        "custom" or "http" or "自定义" => OcrEngineKind.Custom,
        _ => OcrEngineKind.None,
    };

    public static string ToConfigValue(this OcrEngineKind kind) => kind switch
    {
        OcrEngineKind.Umi => "umi",
        OcrEngineKind.Windows => "windows",
        OcrEngineKind.Vision => "vision",
        OcrEngineKind.Ollama => "ollama",
        OcrEngineKind.Custom => "custom",
        _ => "none",
    };

    public static string ToChinese(this OcrEngineKind kind) => kind switch
    {
        OcrEngineKind.Umi => "Umi-OCR（PaddleOCR 离线）",
        OcrEngineKind.Windows => "Windows 内置 OCR",
        OcrEngineKind.Vision => "视觉大模型（联网 API）",
        OcrEngineKind.Ollama => "Ollama 本地视觉模型",
        OcrEngineKind.Custom => "自定义 OCR 接口",
        _ => "未启用",
    };
}