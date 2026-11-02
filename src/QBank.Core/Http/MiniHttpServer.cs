using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QBank.Core.Http;

/// <summary>极简 HTTP 请求（只解析请求行、头部和 body）。</summary>
public sealed class MiniHttpRequest
{
    public string Method { get; init; } = "GET";

    public string Path { get; init; } = "/";

    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string BodyText { get; init; } = string.Empty;

    public string RemoteEndPoint { get; init; } = "?";

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;
}

/// <summary>极简 HTTP 响应。</summary>
public sealed class MiniHttpResponse
{
    public int Status { get; init; } = 200;

    public string StatusText { get; init; } = "OK";

    public string Body { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    /// <summary>是否带跨域头（浏览器里的油猴脚本从网页上下文调本机服务必须要有）。</summary>
    public bool AllowCors { get; init; } = true;

    public Dictionary<string, string> ExtraHeaders { get; init; } = new();

    public static MiniHttpResponse Json(string body, int status = 200, string statusText = "OK", bool cors = true) => new()
    {
        Status = status,
        StatusText = statusText,
        Body = body,
        ContentType = "application/json; charset=utf-8",
        AllowCors = cors,
    };
}

/// <summary>
/// 极简 HTTP/1.1 服务：用 TcpListener 自己解析请求，不依赖 HttpListener
/// （后者在非管理员 / 沙箱环境下需要 urlacl 注册，会直接抛"句柄无效"）。
/// 只处理一问一答，响应带 Connection: close。
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly IPAddress _address;
    private readonly int _desiredPort;
    private readonly Func<MiniHttpRequest, Task<MiniHttpResponse>> _handler;
    private readonly string _displayName;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public MiniHttpServer(
        IPAddress address,
        int desiredPort,
        Func<MiniHttpRequest, Task<MiniHttpResponse>> handler,
        string displayName = "HTTP 服务")
    {
        _address = address;
        _desiredPort = desiredPort <= 0 ? 18321 : desiredPort;
        _handler = handler;
        _displayName = displayName;
    }

    /// <summary>同步处理器的便捷构造。</summary>
    public static MiniHttpServer ForSync(
        IPAddress address,
        int desiredPort,
        Func<MiniHttpRequest, MiniHttpResponse> handler,
        string displayName = "HTTP 服务")
        => new(address, desiredPort, req => Task.FromResult(handler(req)), displayName);

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public int RequestCount { get; private set; }

    public DateTimeOffset? LastRequestAt { get; private set; }

    public event EventHandler<string>? Log;

    public string StatusText => IsRunning
        ? $"运行中 · 端口 {Port} · 已处理 {RequestCount} 次请求"
        : "未启动";

    /// <summary>启动监听；端口被占就往后试 10 个。</summary>
    public void Start()
    {
        if (IsRunning) return;

        Exception? lastError = null;

        for (var offset = 0; offset <= 10; offset++)
        {
            try
            {
                var listener = new TcpListener(_address, _desiredPort + offset);
                listener.Start(64);
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoopAsync(token));
                Report($"{_displayName}已启动：{_address}:{Port}");
                return;
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"端口 {_desiredPort}~{_desiredPort + 10} 都被占用，{_displayName}无法启动。({lastError?.Message})");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Report($"{_displayName}已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, ct, client.Client.RemoteEndPoint?.ToString() ?? "?")
                    .ConfigureAwait(false);
                if (request is null) return;

                RequestCount++;
                LastRequestAt = DateTimeOffset.Now;

                MiniHttpResponse response;
                if (request.Method == "OPTIONS")
                {
                    // 浏览器跨域预检
                    response = new MiniHttpResponse { Status = 204, StatusText = "No Content", Body = string.Empty };
                }
                else
                {
                    try
                    {
                        response = await _handler(request).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Report("处理请求出错：" + ex.Message);
                        response = MiniHttpResponse.Json(
                            "{\"ok\":false,\"msg\":\"服务内部错误\"}", 500, "Internal Server Error");
                    }
                }

                await WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                Report("连接处理异常：" + ex.Message);
            }
        }
    }

    private static async Task<MiniHttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct, string remote)
    {
        var buffer = new byte[8192];
        using var headerBuffer = new MemoryStream();
        var headerEnd = -1;

        while (headerEnd < 0 && headerBuffer.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            headerBuffer.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(headerBuffer.GetBuffer(), (int)headerBuffer.Length);
        }

        if (headerEnd < 0) return null;

        var headerText = Encoding.UTF8.GetString(headerBuffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var queryString = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var parsed)
            ? Math.Clamp(parsed, 0, MaxBodyBytes)
            : 0;

        var buffered = (int)headerBuffer.Length - (headerEnd + 4);
        if (buffered < 0) buffered = 0;

        byte[] body;
        if (contentLength == 0)
        {
            body = Array.Empty<byte>();
        }
        else
        {
            body = new byte[contentLength];
            if (buffered > 0)
            {
                var copy = Math.Min(buffered, contentLength);
                Array.Copy(headerBuffer.GetBuffer(), headerEnd + 4, body, 0, copy);
                if (copy < contentLength)
                    await ReadFullyAsync(stream, body, copy, contentLength - copy, ct).ConfigureAwait(false);
            }
            else
            {
                await ReadFullyAsync(stream, body, 0, contentLength, ct).ConfigureAwait(false);
            }
        }

        return new MiniHttpRequest
        {
            Method = method,
            Path = path,
            Query = ParseQuery(queryString),
            Headers = headers,
            BodyText = body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(body),
            RemoteEndPoint = remote,
        };
    }

    private static async Task ReadFullyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
        }
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(queryString)) return result;

        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, MiniHttpResponse response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {response.StatusText}\r\n");
        head.Append($"Content-Type: {response.ContentType}\r\n");
        head.Append($"Content-Length: {body.Length}\r\n");

        if (response.AllowCors)
        {
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            head.Append("Access-Control-Allow-Headers: Content-Type, X-QBank-Token, Authorization\r\n");
            // Chrome 的 Private Network Access：网页从公网域访问 127.0.0.1 需要这个头
            head.Append("Access-Control-Allow-Private-Network: true\r\n");
            head.Append("Access-Control-Max-Age: 86400\r\n");
        }

        foreach (var pair in response.ExtraHeaders)
            head.Append($"{pair.Key}: {pair.Value}\r\n");

        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Report(string message) => Log?.Invoke(this, message);

    public void Dispose() => Stop();
}
