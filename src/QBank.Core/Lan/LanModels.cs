using System.Text.Json;
using System.Text.Json.Serialization;
using QBank.Core.Models;

namespace QBank.Core.Lan;

/// <summary>局域网搜题请求。</summary>
public sealed class LanQueryRequest
{
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Options { get; set; }
    public int Limit { get; set; } = 5;
    public string? Token { get; set; }
    public string? ClientName { get; set; }
}

/// <summary>局域网搜题响应。</summary>
public sealed class LanQueryResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public List<LanHit> Hits { get; set; } = new();
}

/// <summary>局域网共享的一条题目。</summary>
public sealed class LanHit
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Options { get; set; }
    public string? Source { get; set; }
    public double Score { get; set; }
}

/// <summary>服务端自述信息（供发现/界面展示）。</summary>
public sealed class LanInfo
{
    public string App { get; set; } = "QBank";
    public string Name { get; set; } = "QBank 题库";
    public string Version { get; set; } = "1.0";
    public string? Id { get; set; }
    public int Port { get; set; }
    public int QuestionCount { get; set; }
    public bool AllowWrite { get; set; }
    public bool NeedToken { get; set; }
    public string? Host { get; set; }
}

public sealed class LanDiscoveredPeer
{
    public string Host { get; set; } = string.Empty;
    public LanInfo Info { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Info.Name) ? Host : $"{Info.Name} ({Host})";
}

/// <summary>从某个节点拉题库的结果。</summary>
public sealed class LanPullResult
{
    public string PeerName { get; set; } = string.Empty;
    public bool Incremental { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }

    public int Imported => Added + Updated;

    public string Describe() => Ok
        ? $"{PeerName}：新增 {Added}、更新 {Updated}" + (Incremental ? "（增量）" : "（全量）")
        : $"{PeerName}：{(string.IsNullOrWhiteSpace(Error) ? "失败" : Error)}";
}

/// <summary>题目与局域网传输结构互转。</summary>
public static class LanQuestionMapper
{
    public static LanHit ToHit(Question q, double score) => new()
    {
        Id = q.Id,
        Title = q.Title,
        Answer = q.Answer,
        Type = q.Type.ToOcs(),
        Options = string.IsNullOrEmpty(q.Options) ? null : q.Options,
        Source = q.Source,
        Score = score,
    };

    public static Question ToQuestion(LanHit hit) => new()
    {
        Title = hit.Title,
        Answer = hit.Answer,
        Type = QuestionTypeExtensions.ParseOcs(hit.Type),
        Options = hit.Options ?? string.Empty,
        Source = string.IsNullOrWhiteSpace(hit.Source) ? "局域网" : hit.Source!,
    };
}

/// <summary>局域网协议里的 JSON 选项。</summary>
public static class LanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}