using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QBank.Core.Models;

namespace QBank.Core.Ai;

/// <summary>AI 对话的一则消息。</summary>
public sealed class AiChatMessage
{
    public string Role { get; set; } = "user";

    /// <summary>纯文本内容。</summary>
    public string? Text { get; set; }

    /// <summary>图片（base64 + mime），用于视觉模型。</summary>
    public List<AiImagePart> Images { get; set; } = new();

    public static AiChatMessage System(string text) => new() { Role = "system", Text = text };
    public static AiChatMessage User(string text) => new() { Role = "user", Text = text };
    public static AiChatMessage UserWithImage(string text, byte[] image, string mime) => new()
    {
        Role = "user",
        Text = text,
        Images = { new AiImagePart { Data = image, Mime = mime } },
    };
}

public sealed class AiImagePart
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Mime { get; set; } = "image/png";

    public string ToDataUrl() => $"data:{Mime};base64,{Convert.ToBase64String(Data)}";
}

public sealed class AiChatResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Content { get; set; }
    public string? Reasoning { get; set; }
    public string? Model { get; set; }
    public int HttpStatus { get; set; }
    public long ElapsedMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string? Raw { get; set; }
}

/// <summary>
/// OpenAI 兼容的 <c>/chat/completions</c> 客户端。
/// DeepSeek、硅基流动、阿里云百炼（兼容模式）、Ollama、以及任何 OpenAI 兼容网关都走这一条路。
/// </summary>
public sealed class AiChatClient
{
    private readonly HttpClient? _httpOverride;

    public AiChatClient(HttpClient? http = null) => _httpOverride = http;

    /// <summary>
    /// 每次都向 HttpClients 要最新的客户端 —— 设置里改了代理模式会重建共享客户端，
    /// 如果这里缓存了旧实例，改完设置还得重启才能生效。
    /// </summary>
    private HttpClient _http => _httpOverride ?? HttpClients.Shared;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>把用户填的 BaseUrl 补全成真正的 chat/completions 地址。</summary>
    public static string ResolveEndpoint(AiProviderConfig provider)
    {
        var b = (provider.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.Length == 0) return string.Empty;
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;

        if (Uri.TryCreate(b, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (path.Length == 0)
            {
                // 只填了域名：按服务商补默认路径
                var suffix = provider.ProviderKind switch
                {
                    "bailian" => "/compatible-mode/v1",
                    "ollama" => "/v1",
                    _ => "/v1",
                };
                b += suffix;
            }
        }
        return b + "/chat/completions";
    }

    public async Task<AiChatResult> CompleteAsync(
        AiProviderConfig provider,
        IReadOnlyList<AiChatMessage> messages,
        bool jsonMode,
        CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint(provider);
        if (string.IsNullOrWhiteSpace(endpoint))
            return new AiChatResult { Ok = false, Error = "没有配置接口地址(BaseUrl)" };

        var first = await SendAsync(provider, endpoint, messages, jsonMode, ct).ConfigureAwait(false);

        // 有些服务不支持 response_format，报错后自动去掉重试一次
        if (!first.Ok && jsonMode && LooksLikeJsonModeRejection(first))
        {
            var retry = await SendAsync(provider, endpoint, messages, false, ct).ConfigureAwait(false);
            retry.Error = retry.Ok ? null : retry.Error;
            return retry;
        }

        return first;
    }

    private static bool LooksLikeJsonModeRejection(AiChatResult result)
    {
        if (result.HttpStatus != 400) return false;
        var text = (result.Raw ?? string.Empty) + " " + (result.Error ?? string.Empty);
        return text.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || text.Contains("json_object", StringComparison.OrdinalIgnoreCase)
            || text.Contains("json mode", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AiChatResult> SendAsync(
        AiProviderConfig provider,
        string endpoint,
        IReadOnlyList<AiChatMessage> messages,
        bool jsonMode,
        CancellationToken ct)
    {
        var result = new AiChatResult { Model = provider.Model };
        var sw = Stopwatch.StartNew();

        var body = new JsonObject
        {
            ["model"] = provider.Model,
            ["messages"] = BuildMessages(messages),
            ["stream"] = false,
        };

        if (provider.Temperature >= 0) body["temperature"] = provider.Temperature;
        if (provider.MaxTokens > 0) body["max_tokens"] = provider.MaxTokens;
        if (jsonMode && provider.UseJsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        MergeExtraParams(body, provider.ExtraParamsJson);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 5, 600)));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(JsonOpts), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + provider.ApiKey.Trim());

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            result.HttpStatus = (int)response.StatusCode;
            result.Raw = raw.Length > 4000 ? raw[..4000] + "…" : raw;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                result.Ok = false;
                result.Error = ExplainHttpError((int)response.StatusCode, raw);
                return result;
            }

            ParseResponse(raw, result);
            result.Ok = result.Content is not null;
            if (!result.Ok && result.Error is null)
                result.Error = "接口没有返回任何内容";
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Ok = false;
            result.Error = $"请求超时（超过 {provider.TimeoutSeconds} 秒）";
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.Ok = false;
            result.Error = HttpClients.ExplainNetworkError(ex);
            return result;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Error = HttpClients.ExplainNetworkError(ex);
            return result;
        }
    }

    private static JsonArray BuildMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var node = new JsonObject { ["role"] = m.Role };
            if (m.Images.Count == 0)
            {
                node["content"] = m.Text ?? string.Empty;
            }
            else
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Text))
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var img in m.Images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = img.ToDataUrl() },
                    });
                }
                node["content"] = parts;
            }
            array.Add(node);
        }
        return array;
    }

    private static void MergeExtraParams(JsonObject body, string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return;
        try
        {
            var node = JsonNode.Parse(extraJson);
            if (node is JsonObject obj)
            {
                foreach (var kv in obj)
                    body[kv.Key] = kv.Value?.DeepClone();
            }
        }
        catch (JsonException)
        {
            // 高级参数填错就忽略，不影响主流程
        }
    }

    private static void ParseResponse(string raw, AiChatResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : err.ToString();
                result.Error = "接口返回错误：" + msg;
                return;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                result.Error = "接口返回里没有 choices 字段";
                return;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    result.Content = contentEl.GetString();
                if (message.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
                    result.Reasoning = reasoningEl.GetString();
            }
            else if (choice.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                // 兼容老的 completions 风格
                result.Content = textEl.GetString();
            }

            if (result.Content is null && !string.IsNullOrWhiteSpace(result.Reasoning))
                result.Content = result.Reasoning;

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var p)) result.PromptTokens = p;
                if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var c)) result.CompletionTokens = c;
            }
            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                result.Model = modelEl.GetString();
        }
        catch (JsonException ex)
        {
            result.Error = "接口返回的不是合法 JSON：" + ex.Message;
        }
    }

    private static string ExplainHttpError(int status, string raw)
    {
        var detail = ExtractErrorMessage(raw);
        return status switch
        {
            400 => "请求被拒绝(400)：" + detail,
            401 => "鉴权失败(401)：API Key 不正确或已失效",
            403 => "无权限(403)：账号可能未开通该模型，" + detail,
            404 => "接口地址不存在(404)：检查 BaseUrl 是否填对（一般以 /v1 结尾）",
            408 => "请求超时(408)：服务端响应过慢",
            413 => "请求体过大(413)：题目或图片太长",
            429 => "限流或额度不足(429)：" + detail,
            500 or 502 or 503 or 504 => $"服务端错误({status})：稍后重试，" + detail,
            _ => $"请求失败({status})：" + detail,
        };
    }

    private static string ExtractErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                    return m.GetString() ?? raw;
                return err.ToString();
            }
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString() ?? raw;
        }
        catch (JsonException)
        {
            // 忽略
        }
        return raw.Length > 200 ? raw[..200] : raw;
    }
}