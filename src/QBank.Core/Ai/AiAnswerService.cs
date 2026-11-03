using System.Text;
using System.Text.Json;
using QBank.Core.Models;
using QBank.Core.Ocs;

namespace QBank.Core.Ai;

/// <summary>模型返回的答案对象。</summary>
public sealed class AiAnswerObject
{
    public string? Answer { get; set; }
    public string? Question { get; set; }
    public double Confidence { get; set; } = 0.5;
    public string? Explain { get; set; }

    /// <summary>是否走了兜底解析（模型没按要求输出 JSON）。</summary>
    public bool Fallback { get; set; }
}

/// <summary>答题提示词构造。</summary>
public static class AiPromptBuilder
{
    public const string DefaultSystemPrompt =
        """
        你是一个专业、严谨的答题助手，任务是给出题目的正确答案。

        【输出要求】
        只输出一个 JSON 对象，不要输出 Markdown 代码块，不要输出任何解释性前后缀。格式：
        {"answer":"答案","question":"你对题干的规范化表述","confidence":0.0到1.0之间的小数,"explain":"不超过30字的一句话理由"}

        【答案规则】
        1. 单选题：answer 只填一个选项字母，例如 "A"。
        2. 多选题：answer 填连写的选项字母，例如 "ABD"；不要用逗号、空格、加号分隔。
        3. 判断题：answer 填选项字母（若选项是“对/错”“正确/错误”，也可直接填 "对" 或 "错"）。
        4. 填空题/简答题：answer 填简洁答案；有多个空时按题目顺序用 # 分隔。
        5. 绝不编造不存在于选项中的字母；题干信息不足时给出最可能的答案，并把 confidence 调低（不超过 0.5）。

        【重要】必须输出合法 JSON：所有字符串用双引号，不要在 JSON 之外输出任何字符。
        """;

    public static List<AiChatMessage> Build(SearchRequest req, string? customSystemPrompt = null)
    {
        var system = string.IsNullOrWhiteSpace(customSystemPrompt) ? DefaultSystemPrompt : customSystemPrompt!;
        return new List<AiChatMessage>
        {
            AiChatMessage.System(system),
            AiChatMessage.User(BuildUserContent(req)),
        };
    }

    public static string BuildUserContent(SearchRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("题型：").Append(req.Type == QuestionType.Unknown ? "未知（请根据题干和选项判断）" : req.Type.ToChinese()).Append('\n');
        sb.Append("题干：").Append(string.IsNullOrWhiteSpace(req.Title) ? "（未提供）" : req.Title.Trim()).Append('\n');

        var options = (req.Options ?? string.Empty).Trim();
        if (options.Length > 0)
        {
            sb.Append("选项：\n").Append(options);
        }
        else
        {
            sb.Append("选项：无");
        }
        return sb.ToString();
    }
}

/// <summary>把模型输出解析成结构化答案（尽可能宽容）。</summary>
public static class AiAnswerParser
{
    public static AiAnswerObject Parse(string? content, QuestionType type, string? options)
    {
        var text = (content ?? string.Empty).Trim();
        if (text.Length == 0)
            return new AiAnswerObject { Answer = string.Empty, Confidence = 0, Fallback = true };

        var candidate = ExtractJsonObject(text);
        if (candidate is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var obj = new AiAnswerObject
                    {
                        Answer = ReadString(doc.RootElement, "answer", "ans", "答案", "result", "correct_answer"),
                        Question = ReadString(doc.RootElement, "question", "title", "题目", "题干"),
                        Explain = ReadString(doc.RootElement, "explain", "explanation", "reason", "理由", "analysis", "解析"),
                    };
                    var conf = ReadDouble(doc.RootElement, "confidence", "置信度", "score", "prob", "probability");
                    obj.Confidence = conf ?? 0.7;
                    if (obj.Confidence > 1) obj.Confidence /= 100.0; // 兼容填 90 的模型
                    obj.Confidence = Math.Clamp(obj.Confidence, 0.05, 0.99);
                    if (!string.IsNullOrWhiteSpace(obj.Answer))
                    {
                        obj.Answer = NormalizeAnswer(obj.Answer!, type, options);
                        return obj;
                    }
                }
            }
            catch (JsonException)
            {
                // 落到兜底解析
            }
        }

        return FallbackParse(text, type, options);
    }

    private static string NormalizeAnswer(string answer, QuestionType type, string? options)
    {
        var a = answer.Trim().Trim('"').Trim();
        if (a.Length == 0) return a;

        switch (type)
        {
            case QuestionType.Single:
            case QuestionType.Multiple:
            {
                var letters = AnswerSplitter.ExtractLetters(a);
                return letters.Length > 0 ? letters : a;
            }
            case QuestionType.Judgement:
                return JudgementMapper.Map(a, options);
            default:
                return a;
        }
    }

    private static AiAnswerObject FallbackParse(string text, QuestionType type, string? options)
    {
        var body = text;
        if (body.Contains("```"))
        {
            var first = body.IndexOf("```", StringComparison.Ordinal);
            var last = body.LastIndexOf("```", StringComparison.Ordinal);
            if (last > first)
            {
                body = body[(first + 3)..last];
                var nl = body.IndexOf('\n');
                if (nl >= 0 && body[..nl].Trim().Length <= 12) body = body[(nl + 1)..];
            }
        }

        var line = body
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? string.Empty;

        string[] prefixes = { "正确答案：", "正确答案:", "答案是", "答案：", "答案:", "答：", "答:", "Answer:", "answer:", "正确选项：", "选" };
        foreach (var p in prefixes)
        {
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                line = line[p.Length..].Trim();
                break;
            }
        }

        line = line.Trim('*', '`', '"', '“', '”', '。', '，', ',', ' ');

        var answer = NormalizeAnswer(line, type, options);
        return new AiAnswerObject
        {
            Answer = answer,
            Question = null,
            Confidence = 0.45,
            Explain = null,
            Fallback = true,
        };
    }

    /// <summary>从文本里抠出第一个完整 JSON 对象。</summary>
    internal static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Array => string.Join('#', prop.Value.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())
                        .Where(s => s.Length > 0)),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => prop.Value.GetRawText(),
                };
            }
        }
        return null;
    }

    private static double? ReadDouble(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var d)) return d;
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    double.TryParse(prop.Value.GetString(), out var s)) return s;
            }
        }
        return null;
    }
}

/// <summary>把“对/错/正确/错误”这类判断题答案映射成选项字母。</summary>
public static class JudgementMapper
{
    private static readonly string[] Positive = { "对", "正确", "是", "真", "√", "对的", "是的", "true", "t", "yes", "y", "1" };
    private static readonly string[] Negative = { "错", "错误", "否", "假", "×", "x", "不对", "不是", "false", "f", "no", "n", "0" };

    public static string Map(string answer, string? options)
    {
        var a = (answer ?? string.Empty).Trim();
        if (a.Length == 0) return a;

        var letters = AnswerSplitter.ExtractLetters(a);
        if (letters.Length == 1 && letters[0] is 'A' or 'B') return letters;

        var lower = a.ToLowerInvariant();
        var isPositive = Positive.Any(p => lower == p || lower.Contains(p));
        var isNegative = Negative.Any(p => lower == p || lower.Contains(p));

        var options2 = ParseOptions(options);

        if (isPositive && !isNegative)
            return FindLetter(options2, Positive) ?? "对";
        if (isNegative && !isPositive)
            return FindLetter(options2, Negative) ?? "错";

        return letters.Length > 0 ? letters : a;
    }

    private static List<(string Letter, string Text)> ParseOptions(string? options)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(options)) return result;

        foreach (var raw in options!.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length >= 2 && char.IsLetter(line[0]) && "．.、,，)）:：]".Contains(line[1]))
                result.Add((char.ToUpperInvariant(line[0]).ToString(), line[2..].Trim()));
            else if (line.Length >= 1 && char.IsLetter(line[0]))
                result.Add((char.ToUpperInvariant(line[0]).ToString(), line[1..].Trim()));
        }
        return result;
    }

    private static string? FindLetter(List<(string Letter, string Text)> options, string[] keywords)
    {
        foreach (var (letter, text) in options)
        {
            var t = text.ToLowerInvariant();
            if (keywords.Any(k => t == k || t.Contains(k)))
                return letter;
        }
        return null;
    }
}

/// <summary>把一个 AI 服务商当成一个搜题来源。</summary>
public sealed class AiAnswerService
{
    public AiAnswerService(AiChatClient? client = null) => Client = client ?? new AiChatClient();

    public AiChatClient Client { get; }

    public async Task<SourceOutcome> SearchAsync(AiProviderConfig provider, SearchRequest req, CancellationToken ct = default)
    {
        var outcome = new SourceOutcome
        {
            SourceId = provider.Id,
            SourceName = provider.DisplayName,
            Kind = SourceKind.Ai,
        };

        var messages = AiPromptBuilder.Build(req, provider.SystemPrompt);
        var result = await Client.CompleteAsync(provider, messages, jsonMode: true, ct).ConfigureAwait(false);

        outcome.ElapsedMs = result.ElapsedMs;
        outcome.RawResponse = result.Raw;

        if (!result.Ok)
        {
            outcome.Ok = false;
            outcome.Error = result.Error ?? "模型调用失败";
            if (!string.IsNullOrWhiteSpace(result.Note)) outcome.Notices.Add(result.Note!);
            return outcome;
        }

        var parsed = AiAnswerParser.Parse(result.Content, req.Type, req.Options);
        var answer = (parsed.Answer ?? string.Empty).Trim();

        if (answer.Length == 0)
        {
            outcome.Notices.Add("模型没有给出可用的答案");
            return outcome;
        }

        var noteParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsed.Explain)) noteParts.Add(parsed.Explain!.Trim());
        if (!string.IsNullOrWhiteSpace(result.Note)) noteParts.Add(result.Note!.Trim());
        noteParts.Add($"模型 {result.Model ?? provider.Model}");
        if (result.CompletionTokens > 0) noteParts.Add($"{result.CompletionTokens} tokens");
        if (parsed.Fallback) noteParts.Add("未按 JSON 格式输出，已兜底解析");

        outcome.Hits.Add(new SearchHit
        {
            Question = string.IsNullOrWhiteSpace(parsed.Question) ? req.Title : parsed.Question!,
            Answer = answer,
            Score = parsed.Fallback ? 0.45 : Math.Clamp(parsed.Confidence, 0.05, 0.99),
            Note = string.Join(" · ", noteParts),
        });

        return outcome;
    }

    /// <summary>连通性测试：问一句最简单的，确认地址、Key、模型都对。</summary>
    public async Task<AiChatResult> TestAsync(AiProviderConfig provider, CancellationToken ct = default)
    {
        var messages = new List<AiChatMessage>
        {
            AiChatMessage.System("你是一个测试用的助手，只输出 JSON。"),
            AiChatMessage.User("请只输出这个 JSON，不要有任何其它内容：{\"answer\":\"ok\"}"),
        };
        return await Client.CompleteAsync(provider, messages, jsonMode: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 挑选参与搜题的 AI 服务：优先用显式启用的；
    /// 一个都没启用时退回「填了 BaseUrl + Model 且带 Key」的那些，避免用户刚装好就搜不到。
    /// </summary>
    public static List<AiProviderConfig> SelectProviders(IEnumerable<AiProviderConfig> providers)
    {
        var list = providers.ToList();
        var enabled = list.Where(p => p.Enabled).OrderBy(p => p.Priority).ToList();
        if (enabled.Count > 0) return enabled;

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model))
            .Where(p => !string.IsNullOrWhiteSpace(p.ApiKey) ||
                        string.Equals(p.ProviderKind, "ollama", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Priority)
            .ToList();
    }
}