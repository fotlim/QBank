using System.Text.Json;

namespace QBank.Core.Ocs;

/// <summary>OCS 的 Result：题库返回的单条答案。</summary>
public sealed class OcsResult
{
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public JsonElement? ExtraData { get; set; }
}

/// <summary>OCS 的 SearchInformation：某个题库在本次搜索里的完整表现。</summary>
public sealed class OcsSearchInformation
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Homepage { get; set; }
    public List<OcsResult> Results { get; set; } = new();

    /// <summary>接口原始响应（已按 contentType 解析）。</summary>
    public JsonElement? Response { get; set; }

    /// <summary>实际发出的请求数据。</summary>
    public JsonElement? Data { get; set; }

    /// <summary>失败原因；成功时为 null。</summary>
    public string? Error { get; set; }

    /// <summary>本次请求耗时。</summary>
    public long ElapsedMs { get; set; }

    /// <summary>解析过程中的提示信息（例如未识别的占位符）。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>原始响应文本（截断后），用于排错。</summary>
    public string? RawResponseForDiagnostics { get; set; }

    public bool Ok => Error is null;
}

/// <summary>执行题库配置时的上下文（对应 OCS 的 env: {title, options, type}）。</summary>
public sealed class AnswerWrapperEnv
{
    public string Title { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>额外的自定义占位符。</summary>
    public Dictionary<string, string> Extra { get; set; } = new();

    public string? Get(string key)
    {
        var k = key.Trim();
        return k switch
        {
            "title" => Title,
            "options" => Options,
            "type" => Type,
            _ => Extra.TryGetValue(k, out var v) ? v : null,
        };
    }

    public Dictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>(Extra)
        {
            ["title"] = Title,
            ["options"] = Options,
            ["type"] = Type,
        };
        return d;
    }
}

/// <summary>题库配置解析器。校验信息与 OCS 的 AnswerWrapperParser 保持一致。</summary>
public static class AnswerWrapperParser
{
    private static readonly string[] ContentTypes = { "json", "text" };
    private static readonly string[] Methods = { "post", "get" };
    private static readonly string[] Types = { "fetch", "GM_xmlhttpRequest" };

    /// <summary>从已经反序列化的 JSON 解析（JsonElement 必须是数组）。</summary>
    public static List<AnswererWrapper> FromObject(JsonElement json, bool assignMissingIds = true)
    {
        if (json.ValueKind != JsonValueKind.Array)
            throw new OcsConfigException("题库配置格式错误！");
        if (json.GetArrayLength() == 0)
            throw new OcsConfigException("题库为空！");

        var list = new List<AnswererWrapper>();
        var index = 0;
        foreach (var item in json.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                throw new OcsConfigException($"第 {index} 个题库的配置应为对象");

            if (!HasText(item, "name"))
                throw new OcsConfigException($"第 {index} 个题库的 名字(name) 为空");
            if (!HasText(item, "url"))
                throw new OcsConfigException($"第 {index} 个题库的 接口地址(url) 为空");
            if (!HasText(item, "handler"))
                throw new OcsConfigException($"第 {index} 个题库的 解析器(handler) 为空");
            if (item.TryGetProperty("headers", out var h) && h.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new OcsConfigException($"第 {index} 个题库的 头部信息(header) 应为 对象 格式");
            if (item.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new OcsConfigException($"第 {index} 个题库的 提交数据(data) 应为 对象 格式");
            if (item.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.String &&
                ContentTypes.All(x => x != ct.GetString()))
                throw new OcsConfigException($"第 {index} 个题库的 contentType 必须为以下选项中的一个  {string.Join(", ", ContentTypes)}");
            if (item.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String &&
                Methods.All(x => x != m.GetString()))
                throw new OcsConfigException($"第 {index} 个题库的 method 必须为以下选项中的一个  {string.Join(", ", Methods)}");
            if (item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
                Types.All(x => x != t.GetString()))
                throw new OcsConfigException($"第 {index} 个题库的 type 必须为以下选项中的一个  {string.Join(", ", Types)}");

            var wrapper = item.Deserialize<AnswererWrapper>(JsonOpts) ?? throw new OcsConfigException($"第 {index} 个题库解析失败");
            if (!assignMissingIds && item.TryGetProperty("_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                wrapper.Id = idEl.GetString()!;
            list.Add(wrapper);
        }
        return list;
    }

    private static bool TryGetString(JsonElement obj, string prop, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>必填字符串字段：必须存在、是字符串且去空白后非空（OCS 用 falsy 判定，空串同样算缺失）。</summary>
    private static bool HasText(JsonElement obj, string prop) =>
        TryGetString(obj, prop, out var value) && !string.IsNullOrWhiteSpace(value);

    public static List<AnswererWrapper> FromJsonString(string json)
    {
        var raw = (json ?? string.Empty).Trim();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return FromObject(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new OcsConfigException("格式错误，必须为：json字符串 或 题库配置链接");
        }
    }

    public static List<AnswererWrapper> FromBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return FromJsonString(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (FormatException)
        {
            throw new OcsConfigException("不是合法的 base64 内容");
        }
    }

    public static async Task<List<AnswererWrapper>> FromUrlAsync(string url, HttpClient http, CancellationToken ct = default)
    {
        var text = await http.GetStringAsync(url, ct);
        return FromJsonString(text);
    }

    /// <summary>与 OCS 的 AnswerWrapperParser.from 一致：url / base64 / json / json-string 全支持。</summary>
    public static async Task<List<AnswererWrapper>> FromAsync(string value, HttpClient http, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new OcsConfigException("题库配置不能为空，请配置后重新开始自动答题。");
        var v = value.Trim();
        try
        {
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return await FromUrlAsync(v, http, ct);
            if (v.StartsWith('[') || v.StartsWith('{'))
                return FromJsonString(v);
            // 兜底：当作 base64
            return FromBase64(v);
        }
        catch (OcsConfigException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OcsConfigException("题库配置拉取失败：" + ex.Message);
        }
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}