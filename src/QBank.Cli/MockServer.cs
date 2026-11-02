using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QBank.Cli;

/// <summary>
/// 极简 HTTP 桩服务，用 TcpListener 手写 HTTP/1.1（不用 HttpListener ——
/// 那个需要 urlacl 权限，非管理员/沙箱里会抛「句柄无效」）。
/// 用来在本机模拟 OCS 题库与 OpenAI 兼容接口，让 selftest 不依赖外网。
/// </summary>
internal sealed class MockServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Func<string, string, (int Status, string ContentType, string Body)>> _routes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>记下每次请求的 (方法, 路径, 查询串, 请求体)，方便断言。</summary>
    public List<(string Method, string Path, string Query, string Body)> Requests { get; } = new();

    public string BaseUrl { get; }

    public MockServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>路径 → 处理函数（入参：查询串、请求体；返回：状态码、内容类型、响应体）。</summary>
    public void Map(string path, Func<string, string, (int, string, string)> handler)
        => _routes[path] = handler;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 10000;

            // 先把请求头读完整（以 \r\n\r\n 结束）
            var buffer = new List<byte>(8192);
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one.AsMemory(0, 1), _cts.Token).ConfigureAwait(false);
                if (n <= 0) return;
                buffer.Add(one[0]);
                if (buffer.Count >= 4 &&
                    buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n' &&
                    buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
                    break;
                if (buffer.Count > 65536) return;
            }

            var headerText = Encoding.UTF8.GetString(buffer.ToArray());
            var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (headerLines.Length == 0) return;

            var requestLine = headerLines[0].Split(' ');
            if (requestLine.Length < 2) return;
            var method = requestLine[0];
            var target = requestLine[1];

            var contentLength = 0;
            foreach (var line in headerLines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out var len))
                    contentLength = len;
            }

            string body = string.Empty;
            if (contentLength > 0)
            {
                var bodyBuffer = new byte[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await stream.ReadAsync(bodyBuffer.AsMemory(read, contentLength - read), _cts.Token)
                        .ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(bodyBuffer, 0, read);
            }

            var path = target;
            var query = string.Empty;
            var qIndex = target.IndexOf('?');
            if (qIndex >= 0)
            {
                path = target[..qIndex];
                query = target[(qIndex + 1)..];
            }

            lock (Requests) Requests.Add((method, path, query, body));

            (int Status, string ContentType, string Body) result;
            if (_routes.TryGetValue(path, out var handler))
            {
                try
                {
                    result = handler(query, body);
                }
                catch (Exception ex)
                {
                    result = (500, "application/json", $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}");
                }
            }
            else
            {
                result = (404, "application/json", "{\"error\":\"not found\"}");
            }

            var bytes = Encoding.UTF8.GetBytes(result.Body);
            var header = new StringBuilder()
                .Append("HTTP/1.1 ").Append(result.Status).Append(' ').Append(StatusText(result.Status)).Append("\r\n")
                .Append("Content-Type: ").Append(result.ContentType).Append("; charset=utf-8\r\n")
                .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
                .Append("Connection: close\r\n\r\n")
                .ToString();

            await stream.WriteAsync(Encoding.UTF8.GetBytes(header), _cts.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 客户端提前断开或超时，忽略
        }
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "OK",
    };

    /// <summary>从查询串里取参数（已做 URL 解码）。</summary>
    public static string? QueryParam(string query, string name)
    {
        foreach (var pair in (query ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = idx < 0 ? string.Empty : pair[(idx + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
