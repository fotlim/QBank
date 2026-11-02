using System.Diagnostics;
using System.Text.Json;
using QBank.Core.Models;

namespace QBank.Core.Ai;

/// <summary>从服务端拉到的单个模型。</summary>
public sealed class AiModelInfo
{
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名（Ollama 会带 tag，OpenAI 兼容接口一般等于 Id）。</summary>
    public string Name { get; set; } = string.Empty;

    public string? OwnedBy { get; set; }

    /// <summary>模型体积（Ollama 才有，字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>参数量等结构信息（Ollama 的 details.parameter_size）。</summary>
    public string? ParameterSize { get; set; }

    /// <summary>量化方式（Ollama 的 details.quantization_level）。</summary>
    public string? Quantization { get; set; }

    /// <summary>是否可能支持图片输入（按名字推断）。</summary>
    public bool VisionCapable { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string SizeText => SizeBytes <= 0
        ? string.Empty
        : SizeBytes >= 1024L * 1024 * 1024
            ? (SizeBytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB"
            : (SizeBytes / (1024.0 * 1024)).ToString("0") + " MB";

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(OwnedBy)) parts.Add(OwnedBy!);
            if (!string.IsNullOrWhiteSpace(ParameterSize)) parts.Add(ParameterSize!);
            if (!string.IsNullOrWhiteSpace(Quantization)) parts.Add(Quantization!);
            if (SizeBytes > 0) parts.Add(SizeText);
            if (VisionCapable) parts.Add("支持图片");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>拉取模型列表的结果。</summary>
public sealed class AiModelListResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<AiModelInfo> Models { get; set; } = new();
    public string? Endpoint { get; set; }
    public long ElapsedMs { get; set; }
    public string? Raw { get; set; }
    public bool OllamaNative { get; set; }
}

/// <summary>
/// 模型目录：向服务商的 <c>GET /models</c> 拉取当前账号可用的模型列表。
/// DeepSeek、硅基流动、百炼（兼容模式）、OpenAI 都实现了这套接口；
/// Ollama 额外支持原生 <c>/api/tags</c>（信息更全：体积、参数量、量化）。
/// </summary>
public sealed class AiModelCatalog
{
    private readonly HttpClient _http;

    public AiModelCatalog(HttpClient? http = null) => _http = http ?? HttpClients.Shared;

    /// <summary>把 BaseUrl 补全成 models 列表地址。</summary>
    public static string ResolveModelsEndpoint(AiProviderConfig provider, bool ollamaNative = false)
    {
        var b = (provider.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.Length == 0) return string.Empty;

        if (ollamaNative)
        {
            // http://127.0.0.1:11434/v1 → http://127.0.0.1:11434/api/tags
            foreach (var suffix in new[] { "/v1", "/chat/completions" })
            {
                if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    b = b[..^suffix.Length];
                    break;
                }
            }
            return b + "/api/tags";
        }

        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            b = b[..^"/chat/completions".Length];
        if (b.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return b;

        if (Uri.TryCreate(b, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (path.Length == 0)
            {
                var suffix = provider.ProviderKind switch
                {
                    "bailian" => "/compatible-mode/v1",
                    _ => "/v1",
                };
                b += suffix;
            }
        }
        return b.TrimEnd('/') + "/models";
    }

    /// <summary>拉取可用模型列表。Ollama 会先试原生接口，失败再走 OpenAI 兼容接口。</summary>
    public async Task<AiModelListResult> FetchAsync(AiProviderConfig provider, CancellationToken ct = default)
    {
        var isOllama = string.Equals(provider.ProviderKind, "ollama", StringComparison.OrdinalIgnoreCase);

        if (isOllama)
        {
            var native = await FetchCoreAsync(provider, ResolveModelsEndpoint(provider, true), nativeOllama: true, ct)
                .ConfigureAwait(false);
            if (native.Ok && native.Models.Count > 0) return native;

            var compat = await FetchCoreAsync(provider, ResolveModelsEndpoint(provider), nativeOllama: false, ct)
                .ConfigureAwait(false);
            if (compat.Ok) return compat;
            return native.Ok ? native : compat;
        }

        return await FetchCoreAsync(provider, ResolveModelsEndpoint(provider), nativeOllama: false, ct)
            .ConfigureAwait(false);
    }

    private async Task<AiModelListResult> FetchCoreAsync(
        AiProviderConfig provider,
        string endpoint,
        bool nativeOllama,
        CancellationToken ct)
    {
        var result = new AiModelListResult { Endpoint = endpoint, OllamaNative = nativeOllama };
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            result.Error = "没有配置接口地址(BaseUrl)";
            return result;
        }

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds <= 0 ? 20 : provider.TimeoutSeconds, 5, 120)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(provider.ApiKey) && !nativeOllama)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + provider.ApiKey.Trim());

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.Raw = raw.Length > 4000 ? raw[..4000] + "…" : raw;

            if (!response.IsSuccessStatusCode)
            {
                result.Ok = false;
                result.Error = (int)response.StatusCode switch
                {
                    401 or 403 => "鉴权失败：API Key 不正确，或该 Key 没有列模型的权限",
                    404 => "接口地址不存在：检查 BaseUrl（一般以 /v1 结尾）",
                    429 => "请求过于频繁或额度不足",
                    _ => $"请求失败({(int)response.StatusCode})：{Summarize(raw)}",
                };
                return result;
            }

            result.Models = ParseModels(raw, nativeOllama, out var parseError);
            result.Ok = result.Models.Count > 0;
            if (!result.Ok) result.Error = parseError ?? "接口没有返回任何模型";
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Ok = false;
            result.Error = "请求超时：检查地址是否可达，或服务是否已启动";
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.Ok = false;
            result.Error = "网络请求失败：" + ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>解析模型列表：兼容 OpenAI 的 {"data":[…]}、Ollama 的 {"models":[…]}、以及裸数组。</summary>
    public static List<AiModelInfo> ParseModels(string raw, bool nativeOllama, out string? error)
    {
        error = null;
        var list = new List<AiModelInfo>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = "返回的不是合法 JSON：" + ex.Message;
            return list;
        }

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("data", out array) && !root.TryGetProperty("models", out array))
            {
                // 有的网关把错误塞在 error/message 里
                if (root.TryGetProperty("error", out var err))
                {
                    error = "接口返回错误：" + Summarize(err.ToString());
                    return list;
                }
                error = "返回里没有 data / models 字段";
                return list;
            }
            if (array.ValueKind != JsonValueKind.Array)
            {
                error = "data / models 不是数组";
                return list;
            }
        }
        else
        {
            error = "返回结构不是对象也不是数组";
            return list;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var id = item.GetString() ?? string.Empty;
                if (id.Length == 0) continue;
                list.Add(Build(id, id, null, 0, null, null, null));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            // OpenAI 风格
            var modelId = GetString(item, "id")
                          ?? GetString(item, "name")
                          ?? GetString(item, "model")
                          ?? string.Empty;
            if (modelId.Length == 0) continue;

            var ownedBy = GetString(item, "owned_by") ?? GetString(item, "organization") ?? GetString(item, "publisher");
            var name = GetString(item, "name") ?? modelId;

            long size = 0;
            if (item.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number)
                size = sizeEl.TryGetInt64(out var s) ? s : 0;

            string? parameterSize = null;
            string? quantization = null;
            if (item.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                parameterSize = GetString(details, "parameter_size");
                quantization = GetString(details, "quantization_level");
                ownedBy ??= GetString(details, "family");
            }

            DateTimeOffset? created = null;
            if (item.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number &&
                createdEl.TryGetInt64(out var epoch) && epoch > 0)
            {
                try { created = DateTimeOffset.FromUnixTimeSeconds(epoch); } catch (ArgumentOutOfRangeException) { }
            }
            else if (item.TryGetProperty("modified_at", out var modEl) && modEl.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(modEl.GetString(), out var mod))
            {
                created = mod;
            }

            list.Add(Build(modelId, name, ownedBy, size, parameterSize, quantization, created));
        }

        // Ollama 的 name 带 :tag，去重时用 Id
        return list
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AiModelInfo Build(string id, string name, string? owner, long size, string? ps, string? q, DateTimeOffset? created)
            => new()
            {
                Id = id,
                Name = name,
                OwnedBy = owner,
                SizeBytes = size,
                ParameterSize = ps,
                Quantization = q,
                CreatedAt = created,
                VisionCapable = GuessVision(id + " " + name),
            };
    }

    /// <summary>按模型名猜是否支持图片输入（只做提示，最终以实际调用为准）。</summary>
    public static bool GuessVision(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var n = modelName.ToLowerInvariant();

        string[] positive =
        {
            "-vl", "vl-", "vision", "llava", "minicpm-v", "moondream", "gpt-4o", "gpt-4.1", "gpt-5",
            "gemini", "claude-3", "claude-4", "qwen2-vl", "qwen2.5-vl", "qwen3-vl", "internvl", "cogvlm",
            "glm-4v", "step-1v", "yi-vl", "pixtral", "phi-3.5-vision", "phi-4-multimodal", "gemma3",
            "o3", "o4-mini", "multimodal", "image",
        };
        foreach (var key in positive)
            if (n.Contains(key, StringComparison.Ordinal)) return true;

        // deepseek-chat / deepseek-reasoner / *-instruct / *-coder 等等明确不带视觉
        return false;
    }

    /// <summary>给界面用的一行状态描述。</summary>
    public static string DescribeResult(AiModelListResult result)
    {
        if (result.Ok)
            return $"已获取 {result.Models.Count} 个模型 · {result.ElapsedMs}ms" +
                   (result.OllamaNative ? " · Ollama 原生接口" : string.Empty);
        return "获取失败：" + result.Error;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static string Summarize(string text)
    {
        var t = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length > 160 ? t[..160] + "…" : t;
    }
}
