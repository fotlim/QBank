using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QBank.Core.Ocs;

/// <summary>
/// OCS 题库配置（AnswererWrapper）的 .NET 映射。
/// 字段名、默认值与 OCS 4.x 的 <c>AnswererWrapper</c> 接口一一对应，
/// 额外带 QBank 私有扩展字段（以 <c>_</c> 开头），导出给 OCS 时会剔除。
/// </summary>
public sealed class AnswererWrapper
{
    /// <summary>请求路径，支持 ${title} / ${type} / ${options} 占位符。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    /// <summary>请求数据；值可以是字符串（含占位符）或 { "handler": "return (env)=> ..." }。</summary>
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement>? Data { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "get";

    /// <summary>定义 handler 里收到的响应该按什么解析：json / text。</summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "json";

    /// <summary>fetch / GM_xmlhttpRequest。本程序始终用本机 HTTP 客户端，此字段仅作兼容保留。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "fetch";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("handler")]
    public string Handler { get; set; } = string.Empty;

    // ---------- QBank 扩展（OCS 会忽略未知字段，导出时也会主动剔除） ----------

    [JsonPropertyName("_id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("_enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("_priority")]
    public int Priority { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未知题库" : Name;

    [JsonIgnore]
    public string DisplayUrl => $"{Method.ToUpperInvariant()} {Url}";

    internal static readonly JsonSerializerOptions OcsExportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>导出为 OCS 原样的单个配置对象（不含 QBank 私有字段）。</summary>
    public JsonObjectLike ToOcsObject() => new()
    {
        ["url"] = Url,
        ["name"] = Name,
        ["homepage"] = Homepage ?? "#",
        ["data"] = Data is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(Data),
        ["method"] = Method,
        ["contentType"] = ContentType,
        ["type"] = Type,
        ["headers"] = Headers,
        ["handler"] = Handler,
    };

    /// <summary>导出成可直接粘贴进 OCS 的 JSON。</summary>
    public static string ToOcsJson(IReadOnlyList<AnswererWrapper> wrappers)
    {
        var list = wrappers.Select(w => w.ToOcsObject()).ToList();
        return JsonSerializer.Serialize(list, OcsExportOptions);
    }

    public AnswererWrapper Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AnswererWrapper>(json)!;
    }

    /// <summary>用 <see cref="Dictionary{TKey,TValue}"/> 模拟匿名 JSON 对象，便于序列化导出。</summary>
    public sealed class JsonObjectLike : Dictionary<string, object?> { }
}

/// <summary>题库配置格式错误。</summary>
public sealed class OcsConfigException : Exception
{
    public OcsConfigException(string message) : base(message) { }
}