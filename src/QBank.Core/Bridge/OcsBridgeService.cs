using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QBank.Core.Http;
using QBank.Core.Models;
using QBank.Core.Ocs;

namespace QBank.Core.Bridge;

/// <summary>
/// OCS 对接口：把 QBank 的题库能力（本地题库 + 局域网节点 + AI 模型 + 已配置的在线题库）
/// 聚合成一个 OCS 能直接调用的接口，并生成一段可粘贴进 OCS 的 AnswererWrapper 配置。
///
/// 接口（默认 127.0.0.1:18322）：
///   GET  /qbank/ocs/search?title=X&amp;options=Y&amp;type=single    → OCS 结果数组
///   POST /qbank/ocs/search  {title, options, type}                 → 同上
///   GET  /qbank/ocs/ping                                           → 状态与统计
///   GET  /qbank/ocs/config                                         → 现成的 OCS 题库配置（可直接粘贴）
///   GET  /qbank/ocs/docs                                           → 接口说明
/// </summary>
public sealed class OcsBridgeService : IDisposable
{
    private readonly Models.SearchRequest _requestTemplate = new();

    private MiniHttpServer? _server;
    private Func<SearchRequest, CancellationToken, Task<SearchResponse>>? _searcher;

    public OcsBridgeService() { }

    public OcsBridgeConfig Config { get; private set; } = new();

    public bool IsRunning => _server?.IsRunning == true;

    public int Port => _server?.Port ?? Config.Port;

    public int RequestCount => _server?.RequestCount ?? 0;

    public DateTimeOffset? LastRequestAt => _server?.LastRequestAt;

    public int HitCount { get; private set; }

    public int MissCount { get; private set; }

    public event EventHandler<string>? Log;

    public string StatusText => IsRunning
        ? $"运行中 · {Config.EffectiveHost}:{Port} · 已响应 {RequestCount} 次（命中 {HitCount} / 未命中 {MissCount}）"
        : "未启动";

    /// <summary>
    /// 配置并启动。searcher 由上层注入（一般是 SearchService.SearchAsync），
    /// 这样对接口复用的就是界面里那一套来源和排序逻辑。
    /// </summary>
    public void Apply(
        OcsBridgeConfig config,
        Func<SearchRequest, CancellationToken, Task<SearchResponse>> searcher)
    {
        Config = config;
        _searcher = searcher;

        var address = Config.AllowLan ? IPAddress.Any : IPAddress.Loopback;

        if (_server is not null)
        {
            if (_server.IsRunning) _server.Stop();
            _server.Log -= OnServerLog;
            _server = null;
        }

        _server = new MiniHttpServer(address, Config.Port, HandleAsync, "OCS 对接口");
        _server.Log += OnServerLog;

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, "OCS 对接口启动失败：" + ex.Message);
        }
    }

    private void OnServerLog(object? sender, string message) => Log?.Invoke(this, message);

    public void Stop()
    {
        _server?.Stop();
    }

    // ---------------- 请求处理 ----------------

    private async Task<MiniHttpResponse> HandleAsync(MiniHttpRequest request)
    {
        var path = request.Path.TrimEnd('/').ToLowerInvariant();

        if (path is "/qbank/ocs/ping" or "/qbank/ping")
            return MiniHttpResponse.Json(PingJson());

        if (path is "/qbank/ocs/config" or "/qbank/config")
            return MiniHttpResponse.Json(BuildOcsConfig());

        // 直接给 OCS 的「题库配置链接」用：返回纯配置数组，粘链接就能导入
        if (path is "/qbank/ocs/ocs.json" or "/qbank/ocs/answerer" or "/qbank/ocs.json")
            return new MiniHttpResponse
            {
                Body = BuildOcsConfig(),
                ContentType = "application/json; charset=utf-8",
            };

        if (path is "/qbank/ocs/docs" or "/qbank/docs")
            return MiniHttpResponse.Json(DocsJson());

        if (path is "/qbank/ocs/export")
            return MiniHttpResponse.Json(ConfigJson());

        if (path is "/qbank/ocs/search" or "/qbank/search" or "/")
            return await HandleSearchAsync(request).ConfigureAwait(false);

        return MiniHttpResponse.Json(
            JsonSerializer.Serialize(new
            {
                ok = false,
                msg = $"未知接口：{request.Path}",
                endpoints = new[] { "/qbank/ocs/search", "/qbank/ocs/ping", "/qbank/ocs/config", "/qbank/ocs/docs" },
            }, OcsJsonOptions),
            404, "Not Found");
    }

    private async Task<MiniHttpResponse> HandleSearchAsync(MiniHttpRequest request)
    {
        var query = ReadQuery(request, out var parseError, out var badToken);
        if (badToken)
            return OcsError("访问口令不正确");

        if (parseError is not null)
            return OcsError(parseError);

        if (string.IsNullOrWhiteSpace(query.Title))
            return MiniHttpResponse.Json("[]");

        if (_searcher is null)
            return OcsError("对接口还没接上搜题引擎");

        var search = new SearchRequest
        {
            Title = query.Title,
            Options = query.Options,
            Type = string.IsNullOrWhiteSpace(query.Type)
                ? QuestionTypeInference.Infer(query.Title, query.Options)
                : QuestionTypeExtensions.ParseOcs(query.Type),
            Limit = Math.Clamp(Config.MaxResults <= 0 ? 3 : Config.MaxResults, 1, 20),
            UseCache = true,
            UseLocalBank = Config.UseLocalBank,
            UseLanShare = Config.UseLanShare,
            UseOcs = Config.UseOcs,
            UseAi = Config.UseAi,
            RecordHistory = Config.RecordHistory,
            TimeoutSeconds = Math.Clamp(Config.TimeoutSeconds <= 0 ? 30 : Config.TimeoutSeconds, 5, 180),
        };

        // 题型没传也没推断出来时，OCS 那边多半也没给，别让排序把结果全筛掉
        if (search.Type == QuestionType.Unknown)
            search.Type = QuestionType.Unknown;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(search.TimeoutSeconds + 5));

        SearchResponse response;
        try
        {
            response = await _searcher(search, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcsError("搜题超时");
        }
        catch (Exception ex)
        {
            return OcsError("搜题失败：" + ex.Message);
        }

        var minScore = Math.Clamp(Config.MinScore, 0, 1);
        var hits = response.Ranked
            .Where(h => !string.IsNullOrWhiteSpace(h.Answer))
            .Where(h => h.Score >= minScore || search.Type == QuestionType.Unknown && h.Score >= minScore * 0.8)
            .Take(search.Limit)
            .ToList();

        // 一条都没有时给 OCS 一条提示（OCS 会把它当 [msg, undefined] 显示）
        if (hits.Count == 0)
        {
            var reason = BuildMissReason(response);
            MissCount++;
            Log?.Invoke(this, $"OCS 查询未命中：{Shorten(query.Title, 20)}（{reason}）");
            return MiniHttpResponse.Json(JsonSerializer.Serialize(new object[] { reason, null! }, OcsJsonOptions));
        }

        HitCount++;
        Log?.Invoke(this, $"OCS 查询命中：{Shorten(query.Title, 20)} → {hits.Count} 条（{(response.FromCache ? "缓存" : response.ElapsedMs + "ms")}）");

        // OCS 约定：[[题目, 答案], ...]，题目给 null 表示沿用 OCS 搜索到的题干
        var result = hits.Select(h => new object?[] { null, h.Answer }).ToList();
        return MiniHttpResponse.Json(JsonSerializer.Serialize(result, OcsJsonOptions));
    }

    private string BuildMissReason(SearchResponse response)
    {
        if (response.Ranked.Count > 0)
            return $"找到 {response.Ranked.Count} 条候选，但相似度都低于 {Config.MinScore:P0}";

        var errors = response.Outcomes
            .Where(o => !o.Ok && !string.IsNullOrWhiteSpace(o.Error))
            .Select(o => o.SourceName + "：" + o.Error)
            .ToList();

        if (errors.Count > 0) return "题库工坊未命中 —— " + string.Join("；", errors);

        return "题库工坊未命中";
    }

    private sealed class QueryInput
    {
        public string Title { get; set; } = string.Empty;
        public string Options { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    private QueryInput ReadQuery(MiniHttpRequest request, out string? error, out bool badToken)
    {
        error = null;
        badToken = false;

        var input = new QueryInput
        {
            Title = request.QueryValue("title") ?? string.Empty,
            Options = request.QueryValue("options") ?? string.Empty,
            Type = request.QueryValue("type") ?? string.Empty,
            Token = request.QueryValue("token") ?? request.Header("x-qbank-token") ?? string.Empty,
        };

        // POST 支持 JSON 体（OCS 的 POST data 就是 JSON）
        if (request.Method == "POST" && !string.IsNullOrWhiteSpace(request.BodyText))
        {
            try
            {
                var node = JsonNode.Parse(request.BodyText);
                if (node is JsonObject obj)
                {
                    input.Title = PickString(obj, "title", "problem", "question", "q", "题干", "题目") ?? input.Title;
                    input.Options = PickString(obj, "options", "option", "选项") ?? input.Options;
                    input.Type = PickString(obj, "type", "题型") ?? input.Type;
                    input.Token = PickString(obj, "token", "key") ?? input.Token;
                }
            }
            catch (JsonException)
            {
                // 也可能是表单体
                try
                {
                    var form = ParseForm(request.BodyText);
                    if (form.TryGetValue("title", out var t) && string.IsNullOrEmpty(input.Title)) input.Title = t;
                    if (form.TryGetValue("options", out var o) && string.IsNullOrEmpty(input.Options)) input.Options = o;
                    if (form.TryGetValue("type", out var ty) && string.IsNullOrEmpty(input.Type)) input.Type = ty;
                    if (form.TryGetValue("token", out var tk) && string.IsNullOrEmpty(input.Token)) input.Token = tk;
                }
                catch (Exception)
                {
                    error = "请求体既不是合法 JSON 也不是合法表单";
                }
            }
        }

        var expected = Config.Token?.Trim();
        if (!string.IsNullOrEmpty(expected) &&
            !string.Equals(input.Token?.Trim(), expected, StringComparison.Ordinal))
        {
            badToken = true;
        }

        return input;
    }

    private static string? PickString(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var pair in obj)
            {
                if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;

                if (pair.Value is null) return string.Empty;
                if (pair.Value is JsonValue value)
                {
                    if (value.TryGetValue<string>(out var text)) return text;
                    return value.ToJsonString();
                }
                return pair.Value.ToJsonString();
            }
        }
        return null;
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    private static MiniHttpResponse OcsError(string message)
        => MiniHttpResponse.Json(JsonSerializer.Serialize(new object[] { message, null! }, OcsJsonOptions));

    // ---------------- 生成给 OCS 的配置 ----------------

    /// <summary>
    /// 生成可直接粘贴进 OCS 的 AnswererWrapper JSON 数组。
    /// handler 里做了一次"整个响应体再 JSON.parse"，这样 OCS 那边 contentType 无论按 json
    /// 还是 text 处理都能拿到结果。
    /// </summary>
    public string BuildOcsConfig()
    {
        var host = Config.EffectiveHost;
        var port = Port;
        var token = Config.Token?.Trim() ?? string.Empty;

        var url = $"http://{host}:{port}/qbank/ocs/search?title=${{title}}&options=${{options}}&type=${{type}}";
        if (!string.IsNullOrEmpty(token))
            url += "&token=" + Uri.EscapeDataString(token);

        var wrapper = new JsonObject
        {
            ["url"] = url,
            ["name"] = string.IsNullOrWhiteSpace(Config.DisplayName) ? "题库工坊" : Config.DisplayName,
            ["homepage"] = $"http://{host}:{port}/qbank/ocs/docs",
            ["method"] = "get",
            ["contentType"] = "text",
            ["type"] = "fetch",
            ["headers"] = new JsonObject(),
            ["handler"] = HandlerSource,
        };

        var array = new JsonArray { wrapper };
        return array.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>OCS handler 源码（原生 JS，不依赖任何 OCS 私有 API）。</summary>
    private const string HandlerSource =
        "return (res) => {\n" +
        "  let data = res;\n" +
        "  if (typeof res === 'string') {\n" +
        "    try { data = JSON.parse(res); } catch (e) { return [String(res), undefined]; }\n" +
        "  }\n" +
        "  if (!Array.isArray(data)) return undefined;\n" +
        "\n" +
        "  // 未命中 / 口令不对：服务端给的是扁平的一条 [提示, null]\n" +
        "  if (data.length === 2 && typeof data[0] === 'string' &&\n" +
        "      (data[1] === null || data[1] === undefined)) {\n" +
        "    return [data[0], undefined];\n" +
        "  }\n" +
        "\n" +
        "  const out = [];\n" +
        "  for (const item of data) {\n" +
        "    if (!Array.isArray(item) || item.length < 2) continue;\n" +
        "    const answer = item[1];\n" +
        "    if (answer === null || answer === undefined || answer === '') continue;\n" +
        "    out.push([item[0] === null || item[0] === undefined ? undefined : String(item[0]), String(answer)]);\n" +
        "  }\n" +
        "  return out.length > 0 ? out : undefined;\n" +
        "};";

    private string PingJson()
    {
        var host = Config.EffectiveHost;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            app = "QBank",
            name = string.IsNullOrWhiteSpace(Config.DisplayName) ? "题库工坊" : Config.DisplayName,
            version = "1.0",
            port = Port,
            allowLan = Config.AllowLan,
            needToken = !string.IsNullOrWhiteSpace(Config.Token),
            url = $"http://{host}:{Port}/qbank/ocs/search",
            sources = new
            {
                localBank = Config.UseLocalBank,
                lanShare = Config.UseLanShare,
                ai = Config.UseAi,
                ocs = Config.UseOcs,
            },
            stats = new { requests = RequestCount, hits = HitCount, misses = MissCount },
            endpoints = new[]
            {
                "GET /qbank/ocs/search?title=&options=&type=",
                "POST /qbank/ocs/search  {title, options, type}",
                "GET /qbank/ocs/config   现成的 OCS 配置（粘贴用）",
                "GET /qbank/ocs/ocs.json 同上，纯 JSON（当配置链接用）",
                "GET /qbank/ocs/docs",
            },
        }, OcsJsonOptions);
    }

    private string ConfigJson()
        => JsonSerializer.Serialize(new
        {
            port = Port,
            allowLan = Config.AllowLan,
            needToken = !string.IsNullOrWhiteSpace(Config.Token),
            url = $"http://{Config.EffectiveHost}:{Port}/qbank/ocs/search",
            ocsConfig = BuildOcsConfig(),
        }, OcsJsonOptions);

    private string DocsJson()
        => JsonSerializer.Serialize(new
        {
            name = "题库工坊 OCS 对接口",
            summary = "把本机的本地题库 / 局域网节点 / AI 模型聚合起来，供 OCS 脚本自动答题。",
            usage = new[]
            {
                "1. 在本程序「设置 → OCS 对接」里勾选启用，保存。",
                "2. 点「复制 OCS 配置」，把生成的 JSON 粘到 OCS 的题库设置里（或点「一键导入 OCS」走链接导入）。",
                "3. OCS 收题时就会自动来问本程序。",
            },
            search = new
            {
                method = "GET 或 POST",
                url = $"/qbank/ocs/search",
                parameters = new
                {
                    title = "题干（必填）",
                    options = "选项文本，一行一个（可选）",
                    type = "single / multiple / judgement / completion（可选）",
                    token = "访问口令（配置里设了口令时需要）",
                },
                response = "[[null, '答案'], ...]；未命中时为 [['提示信息', null]]",
            },
            endpoints = new
            {
                ping = "/qbank/ocs/ping",
                config = "/qbank/ocs/config",
                configLink = "/qbank/ocs/ocs.json",
                docs = "/qbank/ocs/docs",
                export = "/qbank/ocs/export",
            },
        }, OcsJsonOptions);

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    internal static readonly JsonSerializerOptions OcsJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public void Dispose() => Stop();
}
