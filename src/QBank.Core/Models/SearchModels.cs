namespace QBank.Core.Models;

/// <summary>题目来源大类。</summary>
public enum SourceKind
{
    Cache,
    LocalBank,
    LanShare,
    Ocs,
    Ai,
    Web,
}

public static class SourceKindExtensions
{
    public static string ToChinese(this SourceKind kind) => kind switch
    {
        SourceKind.Cache => "本地缓存",
        SourceKind.LocalBank => "本地题库",
        SourceKind.LanShare => "局域网共享",
        SourceKind.Ocs => "OCS题库",
        SourceKind.Ai => "AI模型",
        SourceKind.Web => "网页搜索",
        _ => "未知",
    };
}

/// <summary>一次搜题的输入。</summary>
public sealed class SearchRequest
{
    public string Title { get; set; } = string.Empty;

    public QuestionType Type { get; set; } = QuestionType.Unknown;

    public string Options { get; set; } = string.Empty;

    /// <summary>单个来源最多返回多少条候选。</summary>
    public int Limit { get; set; } = 8;

    /// <summary>是否允许直接命中本地缓存后短路返回。</summary>
    public bool UseCache { get; set; } = true;

    /// <summary>跳过缓存，强制全来源搜索。</summary>
    public bool ForceRefresh { get; set; }

    public bool UseLocalBank { get; set; } = true;
    public bool UseLanShare { get; set; } = true;
    public bool UseOcs { get; set; } = true;
    public bool UseAi { get; set; } = true;

    /// <summary>用搜索引擎在网页里找答案（可信命中时可不调 AI）。</summary>
    public bool UseWeb { get; set; } = true;

    /// <summary>只使用这些来源（按 Id 过滤）；为空表示全部启用来源。</summary>
    public List<string> OnlySourceIds { get; set; } = new();

    public bool RecordHistory { get; set; } = true;

    /// <summary>是否把高置信度的结果自动写回本地题库。</summary>
    public bool AutoCache { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 60;

    public string Describe()
    {
        var desc = Title;
        if (!string.IsNullOrWhiteSpace(Options)) desc += "\n" + Options;
        return desc;
    }
}

/// <summary>单条搜题结果。</summary>
public sealed class SearchHit
{
    /// <summary>题库返回的题干（可能为空，空表示沿用提问的题干）。</summary>
    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    /// <summary>与提问题干的相似度 0~1。</summary>
    public double Score { get; set; }

    /// <summary>来源内的原始附加信息（AI 的解释、题库的额外数据等）。</summary>
    public string? Note { get; set; }

    public string DisplayQuestion(string fallback) => string.IsNullOrWhiteSpace(Question) ? fallback : Question;

    public string ScoreText => Score <= 0 ? "-" : Score.ToString("P0");
}

/// <summary>某个题库在本次搜题中的表现。</summary>
public sealed class SourceOutcome
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }
    public List<SearchHit> Hits { get; set; } = new();

    /// <summary>题库返回的提示信息，例如 OCS 约定里的 [msg, undefined]。</summary>
    public List<string> Notices { get; set; } = new();

    /// <summary>原始的接口响应，便于排错。</summary>
    public string? RawResponse { get; set; }

    public string Summary => Ok
        ? (Hits.Count > 0 ? $"命中 {Hits.Count} 条 · {ElapsedMs}ms" : $"未命中 · {ElapsedMs}ms")
        : $"失败：{Error}";
}

/// <summary>一次搜题的完整结果。</summary>
public sealed class SearchResponse
{
    public string Title { get; set; } = string.Empty;
    public QuestionType Type { get; set; }
    public string Options { get; set; } = string.Empty;

    public List<SourceOutcome> Outcomes { get; set; } = new();

    /// <summary>聚合排序后的候选答案。</summary>
    public List<SearchHit> Ranked { get; set; } = new();

    public long ElapsedMs { get; set; }

    /// <summary>命中来源为缓存的直接返回。</summary>
    public bool FromCache { get; set; }

    public SearchHit? Best => Ranked.Count > 0 ? Ranked[0] : null;

    public string? BestAnswer => Best?.Answer;
}

/// <summary>供界面展示的来源描述（OCS / AI / 局域网 / 本地，统一视图）。</summary>
public sealed class SourceDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public string Detail { get; set; } = string.Empty;

    public string KindText => Kind.ToChinese();
    public string EnabledText => Enabled ? "已启用" : "已停用";
}