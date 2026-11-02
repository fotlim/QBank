using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QBank.Core.Models;

namespace QBank.Core.Ocs;

/// <summary>
/// OCS 题库配置执行器：把一组 <see cref="AnswererWrapper"/> 跑成 <see cref="OcsSearchInformation"/> 列表。
///
/// 与 OCS 4.x <c>defaultAnswerWrapperHandler</c> 的行为对齐：
/// <list type="bullet">
/// <item>并发请求所有题库，单个题库失败不影响其它题库；</item>
/// <item>GET：占位符先解析并编码，data 追加为查询参数；POST：data 作为请求体，值可以是 <c>{handler}</c> 解析器；</item>
/// <item>响应按 contentType 解析成 json / text，再交给 handler 处理；</item>
/// <item>handler 返回 <c>[题目,答案]</c> 或 <c>[[题目,答案],…]</c>；返回 undefined / 非数组 → 无结果；</item>
/// <item>答案里的 <c>[msg, undefined]</c> 视为“提示信息”。</item>
/// </list>
/// 已知的两处有意偏差（更实用、更安全）：URL 里的占位符只编码替换值本身（OCS 会把整个 URL 再编码一次）；
/// 未识别的占位符替换为空串（OCS 会替换成字面量 "undefined"）。
/// </summary>
public sealed class AnswerWrapperHandler
{
    public const string DefaultHandler = "return (res)=> [JSON.stringify(res), undefined]";

    private readonly HttpClient? _httpOverride;

    private HttpClient _http => _httpOverride ?? HttpClients.Shared;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> _jsStorage = new();

    public AnswerWrapperHandler(HttpClient? http = null) => _httpOverride = http;

    /// <summary>单个题库的请求超时（OCS 默认 60 秒）。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>并发上限。</summary>
    public int MaxParallel { get; set; } = 8;

    /// <summary>保留每个题库的 localStorage 内容（OCS 文档里“魔改 ChatGPT 上下文”的做法依赖它）。</summary>
    public bool PersistJsStorage { get; set; } = true;

    /// <summary>记录原始响应（用于排错），过长会截断。</summary>
    public int RawResponseLimit { get; set; } = 4000;

    public async Task<List<OcsSearchInformation>> HandleAsync(
        IReadOnlyList<AnswererWrapper> wrappers,
        AnswerWrapperEnv env,
        CancellationToken ct = default)
    {
        if (wrappers.Count == 0) return new List<OcsSearchInformation>();

        using var gate = new SemaphoreSlim(Math.Max(1, MaxParallel));
        var tasks = wrappers.Select(async w =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await HandleOneSafeAsync(w, env, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return all.ToList();
    }

    private async Task<OcsSearchInformation> HandleOneSafeAsync(AnswererWrapper wrapper, AnswerWrapperEnv env, CancellationToken ct)
    {
        try
        {
            return await HandleOneAsync(wrapper, env, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(wrapper, "已取消");
        }
        catch (Exception ex)
        {
            return Fail(wrapper, ex.Message);
        }
    }

    private static OcsSearchInformation Fail(AnswererWrapper wrapper, string error) => new()
    {
        Url = wrapper.Url,
        Name = wrapper.DisplayName,
        Homepage = wrapper.Homepage ?? "#",
        Error = error,
    };

    private async Task<OcsSearchInformation> HandleOneAsync(AnswererWrapper wrapper, AnswerWrapperEnv env, CancellationToken ct)
    {
        var info = new OcsSearchInformation
        {
            Url = wrapper.Url,
            Name = wrapper.DisplayName,
            Homepage = wrapper.Homepage ?? "#",
        };

        var method = (wrapper.Method ?? "get").Trim().ToLowerInvariant();
        var contentType = (wrapper.ContentType ?? "json").Trim().ToLowerInvariant();
        var handlerCode = string.IsNullOrWhiteSpace(wrapper.Handler) ? DefaultHandler : wrapper.Handler;
        var warnings = new List<string>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600)));

        string requestUrl;
        HttpContent? content = null;
        JsonElement? requestData = null;

        if (method == "get")
        {
            var (url, data) = BuildGetRequest(wrapper, env, warnings);
            requestUrl = url;
            requestData = data;
        }
        else if (method == "post")
        {
            var (url, payload, parsed) = BuildPostRequest(wrapper, env, warnings);
            requestUrl = url;
            requestData = parsed;
            var ctHeader = FindHeader(wrapper.Headers, "Content-Type") ?? string.Empty;
            if (ctHeader.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                var form = payload
                    .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(ToText(kv.Value)))
                    .ToList();
                content = new StringContent(string.Join("&", form), Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            else
            {
                var json = JsonSerializer.Serialize(payload, SerializeOptions);
                content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(ctHeader) ? "application/json" : ctHeader.Split(';')[0].Trim());
            }
        }
        else
        {
            info.Error = "不支持的请求方式";
            return info;
        }

        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            info.Error = "题库地址(url) 为空";
            return info;
        }

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            info.Error = "题库地址必须是完整的 http/https 地址";
            return info;
        }

        var sw = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);
        if (content is not null) request.Content = content;

        foreach (var header in wrapper.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        string raw;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            info.Data = requestData;

            if (!response.IsSuccessStatusCode)
            {
                info.Error = $"接口返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                info.Response = TryParseJson(raw);
                return info;
            }

            if (!string.IsNullOrEmpty(raw)) info.RawResponseForDiagnostics = Truncate(raw, RawResponseLimit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            info.Error = "题库请求超时，可能是题库问题，或者请检查网络或者重试。";
            return info;
        }
        catch (HttpRequestException ex)
        {
            info.Error = "网络请求失败：" + ex.Message;
            return info;
        }

        var effectiveHandler = string.IsNullOrWhiteSpace(handlerCode) ? DefaultHandler : handlerCode;
        JsonElement? responseValue = null;
        JsRunOutcome jsOutcome;

        var storage = PersistJsStorage
            ? _jsStorage.GetOrAdd(wrapper.Id ?? wrapper.Url ?? "default", _ => new Dictionary<string, string>())
            : new Dictionary<string, string>();

        if (contentType == "text")
        {
            jsOutcome = JsScriptRunner.RunWithTextArg(effectiveHandler, raw, storage, TimeSpan.FromSeconds(8));
        }
        else
        {
            try
            {
                responseValue = TryParseJson(raw);
                if (responseValue is null)
                {
                    info.Error = "接口返回的不是合法 JSON，请把 contentType 设置为 text 或修正 handler";
                    info.RawResponseForDiagnostics = Truncate(raw, RawResponseLimit);
                    return info;
                }
            }
            catch (JsonException ex)
            {
                info.Error = "解析 JSON 失败：" + ex.Message;
                info.RawResponseForDiagnostics = Truncate(raw, RawResponseLimit);
                return info;
            }
            info.Response = responseValue;
            jsOutcome = JsScriptRunner.RunWithJsonArg(effectiveHandler, raw, storage, TimeSpan.FromSeconds(8));
        }

        if (!jsOutcome.Ok)
        {
            info.Error = "handler 执行失败：" + jsOutcome.Error;
            info.RawResponseForDiagnostics = Truncate(raw, RawResponseLimit);
            return info;
        }

        if (PersistJsStorage && jsOutcome.Storage.Count > 0)
            _jsStorage[wrapper.Id ?? wrapper.Url ?? "default"] = jsOutcome.Storage;

        NormalizeResults(jsOutcome, info);
        info.ElapsedMs = sw.ElapsedMilliseconds;
        info.Warnings = warnings;
        return info;
    }

    // ---------------- 请求构造 ----------------

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static (string Url, JsonElement? Data) BuildGetRequest(AnswererWrapper wrapper, AnswerWrapperEnv env, List<string> warnings)
    {
        var resolved = ResolvePlaceHolder(wrapper.Url ?? string.Empty, env, encode: true, warnings);

        var qIndex = resolved.IndexOf('?');
        var basePart = qIndex >= 0 ? resolved[..qIndex] : resolved;
        var queryPart = qIndex >= 0 ? resolved[(qIndex + 1)..] : string.Empty;

        // 保留原有查询串（原样，不重新编码）
        var existing = queryPart
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                var rawKey = eq >= 0 ? pair[..eq] : pair;
                var rawValue = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
                string decodedKey;
                try { decodedKey = Uri.UnescapeDataString(rawKey.Replace("+", " ")); }
                catch { decodedKey = rawKey; }
                return (RawKey: rawKey, RawValue: rawValue, DecodedKey: decodedKey);
            })
            .ToList();

        var dataDict = new Dictionary<string, string>();
        if (wrapper.Data is not null)
        {
            foreach (var kv in wrapper.Data)
            {
                var value = ResolveDataValueToText(kv.Value, env, warnings);
                dataDict[kv.Key] = value;

                // data 参数优先级高于 url 参数：移除同名项后再追加（对应 URLSearchParams.set）
                if (existing.Count > 0)
                    existing.RemoveAll(e => string.Equals(e.DecodedKey, kv.Key, StringComparison.Ordinal));
            }
        }

        var sb = new StringBuilder(basePart);
        var pairs = existing.Select(e => e.RawKey + (e.RawValue.Length > 0 ? "=" + e.RawValue : string.Empty)).ToList();
        pairs.AddRange(dataDict.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        if (pairs.Count > 0) sb.Append('?').Append(string.Join("&", pairs));

        var dataElement = dataDict.Count > 0
            ? JsonSerializer.SerializeToElement(dataDict, SerializeOptions)
            : (JsonElement?)null;

        return (sb.ToString(), dataElement);
    }

    private static (string Url, Dictionary<string, object?> Payload, JsonElement? Data) BuildPostRequest(
        AnswererWrapper wrapper, AnswerWrapperEnv env, List<string> warnings)
    {
        // 与 OCS 一致：POST 时不解析 url 里的占位符
        var url = wrapper.Url ?? string.Empty;
        var payload = new Dictionary<string, object?>();

        if (wrapper.Data is not null)
        {
            foreach (var kv in wrapper.Data)
            {
                if (kv.Value.ValueKind == JsonValueKind.Object &&
                    kv.Value.TryGetProperty("handler", out var handlerEl) &&
                    handlerEl.ValueKind == JsonValueKind.String)
                {
                    var code = handlerEl.GetString() ?? string.Empty;
                    var outcome = JsScriptRunner.RunWithEnvArg(code, env.ToDictionary(), null, TimeSpan.FromSeconds(8));
                    if (!outcome.Ok)
                        throw new OcsConfigException($"data 字段 {kv.Key} 的解析器执行失败：{outcome.Error}");
                    if (outcome.IsUndefined || string.IsNullOrEmpty(outcome.Json) || outcome.Json == "null")
                        continue; // 对应 JS 里值为 undefined → JSON.stringify 会丢掉这个键

                    payload[kv.Key] = JsonSerializer.Deserialize<JsonElement>(outcome.Json);
                    continue;
                }

                payload[kv.Key] = ResolveJsonValue(kv.Value, env, warnings);
            }
        }

        var dataElement = payload.Count > 0
            ? JsonSerializer.SerializeToElement(payload, SerializeOptions)
            : (JsonElement?)null;

        return (url, payload, dataElement);
    }

    /// <summary>解析 data 里的值：字符串走占位符，其它类型原样保留。</summary>
    private static object? ResolveJsonValue(JsonElement value, AnswerWrapperEnv env, List<string> warnings) => value.ValueKind switch
    {
        JsonValueKind.String => ResolvePlaceHolder(value.GetString() ?? string.Empty, env, encode: false, warnings),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => JsonSerializer.Deserialize<JsonElement>(value.GetRawText()),
    };

    /// <summary>把 data 的值转成文本（GET 查询串、form 表单用）。</summary>
    private static string ResolveDataValueToText(JsonElement value, AnswerWrapperEnv env, List<string> warnings) => value.ValueKind switch
    {
        JsonValueKind.String => ResolvePlaceHolder(value.GetString() ?? string.Empty, env, encode: false, warnings),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        JsonElement el => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText(),
        _ => JsonSerializer.Serialize(value, SerializeOptions),
    };

    private static string? FindHeader(Dictionary<string, string> headers, string name)
        => headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static readonly Regex PlaceholderRegex = new(@"\${(.*?)}", RegexOptions.Compiled);

    /// <summary>解析 ${title} / ${type} / ${options} 之类的占位符。</summary>
    internal static string ResolvePlaceHolder(string text, AnswerWrapperEnv env, bool encode, List<string>? warnings = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${", StringComparison.Ordinal)) return text;

        return PlaceholderRegex.Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            var value = env.Get(key);
            if (value is null)
            {
                warnings?.Add($"占位符 ${{{key}}} 没有对应值，已替换为空串");
                value = string.Empty;
            }
            return encode ? Uri.EscapeDataString(value) : value;
        });
    }

    // ---------------- 结果归一化 ----------------

    private static void NormalizeResults(JsRunOutcome outcome, OcsSearchInformation info)
    {
        if (string.IsNullOrWhiteSpace(outcome.Json) || outcome.Json == "null") return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(outcome.Json);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;
            if (root.GetArrayLength() == 0) return;

            var twoDimensional = true;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) { twoDimensional = false; break; }
            }

            if (twoDimensional)
            {
                foreach (var item in root.EnumerateArray()) PushResult(item, info);
            }
            else
            {
                PushResult(root, info);
            }
        }
    }

    private static void PushResult(JsonElement array, OcsSearchInformation info)
    {
        if (array.ValueKind != JsonValueKind.Array) return;
        var length = array.GetArrayLength();
        if (length == 0) return;

        var question = length > 0 ? JsonToText(array[0]) : null;
        var answer = length > 1 ? JsonToText(array[1]) : null;
        JsonElement? extra = length > 2 ? array[2].Clone() : null;

        info.Results.Add(new OcsResult
        {
            Question = question,
            Answer = answer,
            ExtraData = extra,
        });
    }

    private static string? JsonToText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
        _ => element.GetRawText(),
    };

    private static JsonElement? TryParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…（已截断）";
}