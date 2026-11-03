using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using QBank.Core.Ai;
using QBank.Core.Models;
using QBank.Core.Ocs;

namespace QBank.Core.Search;

/// <summary>一条网页搜索结果。</summary>
public sealed class WebSearchBlock
{
    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;
}

/// <summary>从网页文本里推断出的答案。</summary>
public sealed class WebAnswerGuess
{
    /// <summary>答案：选择题是字母（多选连写，如 ABD），判断题是 对/错 或选项字母，填空是短语。</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>可信度 0~1。</summary>
    public double Score { get; set; }

    /// <summary>为什么这么判（给界面显示，便于用户判断可信度）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>命中的证据条数。</summary>
    public int Evidence { get; set; }

    /// <summary>提问题干在网页文本里的覆盖率，用来判断这条结果是不是真的在讲这道题。</summary>
    public double Coverage { get; set; }

    public string ScoreText => Score <= 0 ? "-" : Score.ToString("P0");
}

/// <summary>搜索引擎地址拼装。</summary>
public static class WebSearchEngines
{
    public const string So360 = "so360";
    public const string Sogou = "sogou";
    public const string Bing = "bing";
    public const string Baidu = "baidu";
    public const string Custom = "custom";

    /// <summary>默认引擎：360 搜索。实测百度会弹安全验证、必应结果常与查询无关、搜狗说得过去，360 最稳。</summary>
    public const string Default = So360;

    public static readonly string[] All = { So360, Sogou, Bing, Baidu, Custom };

    public static string DisplayName(string? engine) => (engine ?? Default).ToLowerInvariant() switch
    {
        So360 => "360 搜索",
        Sogou => "搜狗",
        Baidu => "百度",
        Custom => "自定义",
        _ => "必应",
    };

    /// <summary>
    /// 把查询词拼成搜索地址；自定义模板用 {q} 当占位符。
    /// 这是「首选地址」；实际抓取时按 <see cref="BuildUrls"/> 依次试。
    /// </summary>
    public static string BuildUrl(string? engine, string query, string? customTemplate = null)
        => BuildUrls(engine, query, customTemplate)[0];

    /// <summary>
    /// 一个引擎的可抓地址，按顺序试。
    /// 桌面版入口经常被限流（360 会 302 到 qcaptcha、搜狗会 302 到 antispider），
    /// 但**手机版入口往往还能用**，所以每个引擎都带一条手机版地址兜底。
    /// </summary>
    public static string[] BuildUrls(string? engine, string query, string? customTemplate = null)
    {
        var q = Uri.EscapeDataString(query ?? string.Empty);
        var e = (engine ?? Default).ToLowerInvariant();
        return e switch
        {
            So360 => new[]
            {
                $"https://www.so.com/s?q={q}",
                $"https://m.so.com/s?q={q}",
                $"https://m.so.com/s?q={q}&src=srp",
            },
            Sogou => new[]
            {
                $"https://www.sogou.com/web?query={q}",
                $"https://m.sogou.com/web/searchList.jsp?keyword={q}",
            },
            Baidu => new[]
            {
                $"https://www.baidu.com/s?wd={q}&ie=utf-8",
                $"https://m.baidu.com/s?wd={q}",
            },
            Custom when !string.IsNullOrWhiteSpace(customTemplate) =>
                new[]
                {
                    customTemplate!.Contains("{q}", StringComparison.OrdinalIgnoreCase)
                        ? customTemplate.Replace("{q}", q, StringComparison.OrdinalIgnoreCase)
                        : customTemplate + q,
                },
            // 必应：www 会 302 到 cn，直接打 cn 少跳一次
            _ => new[]
            {
                $"https://cn.bing.com/search?q={q}",
                $"https://www.bing.com/search?q={q}&setlang=zh-CN&cc=CN",
            },
        };
    }

    /// <summary>这个引擎能不能直接抓（自定义模板必须填了地址）。</summary>
    public static bool IsUsable(string? engine, string? customTemplate) =>
        (engine ?? Default).ToLowerInvariant() != Custom || !string.IsNullOrWhiteSpace(customTemplate);

    public static string DescribeLimitation(string? engine) => (engine ?? Default).ToLowerInvariant() switch
    {
        Baidu => "百度经常弹「安全验证」；抓不到内容时会自动换 360 搜索再试一次。",
        Sogou => "搜狗结果里广告较多，连查几题还可能被限流；抓不到时自动换 360 搜索。",
        Bing => "必应结构最规整，但部分地区的结果可能跟查询对不上；不对时会自动换 360 搜索。",
        _ => string.Empty,
    };

    /// <summary>抓不到内容时依次换着试的引擎（排掉当前这个）。</summary>
    public static IEnumerable<string> Fallbacks(string? engine)
    {
        var current = (engine ?? Default).ToLowerInvariant();
        foreach (var e in new[] { So360, Sogou, Bing })
            if (!string.Equals(e, current, StringComparison.OrdinalIgnoreCase)) yield return e;
    }
}

/// <summary>把搜索引擎的结果页切成「标题 / 链接 / 摘要」，并抽出一段可用于判分的正文。</summary>
public static class HtmlSlicer
{
    private static readonly Regex ScriptStyle = new(
        @"<(script|style|noscript|svg|iframe)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockBreak = new(
        @"</?(p|div|li|ul|ol|h[1-6]|br|tr|td|table|section|article|header|footer|blockquote)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t\u00a0]{2,}", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"(\r?\n){3,}", RegexOptions.Compiled);

    /// <summary>HTML 转纯文本（块级标签换行，其余标签丢弃，实体解码）。</summary>
    public static string ToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var s = Comment.Replace(html, " ");
        s = ScriptStyle.Replace(s, " ");
        s = BlockBreak.Replace(s, "\n");
        s = Tag.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace('\u00a0', ' ');
        s = Spaces.Replace(s, " ");
        s = BlankLines.Replace(s, "\n\n");

        var sb = new StringBuilder(s.Length);
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>按引擎的结果容器切分页面。</summary>
    public static List<WebSearchBlock> Slice(string? html, string? engine)
    {
        var blocks = new List<WebSearchBlock>();
        if (string.IsNullOrEmpty(html)) return blocks;

        var containers = ContainerPatterns(engine);
        var chunks = new List<string>();
        foreach (var pattern in containers)
        {
            chunks = SplitBy(html!, pattern);
            if (chunks.Count >= 2) break;
        }

        if (chunks.Count < 2)
        {
            // 退路：整页当一个块，标题取 <title>
            var title = ExtractFirst(html!, @"<title[^>]*>(.*?)</title>");
            var text = ToText(html!);
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add(new WebSearchBlock { Order = 1, Title = ToText(title), Snippet = text });
            return blocks;
        }

        var order = 0;
        foreach (var chunk in chunks)
        {
            var title = ToText(ExtractFirst(chunk, @"<h[1-3][^>]*>(.*?)</h[1-3]>"));
            // 360 的真实地址藏在 data-mdurl 里（href 是跳转链接），优先用它
            var url = WebUtility.HtmlDecode(ExtractFirst(chunk, @"data-mdurl\s*=\s*""(https?://[^""]+)"""));
            if (url.Length == 0)
                url = WebUtility.HtmlDecode(ExtractFirst(chunk, @"href\s*=\s*""(https?://[^""]+)"""));
            var body = ToText(chunk);

            if (title.Length > 0) body = body.Replace(title, string.Empty, StringComparison.Ordinal).Trim();
            if (body.Length > 0) body = body.TrimStart('·', '-', '|', ' ').Trim();

            if (title.Length == 0 && body.Length == 0) continue;
            if (IsBoilerplate(title) && body.Length < 20) continue;

            order++;
            blocks.Add(new WebSearchBlock
            {
                Order = order,
                Title = title,
                Url = url,
                Snippet = body.Length > 1200 ? body[..1200] : body,
            });
        }

        return blocks;
    }

    private static string[] ContainerPatterns(string? engine) => (engine ?? WebSearchEngines.Default).ToLowerInvariant() switch
    {
        WebSearchEngines.Baidu => new[]
        {
            "<div[^>]*class=\"[^\"]*result[^\"]*c-container",
            "<div[^>]*class=\"[^\"]*c-container",
        },
        WebSearchEngines.Sogou => new[]
        {
            // 手机版搜狗的结果块叫 vrResult（桌面版是 vrwrap）
            "<div[^>]*class=\"[^\"]*vrwrap",
            "<div[^>]*class=\"[^\"]*vrResult",
            "<div[^>]*class=\"[^\"]*rb\"",
        },
        WebSearchEngines.So360 => new[]
        {
            "<li[^>]*class=\"[^\"]*res-list",
            "<li[^>]*class=\"[^\"]*res-item",
            // 手机版：<div class="g-card res-list og">，注意 class 里 res-list 不一定打头
            "<div[^>]*class=\"[^\"]*res-list",
        },
        _ => new[] { "<li[^>]*class=\"[^\"]*b_algo", "<li[^>]*class=\"[^\"]*b_ans" },
    };

    /// <summary>按正则标记切块：每块从自己的标记处开始，到下一个标记前结束。</summary>
    private static List<string> SplitBy(string html, string pattern)
    {
        var result = new List<string>();
        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
        if (matches.Count == 0) return result;

        for (var i = 0; i < matches.Count && i < 40; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            if (end > start) result.Add(html[start..end]);
        }
        return result;
    }

    private static string ExtractFirst(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>导航、登录、广告这类没有信息量的行。</summary>
    public static bool IsBoilerplate(string? line)
    {
        var t = (line ?? string.Empty).Trim();
        if (t.Length == 0) return true;
        if (t.Length <= 3) return true;

        foreach (var junk in BoilerplateWords)
            if (t.Contains(junk, StringComparison.OrdinalIgnoreCase)) return true;

        // 全是标点/符号
        return !t.Any(char.IsLetterOrDigit);
    }

    /// <summary>
    /// 导航条、页脚、广告这类没有信息量的行。
    /// 注意：**不要**把「百度 / 搜狗 / 必应 / 微软」这类站点名算进来 —— 它们经常出现在真实结果的标题里
    /// （「中国的首都是哪座城市 - 百度知道」），当成垃圾会把好结果整条丢掉。
    /// 页面级「是否被反爬拦下」由 WebSearchSource.LooksBlocked 单独判断。
    /// </summary>
    private static readonly string[] BoilerplateWords =
    {
        "百度安全验证", "安全验证", "请点击继续访问", "网络不给力", "操作过于频繁",
        "登录", "注册", "下载客户端", "意见反馈",
        "网页图片视频", "搜索工具", "关于我们", "隐私政策", "服务条款", "Cookie",
        "京ICP", "公安备案", "广告",
    };
}

/// <summary>从网页文本里推断答案。全部是本地启发式，不花钱。</summary>
public static class WebAnswerMatcher
{
    private static readonly Regex OptionLine = new(
        @"^\s*[（(【\[]?\s*([A-Ha-h])\s*[）)】\]]?\s*[.．、,，:：·]\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex OptionInline = new(
        @"(?:^|\s|\|)([A-Ha-h])\s*[.．、,，:：]\s*([^\s|]+(?:\s?[^\s|]+)*)",
        RegexOptions.Compiled);

    private static readonly string[] StrongPatterns =
    {
        @"正确答案\s*[是为:：]?\s*([A-Ha-h]{1,8})(?![A-Za-z])",
        @"答案\s*[是为:：]?\s*([A-Ha-h]{1,8})(?![A-Za-z])",
        @"([A-Ha-h]{1,8})\s*为正确答案",
        @"参考答案\s*[是为:：]?\s*([A-Ha-h]{1,8})(?![A-Za-z])",
    };

    private static readonly string[] WeakPatterns =
    {
        @"[（(]\s*([A-Ha-h])\s*[）)]",
        @"选\s*([A-Ha-h]{1,4})(?![A-Za-z])",
    };

    /// <summary>解析选项，返回 (字母, 文本) 列表。支持每行一个，也支持一行里连写。</summary>
    public static List<(string Letter, string Text)> ParseOptions(string? options)
    {
        var result = new List<(string Letter, string Text)>();
        if (string.IsNullOrWhiteSpace(options)) return result;

        var text = options!.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('|', '│').Trim();
            if (line.Length == 0) continue;

            var m = OptionLine.Match(line);
            if (m.Success)
            {
                Add(result, m.Groups[1].Value, m.Groups[2].Value);
                continue;
            }

            // 一行里塞了多个选项："A. 上海 B. 北京 C. 广州"
            var inline = OptionInline.Matches(line);
            if (inline.Count >= 2)
            {
                foreach (Match im in inline)
                    Add(result, im.Groups[1].Value, im.Groups[2].Value);
            }
        }

        // 去重（同一个字母重复出现时保留更长的文本）
        var merged = new List<(string Letter, string Text)>();
        foreach (var item in result)
        {
            var at = merged.FindIndex(x => x.Letter == item.Letter);
            if (at < 0) merged.Add(item);
            else if (item.Text.Length > merged[at].Text.Length) merged[at] = item;
        }
        merged.Sort((a, b) => string.CompareOrdinal(a.Letter, b.Letter));
        return merged;

        static void Add(List<(string Letter, string Text)> list, string letter, string text)
        {
            var l = letter.ToUpperInvariant();
            var t = text.Trim().TrimEnd('|', '│').Trim();
            if (l.Length == 1 && t.Length > 0) list.Add((l, t));
        }
    }

    /// <summary>提问题干有多少比例出现在网页文本里（0~1），用来判断网页讲的是不是这道题。</summary>
    public static double Coverage(string? question, string? corpus)
    {
        var q = QuestionNormalizer.Normalize(question);
        if (q.Length < 4) return 1;

        var text = QuestionNormalizer.Normalize(corpus);
        if (text.Length == 0) return 0;

        var gram = 3;
        if (q.Length < gram * 2) gram = 2;

        var total = 0;
        var hit = 0;
        for (var i = 0; i + gram <= q.Length; i++)
        {
            total++;
            if (text.Contains(q.Substring(i, gram), StringComparison.Ordinal)) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>核心：给题干+选项+网页正文，猜答案；没把握返回 null。</summary>
    public static WebAnswerGuess? Guess(SearchRequest request, string? corpus, WebSearchConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(corpus)) return null;

        var text = Corpus(corpus!);
        if (text.Length < 8) return null;

        var coverage = Coverage(request.Title, corpus);
        var options = ParseOptions(request.Options);
        var type = request.Type;

        if (type == QuestionType.Unknown && options.Count >= 2)
            type = options.Count == 2 ? QuestionType.Judgement : QuestionType.Single;

        // 1) 判断题：先看「答案是对/错」这种明确说法
        if (type == QuestionType.Judgement || IsJudgementPair(options))
        {
            var judge = GuessJudgement(text, options, coverage);
            if (judge is not null && judge.Score >= cfg.MinScore) return judge;
        }

        // 2) 选择题：按「答案：X」这类明确写法投票
        if (options.Count >= 2 && type != QuestionType.Completion)
        {
            var byLetter = GuessByLetters(text, request, options, type, coverage);
            if (byLetter is not null && byLetter.Score >= cfg.MinScore) return byLetter;

            var byText = GuessByOptionText(text, options, coverage);
            if (byText is not null && byText.Score >= cfg.MinScore) return byText;

            // 有明确字母证据但分不够时，仍返回（让上层决定要不要展示）
            if (byLetter is not null) return byLetter;
        }

        // 3) 填空/简答：抓「答案是 xxx」
        var blank = GuessBlank(text, coverage);
        if (blank is not null && blank.Score >= cfg.MinScore) return blank;

        return null;
    }

    private static bool IsJudgementPair(List<(string Letter, string Text)> options)
    {
        if (options.Count != 2) return false;
        var joined = string.Concat(options.Select(o => o.Text));
        return joined.Contains("正确", StringComparison.Ordinal) || joined.Contains("错误", StringComparison.Ordinal)
            || (joined.Contains('对', StringComparison.Ordinal) && joined.Contains('错', StringComparison.Ordinal))
            || (joined.Contains('是', StringComparison.Ordinal) && joined.Contains('否', StringComparison.Ordinal));
    }

    private static string Corpus(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (HtmlSlicer.IsBoilerplate(t)) continue;
            sb.Append(t).Append('\n');
        }
        return sb.ToString();
    }

    private static WebAnswerGuess? GuessJudgement(string text, List<(string Letter, string Text)> options, double coverage)
    {
        var positive = CountMatches(text, @"(?:答案|判断|结论)?\s*(?:是|为|：|:)?\s*(正确|对的|是|√|对的|true)(?![一-龥])");
        var negative = CountMatches(text, @"(?:答案|判断|结论)?\s*(?:是|为|：|:)?\s*(错误|不对|否|×|false)(?![一-龥])");

        var strongPositive = CountMatches(text, @"(?:正确|对)\s*[。.!！]?\s*$", RegexOptions.Multiline);
        var strongNegative = CountMatches(text, @"(?:错误|不对|错)\s*[。.!！]?\s*$", RegexOptions.Multiline);

        positive += strongPositive;
        negative += strongNegative;

        if (positive == 0 && negative == 0) return null;

        var winner = positive >= negative ? "正确" : "错误";
        var win = Math.Max(positive, negative);
        var lose = Math.Min(positive, negative);

        var dominance = win >= 2 * Math.Max(lose, 1) ? 1.0 : 0.75;
        var score = Math.Min(0.95, 0.52 + 0.1 * (win - 1)) * dominance * ConfidenceFactor(coverage);

        var answer = JudgementMapper.Map(winner, string.Join("\n", options.Select(o => o.Letter + ". " + o.Text)));
        return new WebAnswerGuess
        {
            Answer = string.IsNullOrWhiteSpace(answer) ? winner : answer,
            Score = Math.Round(score, 4),
            Evidence = win,
            Coverage = Math.Round(coverage, 4),
            Reason = $"网页里出现「{winner}」{win} 次、反方 {lose} 次（题干吻合度 {coverage:P0}）",
        };
    }

    private static WebAnswerGuess? GuessByLetters(
        string text, SearchRequest request, List<(string Letter, string Text)> options, QuestionType type, double coverage)
    {
        var votes = new Dictionary<string, double>(StringComparer.Ordinal);
        var hits = new List<(string Group, double Weight, string Pattern)>();
        var known = options.Select(o => o.Letter).ToHashSet(StringComparer.Ordinal);

        foreach (var pattern in StrongPatterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var group = NormalizeLetters(m.Groups[1].Value, known);
                if (group.Length == 0) continue;
                votes[group] = votes.GetValueOrDefault(group) + 2;
                hits.Add((group, 2, "答案写在网页里"));
            }
        }

        foreach (var pattern in WeakPatterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var group = NormalizeLetters(m.Groups[1].Value, known);
                if (group.Length == 0) continue;
                votes[group] = votes.GetValueOrDefault(group) + 1;
                hits.Add((group, 1, "网页里提到该选项"));
            }
        }

        if (votes.Count == 0) return null;

        var best = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First();
        var total = votes.Values.Sum();
        var second = votes.Where(v => v.Key != best.Key).Select(v => v.Value).DefaultIfEmpty(0).Max();

        // 多选：字母连写的才认；单选却写成多个字母通常是别的题，降权
        var isMulti = type == QuestionType.Multiple || IsMultiOptionAnswer(request.Options);
        if (!isMulti && best.Key.Length > 1) return null;

        var weight = best.Value;
        var dominance = second <= 0 ? 1.0 : Math.Min(1.0, weight / (weight + second * 0.6));
        var votesShare = total <= 0 ? 0 : weight / total;
        var score = Math.Min(0.95, 0.55 + 0.1 * (weight - 1)) * dominance * (0.75 + 0.25 * votesShare) * ConfidenceFactor(coverage);

        var letter = isMulti && best.Key.Length > 1
            ? best.Key
            : best.Key[..1];

        return new WebAnswerGuess
        {
            Answer = letter,
            Score = Math.Round(score, 4),
            Evidence = (int)Math.Round(weight),
            Coverage = Math.Round(coverage, 4),
            Reason = $"网页里 {weight:0.#} 次指向「{letter}」（其它选项 {second:0.#} 次，题干吻合度 {coverage:P0}）",
        };
    }

    private static bool IsMultiOptionAnswer(string? options)
    {
        var text = options ?? string.Empty;
        return text.Contains("多选", StringComparison.Ordinal) || text.Contains("【多选】", StringComparison.Ordinal);
    }

    private static WebAnswerGuess? GuessByOptionText(string text, List<(string Letter, string Text)> options, double coverage)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (letter, label) in options)
        {
            var key = QuestionNormalizer.Normalize(label);
            if (key.Length < 2) continue;
            counts[letter] = Occurrences(text, key);
        }

        if (counts.Count == 0 || counts.Values.Max() < 2) return null;

        var best = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First();
        var second = counts.Where(c => c.Key != best.Key).Select(c => c.Value).DefaultIfEmpty(0).Max();
        if (best.Value < 2 * Math.Max(second, 1)) return null;

        var score = Math.Min(0.88, 0.48 + 0.07 * best.Value) * ConfidenceFactor(coverage);
        return new WebAnswerGuess
        {
            Answer = best.Key,
            Score = Math.Round(score, 4),
            Evidence = best.Value,
            Coverage = Math.Round(coverage, 4),
            Reason = $"选项内容在网页里出现 {best.Value} 次，其它选项最多 {second} 次（题干吻合度 {coverage:P0}）",
        };
    }

    private static WebAnswerGuess? GuessBlank(string text, double coverage)
    {
        foreach (var pattern in new[]
        {
            @"答案\s*[是为:：]\s*([^\n。；;，,]{1,24})",
            @"正确答案\s*[:：]?\s*([^\n。；;，,]{1,24})",
        })
        {
            var m = Regex.Match(text, pattern);
            if (!m.Success) continue;

            var value = m.Groups[1].Value.Trim().Trim('"', '\'', '「', '」', '“', '”');
            if (value.Length == 0 || value.Length > 24) continue;
            if (value.StartsWith('A') && value.Length <= 8 && value.All(c => char.IsLetter(c) || c == '#')) continue;

            var score = 0.66 * ConfidenceFactor(coverage);
            return new WebAnswerGuess
            {
                Answer = value,
                Score = Math.Round(score, 4),
                Evidence = 1,
                Coverage = Math.Round(coverage, 4),
                Reason = $"网页里写着「{value}」（题干吻合度 {coverage:P0}）",
            };
        }
        return null;
    }

    /// <summary>题干吻合度太低时整体降权，避免拿别的题的答案。</summary>
    private static double ConfidenceFactor(double coverage) => coverage switch
    {
        >= 0.6 => 1.0,
        >= 0.45 => 0.9,
        >= 0.3 => 0.7,
        _ => 0.45,
    };

    private static string NormalizeLetters(string raw, HashSet<string> known)
    {
        var letters = new List<char>();
        foreach (var ch in raw.ToUpperInvariant())
        {
            if (ch < 'A' || ch > 'H') continue;
            if (known.Count > 0 && !known.Contains(ch.ToString())) continue;
            if (!letters.Contains(ch)) letters.Add(ch);
        }
        letters.Sort();
        return new string(letters.ToArray());
    }

    private static int Occurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while (true)
        {
            var at = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            count++;
            index = at + needle.Length;
            if (count > 50) break;
        }
        return count;
    }

    private static int CountMatches(string text, string pattern, RegexOptions extra = RegexOptions.None) =>
        Regex.Matches(text, pattern, RegexOptions.IgnoreCase | extra).Count;

    /// <summary>把网页文本加上题干一起判分（题干本身也常带「答案」）。</summary>
    public static WebAnswerGuess? GuessFromBlocks(SearchRequest request, IEnumerable<WebSearchBlock> blocks, WebSearchConfig cfg)
    {
        var corpus = new StringBuilder();
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.Title)) corpus.Append(block.Title).Append('\n');
            if (!string.IsNullOrWhiteSpace(block.Snippet))
            {
                var snippet = block.Snippet.Length > cfg.SnippetChars ? block.Snippet[..cfg.SnippetChars] : block.Snippet;
                corpus.Append(snippet).Append('\n');
            }
        }
        return Guess(request, corpus.ToString(), cfg);
    }
}

/// <summary>网页搜索来源：搜索引擎直连，命中可信答案后可以顶替 AI，省 token。</summary>
/// <summary>一次抓取的结果（含实际用上的引擎，因为可能会换）。</summary>
public sealed class WebFetchResult
{
    public List<WebSearchBlock> Blocks { get; init; } = new();
    public string? Error { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Engine { get; init; } = WebSearchEngines.Default;
    /// <summary>换过引擎时的一句话说明。</summary>
    public string? Notice { get; init; }
    public string EngineName => WebSearchEngines.DisplayName(Engine);
    public bool Ok => Blocks.Count > 0 && Error is null;
}

public sealed class WebSearchSource
{
    private readonly HttpClient _http;

    public WebSearchSource(HttpClient? http = null) => _http = http ?? HttpClients.ForWebScraping;

    /// <summary>把题干（可选带选项）拼成查询词。</summary>
    public static string BuildQuery(SearchRequest request, WebSearchConfig cfg)
    {
        var title = QuestionNormalizer.StripDecorations(request.Title).Trim();
        title = Regex.Replace(title, @"\s+", " ");
        if (title.Length > Math.Max(cfg.QueryChars, 8)) title = title[..Math.Max(cfg.QueryChars, 8)];

        if (!cfg.QueryWithOptions || string.IsNullOrWhiteSpace(request.Options)) return title;

        var options = request.Options.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (options.Length > 60) options = options[..60];
        return title + " " + options;
    }

    /// <summary>抓一次搜索结果。</summary>
    public async Task<WebFetchResult> FetchAsync(
        string query, WebSearchConfig cfg, CancellationToken ct = default)
    {
        var primary = (cfg.Engine ?? WebSearchEngines.Default).ToLowerInvariant();
        var tried = new List<string>();
        var problems = new List<string>();
        var lastUrl = WebSearchEngines.BuildUrl(primary, query, cfg.CustomUrlTemplate);

        if (!WebSearchEngines.IsUsable(primary, cfg.CustomUrlTemplate))
            return new WebFetchResult { Error = "自定义搜索没有填地址模板", Url = lastUrl, Engine = primary };

        // 先试设定的引擎，抓不到再依次换别的
        var candidates = new List<string> { primary };
        if (primary != WebSearchEngines.Custom)
            candidates.AddRange(WebSearchEngines.Fallbacks(primary));

        // 刚被限流过的引擎排到后面：它这会儿大概率还是验证码页，
        // 先试它只会白等一轮超时，而用户要的是答案，不是尊重偏好。
        var ordered = candidates
            .OrderBy(e => InCooldown(e) ? 1 : 0)
            .ThenBy(e => candidates.IndexOf(e))
            .ToList();

        foreach (var engine in ordered)
        {
            ct.ThrowIfCancellationRequested();

            // 同一个引擎可能有好几条地址（桌面版 + 手机版）：桌面版被限流时手机版常常还能用。
            // 但冷却中的引擎只试第一条 —— 别为了一个已知不通的站把整体拖到十几秒。
            var urls = WebSearchEngines.BuildUrls(engine, query, engine == primary ? cfg.CustomUrlTemplate : null);
            if (InCooldown(engine) && urls.Length > 1) urls = urls[..1];
            tried.Add(engine);

            string? engineProblem = null;
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                lastUrl = url;

                var attempt = await TryFetchAsync(url, engine, cfg, ct).ConfigureAwait(false);
                if (attempt.Blocks is null)
                {
                    // 每个引擎只留第一条问题，别再往提示里堆「桌面版失败；curl 也失败；手机版失败…」
                    engineProblem ??= attempt.Problem ?? $"{WebSearchEngines.DisplayName(engine)}：抓取失败";

                    // 被限流/验证码挡住的引擎记一笔冷却，这一阵子就别再优先试它了
                    if (LooksRateLimited(engineProblem)) MarkCooldown(engine);
                    continue;
                }

                var blocks = attempt.Blocks;
                if (blocks.Count > cfg.MaxResults) blocks = blocks.Take(Math.Max(cfg.MaxResults, 1)).ToList();

                var notice = tried.Count > 1
                    ? $"默认引擎抓不到，改用{WebSearchEngines.DisplayName(engine)}（{string.Join("；", problems)}）"
                    : attempt.Note;

                return new WebFetchResult { Blocks = blocks, Url = url, Engine = engine, Notice = notice };
            }

            if (engineProblem is not null) problems.Add(engineProblem);
        }

        return new WebFetchResult
        {
            Error = problems.Count > 0 ? string.Join("；", problems) : "搜索结果页没有解析出内容（可能被反爬拦了）",
            Url = lastUrl,
            Engine = primary,
        };
    }

    /// <summary>引擎被限流后的冷却时间：这段时间内不再优先试它。</summary>
    private static readonly TimeSpan CooldownLength = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, DateTime> CooldownUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个引擎是不是刚被限流过。</summary>
    public static bool InCooldown(string engine) =>
        CooldownUntil.TryGetValue(engine, out var until) && DateTime.UtcNow < until;

    private static void MarkCooldown(string engine) =>
        CooldownUntil[engine] = DateTime.UtcNow + CooldownLength;

    /// <summary>清掉冷却（设置页「重新试一次」时用）。</summary>
    public static void ResetCooldown() => CooldownUntil.Clear();

    /// <summary>这些说法基本就是「被限流 / 撞验证码」。</summary>
    private static bool LooksRateLimited(string problem) =>
        problem.Contains("验证页", StringComparison.Ordinal) ||
        problem.Contains("反爬", StringComparison.Ordinal) ||
        problem.Contains("验证码", StringComparison.Ordinal) ||
        problem.Contains("限流", StringComparison.Ordinal);

    /// <summary>一个引擎的一次抓取：先用 HttpClient，TLS 谈崩了就用系统自带的 curl.exe 再试一遍。</summary>
    private async Task<(List<WebSearchBlock>? Blocks, string? Problem, string? Note)> TryFetchAsync(
        string url, string engine, WebSearchConfig cfg, CancellationToken ct)
    {
        var name = WebSearchEngines.DisplayName(engine);
        string? httpProblem;

        try
        {
            var html = await GetHtmlAsync(url, cfg, engine, ct).ConfigureAwait(false);

            if (LooksBlocked(html, engine))
                return (null, $"{name}：被反爬拦了", null);

            var blocks = Filter(HtmlSlicer.Slice(html, engine));
            if (blocks.Count > 0) return (blocks, null, null);

            httpProblem = $"{name}：没解析出结果";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            httpProblem = $"{name}：超时";
        }
        catch (Exception ex)
        {
            httpProblem = DescribeHttpProblem(name, ex);
        }

        // 兜底：有些站点的 TLS 协商 .NET 谈不下来（比如 360 搜索不接受 TLS 1.3），
        // 系统自带的 curl.exe 却能抓。Windows 10 1803 以后都自带它。
        var curlPath = FindCurl();
        if (curlPath is null) return (null, httpProblem, null);

        var (curlHtml, curlError) = await FetchWithCurlAsync(curlPath, url, engine, cfg, ct).ConfigureAwait(false);
        if (curlHtml is null)
            return (null, $"{httpProblem}；curl 兜底也失败：{curlError}", null);

        if (LooksBlocked(curlHtml, engine))
            return (null, $"{httpProblem}；curl 兜底拿到的是反爬页", null);

        var curlBlocks = Filter(HtmlSlicer.Slice(curlHtml, engine));
        if (curlBlocks.Count == 0)
            return (null, $"{httpProblem}；curl 兜底也没解析出结果", null);

        return (curlBlocks, null, $"这个站点用 .NET 直连谈不下 TLS，已改用系统 curl 抓到（{name}）");
    }

    private static List<WebSearchBlock> Filter(List<WebSearchBlock> blocks) =>
        blocks.Where(b => !HtmlSlicer.IsBoilerplate(b.Title) || b.Snippet.Length >= 30).ToList();

    /// <summary>
    /// 把抓取异常翻成人话。3xx 单独解释：搜索引擎被限流时会把请求 302 跳到验证页
    /// （360 是 qcaptcha.so.com，搜狗是 antispider），直接抛「302 (Found)」用户看不懂。
    /// </summary>
    private static string DescribeHttpProblem(string name, Exception ex)
    {
        var msg = HttpClients.ExplainNetworkError(ex);
        var looksRedirect =
            msg.Contains("301", StringComparison.Ordinal) ||
            msg.Contains("302", StringComparison.Ordinal) ||
            msg.Contains("307", StringComparison.Ordinal) ||
            msg.Contains("308", StringComparison.Ordinal);

        if (looksRedirect)
            return $"{name}：被跳去验证页了（这个搜索站暂时在限流，过一会儿再试或换个引擎）";

        return $"{name}：{msg}";
    }

    /// <summary>系统自带的 curl.exe 在哪（Windows 10 1803+ 都有）。</summary>
    public static string? FindCurl()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var system = Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (File.Exists(system)) return system;
        }
        catch
        {
            // 忽略
        }

        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), "curl.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private static async Task<(string? Html, string? Error)> FetchWithCurlAsync(
        string curlPath, string url, string engine, WebSearchConfig cfg, CancellationToken ct)
    {
        var timeout = Math.Clamp(cfg.TimeoutSeconds, 5, 60);
        var psi = new ProcessStartInfo(curlPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--show-error");
        psi.ArgumentList.Add("--location");
        psi.ArgumentList.Add("--compressed");
        // Windows 自带的 curl 走 schannel，默认会去查证书吊销状态；那个服务器在国内
        // 常常连不上，于是报 CRYPT_E_REVOCATION_OFFLINE 直接失败。抓公开页面不查这个。
        psi.ArgumentList.Add("--ssl-no-revoke");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add(timeout.ToString());
        psi.ArgumentList.Add("--user-agent");
        psi.ArgumentList.Add(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        psi.ArgumentList.Add("--header");
        psi.ArgumentList.Add("Accept-Language: zh-CN,zh;q=0.9,en;q=0.8");
        var referer = engine.ToLowerInvariant() switch
        {
            WebSearchEngines.So360 => "https://www.so.com/",
            WebSearchEngines.Sogou => "https://www.sogou.com/",
            WebSearchEngines.Baidu => "https://www.baidu.com/",
            WebSearchEngines.Bing => "https://www.bing.com/",
            _ => null,
        };
        if (referer is not null)
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(referer);
        }
        psi.ArgumentList.Add(url);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (null, "curl 起不来");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(timeout + 5));

            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return (null, "curl 超时");
            }

            var html = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                return (null, $"curl 退出码 {process.ExitCode}：{stderr.Trim()}");
            if (string.IsNullOrWhiteSpace(html)) return (null, "curl 什么都没返回");
            return (html, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, HttpClients.ExplainNetworkError(ex));
        }
    }

    /// <summary>被反爬拦下来的页面长什么样：安全验证页、验证码页、空壳页。公开是为了能离线自测。</summary>
    public static bool LooksBlocked(string? html, string? engine)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;

        // 整个页面小得可怜，基本就是一张提示页
        if (html.Length < 4000) return true;

        if (html.Contains("antispider", StringComparison.OrdinalIgnoreCase)) return true;
        if (html.Contains("百度安全验证") || html.Contains("网络不给力，请稍后重试")) return true;
        if (html.Contains("请输入验证码") || html.Contains("人机验证") || html.Contains("访问过于频繁")) return true;
        if (html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)) return true;

        // 注意：**必须扫整页**。以前只扫前 20000 字符，结果必应那种结果区在第 5 万多字符
        // 的页面永远被判成「被反爬拦了」—— 明明抓到了完整结果却没解析，网页搜索就等于没用。
        var markers = engine?.ToLowerInvariant() switch
        {
            WebSearchEngines.So360 => new[] { "res-list", "res-title", "g-card" },
            WebSearchEngines.Sogou => new[] { "vrwrap", "vrResult", "vr-tit", "rb\"" },
            WebSearchEngines.Baidu => new[] { "c-container", "result" },
            _ => new[] { "b_algo", "b_results", "b_ans" },
        };
        return markers is not null && !markers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>调试用：直接取搜索引擎返回的原始 HTML（排错时看反爬拦没拦）。</summary>
    public async Task<string> FetchHtmlAsync(string query, WebSearchConfig cfg, CancellationToken ct = default)
    {
        var engine = (cfg.Engine ?? WebSearchEngines.Default).ToLowerInvariant();
        var url = WebSearchEngines.BuildUrl(engine, query, cfg.CustomUrlTemplate);
        return await GetHtmlAsync(url, cfg, engine, ct).ConfigureAwait(false);
    }

    private async Task<string> GetHtmlAsync(string url, WebSearchConfig cfg, string? engine, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 3, 60)));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        // 带上站点自己的 Referer，看起来像从首页点进来的，能少挨反爬
        var referer = engine?.ToLowerInvariant() switch
        {
            WebSearchEngines.So360 => "https://www.so.com/",
            WebSearchEngines.Sogou => "https://www.sogou.com/",
            WebSearchEngines.Baidu => "https://www.baidu.com/",
            WebSearchEngines.Bing => "https://www.bing.com/",
            _ => null,
        };
        if (referer is not null) req.Headers.TryAddWithoutValidation("Referer", referer);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>作为一个搜题来源跑一次。</summary>
    public async Task<SourceOutcome> SearchAsync(SearchRequest request, AppConfig cfg, CancellationToken ct = default)
    {
        var web = cfg.WebSearch;
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = "web",
            SourceName = "网页搜索",
            Kind = SourceKind.Web,
        };

        if (!web.Enabled)
        {
            outcome.Notices.Add("网页搜索已关闭");
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return outcome;
        }

        var query = BuildQuery(request, web);
        if (string.IsNullOrWhiteSpace(query))
        {
            outcome.Notices.Add("题干太短，没法拿去搜");
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return outcome;
        }

        outcome.RawResponse = query;

        var fetch = await FetchAsync(query, web, ct).ConfigureAwait(false);
        if (fetch.Notice is not null) outcome.Notices.Add(fetch.Notice);
        if (fetch.Error is not null) outcome.Notices.Add(fetch.Error);

        var blocks = fetch.Blocks;
        if (blocks.Count == 0)
        {
            outcome.Ok = fetch.Error is null;
            outcome.Error = fetch.Error;
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return outcome;
        }

        var guess = WebAnswerMatcher.GuessFromBlocks(request, blocks, web);
        if (guess is null)
        {
            outcome.Notices.Add($"搜到 {blocks.Count} 条网页，但没能确定答案");
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return outcome;
        }

        if (guess.Score < web.MinScore)
        {
            outcome.Notices.Add($"网页里似乎有答案但把握不够（{guess.Score:P0}）：{guess.Reason}");
            outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return outcome;
        }

        var source = blocks.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Url))?.Url;
        var note = new StringBuilder();
        note.Append(fetch.EngineName).Append("：").Append(guess.Reason);
        if (!string.IsNullOrWhiteSpace(source)) note.Append("\n").Append(source);

        outcome.Hits.Add(new SearchHit
        {
            Question = string.Empty,
            Answer = guess.Answer,
            Score = guess.Score,
            Note = note.ToString(),
        });

        outcome.Ok = true;
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }
}