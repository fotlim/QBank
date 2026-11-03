using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QBank.Core.Models;

namespace QBank.Core.Ai;

/// <summary>自动打标签的结果。</summary>
public sealed class AutoTagResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Engine { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }

    public string TagsText => Tags.Count == 0 ? "（没打出标签）" : string.Join(" ", Tags);
}

/// <summary>本地词表打标签：不联网、不花钱，按学科关键词 + 题型 + 题干特征给标签。</summary>
public static class QuestionTagRules
{
    /// <summary>学科关键词表：命中就加对应学科标签。</summary>
    private static readonly (string Tag, string[] Words)[] Subjects =
    {
        ("数学", new[] { "函数", "方程", "不等式", "几何", "三角", "数列", "概率", "统计", "向量", "导数", "积分", "集合", "复数", "排列", "组合", "抛物线", "椭圆", "双曲线", "圆心", "半径", "面积", "体积", "周长", "因数", "倍数", "整除", "余数", "百分比", "平方", "立方", "分数", "小数", "对数", "指数", "矩阵", "坐标系", "绝对值" }),
        ("物理", new[] { "力", "速度", "加速度", "质量", "重力", "摩擦", "电场", "磁场", "电流", "电压", "电阻", "功率", "能量", "动量", "波长", "频率", "透镜", "折射", "反射", "压强", "浮力", "机械", "振动", "相对论", "匀速", "牛顿" }),
        ("化学", new[] { "化学", "元素", "原子", "分子", "离子", "化合", "氧化", "还原", "溶液", "摩尔", "浓度", "酸碱", "反应", "有机物", "电解质", "沉淀", "催化剂", "烃", "醇", "酯", "ph值", "同位素" }),
        ("生物", new[] { "细胞", "遗传", "基因", "dna", "rna", "蛋白", "酶", "生态", "光合", "呼吸作用", "神经", "激素", "免疫", "种群", "群落", "染色体", "减数", "有丝", "细胞器", "微生物", "病毒", "疫苗" }),
        ("语文", new[] { "诗", "词牌", "文言文", "成语", "修辞", "病句", "标点", "作者", "名著", "作文", "阅读", "拼音", "字音", "字形", "近义词", "反义词", "俗语", "对联", "古文", "翻译成现代汉语" }),
        ("英语", new[] { "英语", "单词", "语法", "时态", "从句", "完形", "词汇", "音标", "english", "grammar", "复数", "过去式", "不定式", "介词", "被动语态", "定语从句", "虚拟语气", "短语", "translate" }),
        ("政治", new[] { "政治", "宪法", "人民代表大会", "民主", "法治", "市场经济", "货币", "价值规律", "哲学", "唯物", "唯心", "矛盾", "实践", "文化", "民族", "公民", "权利", "义务", "政府", "国家" }),
        ("历史", new[] { "历史", "朝代", "革命", "战争", "条约", "改革", "开放", "皇帝", "王朝", "世纪", "年代", "起义", "变法", "运动", "会议", "秦", "汉", "唐", "宋", "元", "明", "清", "近代", "鸦片" }),
        ("地理", new[] { "地理", "气候", "地形", "洋流", "季风", "经纬", "半球", "人口", "城市", "河流", "山脉", "高原", "平原", "盆地", "土壤", "植被", "时区", "等温线", "地质", "地震" }),
        ("信息技术", new[] { "程序", "代码", "算法", "数组", "变量", "循环", "编译", "网络协议", "数据库", "sql", "二进制", "字节", "操作系统", "加密", "端口", "服务器", "python", "java", "c++", "函数调用", "数据结构", "链表", "递归", "排序" }),
        ("医学护理", new[] { "护理", "患者", "临床", "症状", "血压", "脉搏", "体温", "给药", "无菌", "消毒", "解剖", "药理", "诊断", "心肺" }),
        ("建筑工程", new[] { "混凝土", "钢筋", "施工", "荷载", "砌体", "图纸", "地基", "梁", "柱", "板", "楼板", "防水", "招标" }),
        ("财会经济", new[] { "会计", "借贷", "资产", "负债", "所有者权益", "利润", "凭证", "账", "增值税", "折旧", "成本核算", "审计", "财务报表" }),
    };

    /// <summary>题干特征标签。</summary>
    private static readonly (string Tag, Func<string, bool> Match)[] Features =
    {
        ("公式题", t => t.Any(c => "∫∑√π±²³×÷≈≤≥≠∞".Contains(c)) || t.Contains("^")),
        ("材料题", t => t.Contains("材料") || t.Contains("如下表") || t.Contains("如图所示") || t.Contains("根据图")),
        ("综合题", t => t.Contains("下列说法正确") || t.Contains("以下说法正确") || t.Contains("正确的有")),
        ("概念题", t => t.Contains("是指") || t.Contains("定义") || t.Contains("含义是")),
        ("计算题", t => t.Contains("计算") || t.Contains("求") || t.Contains("等于")),
    };

    /// <summary>按词表给出标签。</summary>
    public static List<string> Suggest(Question question, int maxTags = 4, IEnumerable<string>? knownTags = null)
    {
        maxTags = Math.Clamp(maxTags, 1, 8);
        var tags = new List<string>();

        var haystack = ((question.Title ?? string.Empty) + " " + (question.Options ?? string.Empty)).ToLowerInvariant();

        // 1) 题型
        var typeTag = question.Type switch
        {
            QuestionType.Single => "单选",
            QuestionType.Multiple => "多选",
            QuestionType.Judgement => "判断",
            QuestionType.Completion => "填空",
            _ => null,
        };
        if (typeTag is not null) tags.Add(typeTag);

        // 2) 学科（按命中关键词数量排，避免一题一堆学科）
        var subjectHits = new List<(string Tag, int Hits)>();
        foreach (var (tag, words) in Subjects)
        {
            var hits = words.Count(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hits > 0) subjectHits.Add((tag, hits));
        }
        foreach (var (tag, _) in subjectHits.OrderByDescending(s => s.Hits).ThenBy(s => s.Tag, StringComparer.Ordinal))
        {
            if (tags.Count >= maxTags) break;
            if (!tags.Contains(tag)) tags.Add(tag);
        }

        // 3) 特征
        foreach (var (tag, match) in Features)
        {
            if (tags.Count >= maxTags) break;
            try
            {
                if (match(haystack) && !tags.Contains(tag)) tags.Add(tag);
            }
            catch (ArgumentException)
            {
                // 题干里有奇怪字符时正则判断可能抛错，忽略该特征
            }
        }

        // 4) 已有标签里能对上的，优先保留（让同一批题目的标签收敛）
        if (knownTags is not null)
        {
            var known = knownTags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var tag in known)
            {
                if (tags.Contains(tag)) continue;
                if (demonstrates(haystack, tag) && tags.Count < maxTags) tags.Add(tag);
            }
        }

        return tags.Take(maxTags).ToList();

        static bool demonstrates(string haystack, string tag)
        {
            var t = QuestionNormalizer.Normalize(tag);
            return t.Length >= 2 && haystack.Contains(t, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>把标签合并进一个字符串字段（空格分隔，去重，保序）。</summary>
public static class TagList
{
    public static List<string> Parse(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(new[] { ' ', '\t', ',', '，', '、', '#', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(t => t.Trim())
                  .Where(t => t.Length > 0)
                  .Distinct(StringComparer.Ordinal)
                  .ToList();

    public static string Join(IEnumerable<string> tags) => string.Join(" ", Clean(tags));

    public static List<string> Clean(IEnumerable<string> tags)
    {
        var result = new List<string>();
        foreach (var raw in tags)
        {
            var t = Sanitize(raw);
            if (t.Length == 0 || result.Contains(t, StringComparer.Ordinal)) continue;
            result.Add(t);
        }
        return result;
    }

    /// <summary>标签要短、要干净：去掉引号井号、限制长度。</summary>
    public static string Sanitize(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().Trim('"', '\'', '`', '「', '」', '“', '”', '#', '-', '·', ' ');
        t = Regex.Replace(t, @"^[\d]+[\.、)]\s*", string.Empty);
        t = Regex.Replace(t, @"\s+", string.Empty);
        if (t.Length > 10) t = t[..10];
        return t.Trim();
    }
}

/// <summary>自动打标签：本地词表 / 云端模型 / 本机 Ollama 三档。</summary>
public sealed class AutoTagger
{
    private readonly AiChatClient _ai;

    public AutoTagger(AiChatClient? ai = null) => _ai = ai ?? new AiChatClient();

    public const string DefaultPrompt = """
        你是题库的分类助手。给出一道题，请给出 1~4 个中文短标签，用于分类检索。
        标签要覆盖：学科（如 数学/英语/政治）、知识点（如 函数/时态/宪法）、题型（单选/多选/判断/填空）。
        只输出一个 JSON 数组，不要解释，不要多余文字。例如：["数学","函数","单选"]
        """;

    /// <summary>给一道题打标签。</summary>
    public async Task<AutoTagResult> TagAsync(
        Question question,
        AutoTagConfig cfg,
        AppConfig appConfig,
        IReadOnlyList<string>? knownTags = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var engine = (cfg.Engine ?? "rule").ToLowerInvariant();

        if (engine is "ai" or "local")
        {
            var provider = PickProvider(engine, cfg, appConfig);
            if (provider is not null)
            {
                try
                {
                    var result = await TagWithAiAsync(question, provider, cfg, ct).ConfigureAwait(false);
                    result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                    if (result.Ok && result.Tags.Count > 0) return result;

                    // 模型没给出可用标签时退回词表，至少有个结果
                    var fallback = QuestionTagRules.Suggest(question, cfg.MaxTags, knownTags);
                    return new AutoTagResult
                    {
                        Ok = true,
                        Tags = fallback,
                        Engine = "词表（模型没给出可用标签）",
                        Error = result.Error,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var fallback = QuestionTagRules.Suggest(question, cfg.MaxTags, knownTags);
                    return new AutoTagResult
                    {
                        Ok = true,
                        Tags = fallback,
                        Engine = "词表（模型调用失败）",
                        Error = ex.Message,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                    };
                }
            }

            var ruleOnly = QuestionTagRules.Suggest(question, cfg.MaxTags, knownTags);
            return new AutoTagResult
            {
                Ok = true,
                Tags = ruleOnly,
                Engine = "词表（没有可用的模型）",
                Error = engine == "local" ? "没有启用的本机模型，先用词表打标签" : "没有启用的 AI 服务，先用词表打标签",
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }

        return new AutoTagResult
        {
            Ok = true,
            Tags = QuestionTagRules.Suggest(question, cfg.MaxTags, knownTags),
            Engine = "词表",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>批量打标签。返回处理过的题目列表（原地改 Tags 字段）。</summary>
    public async Task<(int Tagged, int Skipped, int Failed, List<string> Errors)> TagManyAsync(
        IEnumerable<Question> questions,
        AutoTagConfig cfg,
        AppConfig appConfig,
        IReadOnlyList<string>? knownTags = null,
        IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var list = questions.ToList();
        var targets = list.Where(q => cfg.Overwrite || string.IsNullOrWhiteSpace(q.Tags)).ToList();
        var skipped = list.Count - targets.Count;

        var tagged = 0;
        var failed = 0;
        var errors = new List<string>();
        var done = 0;
        var gate = new SemaphoreSlim(3);

        var tasks = targets.Select(async q =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await TagAsync(q, cfg, appConfig, knownTags, ct).ConfigureAwait(false);
                if (result.Tags.Count == 0)
                {
                    Interlocked.Increment(ref failed);
                    if (result.Error is not null && errors.Count < 5) errors.Add($"{Trim(q.Title)}：{result.Error}");
                    return;
                }

                var merged = cfg.Overwrite
                    ? TagList.Clean(result.Tags)
                    : TagList.Clean(TagList.Parse(q.Tags).Concat(result.Tags));

                q.Tags = TagList.Join(merged.Take(Math.Max(cfg.MaxTags, 1)));
                q.UpdatedAt = DateTimeOffset.Now;
                Interlocked.Increment(ref tagged);
                if (!string.IsNullOrWhiteSpace(result.Error) && errors.Count < 5)
                    errors.Add($"{Trim(q.Title)}：{result.Error}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                if (errors.Count < 5) errors.Add($"{Trim(q.Title)}：{ex.Message}");
            }
            finally
            {
                gate.Release();
                var now = Interlocked.Increment(ref done);
                progress?.Report((now, targets.Count, Trim(q.Title)));
            }
        }).ToList();

        if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);

        return (tagged, skipped, failed, errors);

        static string Trim(string? s) => string.IsNullOrEmpty(s) ? "(无题干)" : (s.Length <= 18 ? s : s[..18] + "…");
    }

    private AiProviderConfig? PickProvider(string engine, AutoTagConfig cfg, AppConfig appConfig)
    {
        var all = appConfig.AiProviders ?? new List<AiProviderConfig>();

        if (!string.IsNullOrWhiteSpace(cfg.ProviderId))
        {
            var pinned = all.FirstOrDefault(p => p.Id == cfg.ProviderId);
            if (pinned is not null && IsUsable(pinned)) return pinned;
        }

        if (engine == "local")
            return all.FirstOrDefault(p => p.ProviderKind == "ollama" && IsUsable(p));

        return AiAnswerService.SelectProviders(all).FirstOrDefault();
    }

    private static bool IsUsable(AiProviderConfig p) =>
        !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model)
        && (!string.IsNullOrWhiteSpace(p.ApiKey) || p.ProviderKind == "ollama");

    private async Task<AutoTagResult> TagWithAiAsync(
        Question question, AiProviderConfig provider, AutoTagConfig cfg, CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(cfg.Prompt) ? DefaultPrompt : cfg.Prompt;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(question.Title)) sb.Append("题干：").Append(question.Title!.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(question.Options)) sb.Append("选项：\n").Append(question.Options!.Trim()).Append('\n');
        sb.Append("已知答案：").Append(string.IsNullOrWhiteSpace(question.Answer) ? "（无）" : question.Answer).Append('\n');
        sb.Append("题型：").Append(question.Type.ToChinese()).Append('\n');
        sb.Append("最多 ").Append(Math.Clamp(cfg.MaxTags, 1, 8)).Append(" 个标签。");

        var messages = new List<AiChatMessage>
        {
            AiChatMessage.System(prompt),
            AiChatMessage.User(sb.ToString()),
        };

        var model = provider.Clone();
        model.MaxTokens = Math.Min(model.MaxTokens <= 0 ? 200 : model.MaxTokens, 400);
        model.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 5, 180);

        var result = await _ai.CompleteAsync(model, messages, jsonMode: true, ct).ConfigureAwait(false);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Content))
        {
            return new AutoTagResult
            {
                Ok = false,
                Error = result.Error ?? "模型没有返回内容",
                Engine = provider.Name,
            };
        }

        return new AutoTagResult
        {
            Ok = true,
            Tags = ParseTags(result.Content!, cfg.MaxTags),
            Engine = provider.Name + " · " + (result.Model ?? model.Model),
            Error = null,
        };
    }

    /// <summary>从模型输出里抠出标签数组，容忍 markdown 代码块和多余文字。</summary>
    public static List<string> ParseTags(string content, int maxTags)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
        }

        var parsed = new List<string>();
        var arrayStart = text.IndexOf('[');
        var arrayEnd = text.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            var json = text[arrayStart..(arrayEnd + 1)];
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            parsed.Add(item.GetString() ?? string.Empty);
                    }
                }
            }
            catch (JsonException)
            {
                // JSON 解析失败，退化成按分隔符切
            }
        }

        if (parsed.Count == 0)
        {
            var body = text;
            if (arrayStart >= 0) body = text[(arrayStart + 1)..];
            if (arrayEnd > 0 && arrayEnd < body.Length) body = body[..arrayEnd];
            body = body.Replace('[', ' ').Replace(']', ' ');
            parsed.AddRange(body.Split(new[] { ',', '，', '、', ' ', '\n', '\t', ';', '；', '#', '/' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return TagList.Clean(parsed).Take(Math.Clamp(maxTags, 1, 8)).ToList();
    }
}