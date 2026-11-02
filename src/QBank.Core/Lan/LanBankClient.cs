using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QBank.Core.Models;

namespace QBank.Core.Lan;

/// <summary>局域网题库客户端：向某个节点发搜题/写入/导出请求。</summary>
public sealed class LanBankClient : IDisposable
{
    private readonly LanPeerConfig _peer;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LanBankClient(LanPeerConfig peer, HttpClient? httpClient = null)
    {
        _peer = peer;
        if (httpClient is null)
        {
            _http = HttpClients.Create();
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public LanPeerConfig Peer => _peer;

    public string BaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_peer.BaseUrl))
                return _peer.BaseUrl!.TrimEnd('/');

            var host = string.IsNullOrWhiteSpace(_peer.Host) ? "127.0.0.1" : _peer.Host!.Trim();
            var port = _peer.Port <= 0 ? 18321 : _peer.Port;
            return $"http://{host}:{port}";
        }
    }

    public async Task<LanInfo?> PingAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{BaseUrl}/qbank/ping", ct);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<LanInfo>(json, LanJson.Options);
    }

    public async Task<LanQueryResponse> QueryAsync(LanQueryRequest request, CancellationToken ct = default)
    {
        request.Token ??= _peer.Token;
        request.ClientName ??= Environment.MachineName;

        var body = JsonSerializer.Serialize(request, LanJson.Options);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{BaseUrl}/qbank/query", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (json.Length == 0)
            return new LanQueryResponse { Ok = false, Error = $"节点无响应（HTTP {(int)response.StatusCode}）" };

        LanQueryResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LanQueryResponse>(json, LanJson.Options);
        }
        catch (JsonException)
        {
            return new LanQueryResponse { Ok = false, Error = "节点返回的内容不是合法 JSON" };
        }

        if (parsed is null)
            return new LanQueryResponse { Ok = false, Error = "节点返回空响应" };

        if (!parsed.Ok && string.IsNullOrWhiteSpace(parsed.Error))
            parsed.Error = $"HTTP {(int)response.StatusCode}";
        return parsed;
    }

    /// <summary>把一条题目推到远端题库（需要对方开启远程写入）。</summary>
    public async Task<bool> AddAsync(LanHit hit, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(hit, LanJson.Options);
        var url = $"{BaseUrl}/qbank/add";
        if (!string.IsNullOrWhiteSpace(_peer.Token))
            url += "?token=" + Uri.EscapeDataString(_peer.Token!);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> ExportAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/qbank/export";
        if (!string.IsNullOrWhiteSpace(_peer.Token))
            url += "?token=" + Uri.EscapeDataString(_peer.Token!);

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>连通性测试，返回 (成功, 说明)。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await PingAsync(ct);
            if (info is null) return (false, "节点没有回应 /qbank/ping");
            return (true, $"{info.Name} · {info.QuestionCount} 道题 · 端口 {info.Port}" +
                         (info.NeedToken ? " · 需要口令" : string.Empty));
        }
        catch (TaskCanceledException)
        {
            return (false, "连接超时");
        }
        catch (HttpRequestException ex)
        {
            return (false, "无法连接：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}