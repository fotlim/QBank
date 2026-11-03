using System.Net;
using System.Text.Json;
using QBank.Core.Http;
using QBank.Core.Models;

namespace QBank.Core.Lan;

/// <summary>
/// 局域网题库共享服务（TcpListener 极简 HTTP，不需要 urlacl）。
/// 路由：
///   GET  /qbank/ping       → LanInfo
///   POST /qbank/query      → LanQueryResponse（搜题）
///   POST /qbank/add        → 写入一条题目（需 AllowRemoteWrite）
///   GET  /qbank/export     → 题库 JSON（需 AllowRemoteWrite；带 since=ISO 时间则只导出自那以后改动的，用来做增量同步）
/// </summary>
public sealed class LanBankServer : IDisposable
{
    private readonly LanServerConfig _config;
    private readonly Func<LanQueryRequest, LanQueryResponse> _queryHandler;
    private readonly Func<LanHit, bool>? _addHandler;
    private readonly Func<DateTimeOffset?, int, string>? _exportHandler;
    private readonly Func<LanInfo> _infoProvider;

    private MiniHttpServer? _server;

    public LanBankServer(
        LanServerConfig config,
        Func<LanQueryRequest, LanQueryResponse> queryHandler,
        Func<LanInfo> infoProvider,
        Func<LanHit, bool>? addHandler = null,
        Func<DateTimeOffset?, int, string>? exportHandler = null)
    {
        _config = config;
        _queryHandler = queryHandler;
        _infoProvider = infoProvider;
        _addHandler = addHandler;
        _exportHandler = exportHandler;
    }

    public bool IsRunning => _server?.IsRunning == true;

    public int Port => _server?.Port ?? 0;

    public int RequestCount => _server?.RequestCount ?? 0;

    public DateTimeOffset? LastRequestAt => _server?.LastRequestAt;

    public event EventHandler<string>? Log;

    public string StatusText => _server?.StatusText ?? "未启动";

    public void Start()
    {
        if (IsRunning) return;

        var address = _config.AllowLan ? IPAddress.Any : IPAddress.Loopback;
        var desired = _config.Port <= 0 ? 18321 : _config.Port;

        _server = new MiniHttpServer(address, desired, req => Task.FromResult(Route(req)), "局域网题库服务");
        _server.Log += (_, message) => Log?.Invoke(this, message);
        _server.Start();
    }

    public void Stop()
    {
        _server?.Stop();
        _server = null;
    }

    private MiniHttpResponse Route(MiniHttpRequest request)
    {
        var path = request.Path;
        var token = request.Header("x-qbank-token") ?? request.QueryValue("token");
        var remote = request.RemoteEndPoint;

        if (path.Equals("/qbank/ping", StringComparison.OrdinalIgnoreCase))
            return MiniHttpResponse.Json(JsonSerializer.Serialize(_infoProvider(), LanJson.Options));

        if (path.Equals("/qbank/query", StringComparison.OrdinalIgnoreCase))
        {
            LanQueryRequest? query;
            try
            {
                query = JsonSerializer.Deserialize<LanQueryRequest>(request.BodyText, LanJson.Options);
            }
            catch (JsonException)
            {
                return Json(new LanQueryResponse { Ok = false, Error = "请求体不是合法 JSON" }, 400, "Bad Request");
            }

            if (query is null || string.IsNullOrWhiteSpace(query.Title))
                return Error("缺少题目内容 (title)", 400, "Bad Request");

            if (!CheckToken(query.Token ?? token, out var authError))
                return Error(authError!, 401, "Unauthorized");

            query.Limit = Math.Clamp(query.Limit <= 0 ? 5 : query.Limit, 1, 50);

            try
            {
                var response = _queryHandler(query);
                Log?.Invoke(this, $"来自 {remote} 的搜题：{Shorten(query.Title, 24)} → {response.Hits.Count} 条");
                return Json(response);
            }
            catch (Exception ex)
            {
                return Error("服务端处理失败：" + ex.Message, 500, "Internal Server Error");
            }
        }

        if (path.Equals("/qbank/add", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.AllowRemoteWrite)
                return Error("服务端未开启远程写入", 403, "Forbidden");
            if (!CheckToken(token, out var authError2))
                return Error(authError2!, 401, "Unauthorized");
            if (_addHandler is null)
                return Error("服务端不支持写入", 501, "Not Implemented");

            LanHit? hit;
            try
            {
                hit = JsonSerializer.Deserialize<LanHit>(request.BodyText, LanJson.Options);
            }
            catch (JsonException)
            {
                return Error("请求体不是合法 JSON", 400, "Bad Request");
            }

            if (hit is null || string.IsNullOrWhiteSpace(hit.Title) || string.IsNullOrWhiteSpace(hit.Answer))
                return Error("题目或答案为空", 400, "Bad Request");

            var ok = _addHandler(hit);
            Log?.Invoke(this, $"来自 {remote} 的新题入库：{Shorten(hit.Title, 24)}");
            return MiniHttpResponse.Json(
                JsonSerializer.Serialize(new { ok, message = ok ? "已保存" : "保存失败" }, LanJson.Options),
                ok ? 200 : 500,
                ok ? "OK" : "Internal Server Error");
        }

        if (path.Equals("/qbank/export", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.AllowRemoteWrite)
                return Error("服务端未开启远程导出", 403, "Forbidden");
            if (!CheckToken(token, out var authError3))
                return Error(authError3!, 401, "Unauthorized");
            if (_exportHandler is null)
                return Error("服务端不支持导出", 501, "Not Implemented");

            // since=ISO 时间：只导出那之后改动过的题目（增量同步用）。解析不了就当全量。
            DateTimeOffset? since = null;
            var sinceText = request.QueryValue("since");
            if (!string.IsNullOrWhiteSpace(sinceText) &&
                DateTimeOffset.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                since = parsed;
            }

            var limit = 0;
            var limitText = request.QueryValue("limit");
            if (!string.IsNullOrWhiteSpace(limitText) && int.TryParse(limitText, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 0, 100000);

            Log?.Invoke(this, since is null
                ? $"来自 {remote} 的题库导出（全量）"
                : $"来自 {remote} 的题库导出（{since:yyyy-MM-dd HH:mm:ss} 之后）");

            return MiniHttpResponse.Json(_exportHandler(since, limit));
        }

        return Error($"未知接口：{path}", 404, "Not Found");
    }

    private bool CheckToken(string? provided, out string? error)
    {
        var expected = _config.Token?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            error = null;
            return true;
        }

        if (string.Equals(provided?.Trim(), expected, StringComparison.Ordinal))
        {
            error = null;
            return true;
        }

        error = "访问口令不正确";
        return false;
    }

    private static MiniHttpResponse Json<T>(T value, int status = 200, string text = "OK")
        => MiniHttpResponse.Json(JsonSerializer.Serialize(value, LanJson.Options), status, text);

    private static MiniHttpResponse Error(string message, int status, string text)
        => MiniHttpResponse.Json(
            JsonSerializer.Serialize(new { ok = false, error = message }, LanJson.Options), status, text);

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => Stop();
}
