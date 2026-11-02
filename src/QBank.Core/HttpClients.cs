using System.Net;
using System.Net.Sockets;
using QBank.Core.Models;

namespace QBank.Core;

/// <summary>共享的 HTTP 客户端工厂。</summary>
public static class HttpClients
{
    private static HttpClient? _shared;
    private static NetworkConfig? _network;
    private static string? _autoFallbackNote;

    /// <summary>
    /// 当前网络出口设置。由 App 启动时从配置里灌进来；
    /// 没灌就按系统代理（HttpClient 默认行为）。
    /// </summary>
    public static NetworkConfig? Network
    {
        get => _network;
        set
        {
            _network = value;
            Reset();
        }
    }

    /// <summary>「跟随系统代理」时自动改走直连的原因（没发生回落就是 null）。</summary>
    public static string? AutoFallbackNote => _autoFallbackNote;

    /// <summary>默认客户端（按当前的代理设置构建，设置变了会自动重建）。</summary>
    public static HttpClient Shared => _shared ??= Create();

    /// <summary>配置变了以后调用，下次取 Shared 会重新构建。</summary>
    public static void Reset()
    {
        var old = _shared;
        _shared = null;
        _autoFallbackNote = null;
        try { old?.Dispose(); } catch { /* 忽略 */ }
    }

    public static HttpClient Create(bool allowInsecureCertificate = false, TimeSpan? poolLifetime = null)
        => Create(Network, allowInsecureCertificate, poolLifetime);

    /// <summary>显式指定网络设置的版本（自测用）。</summary>
    public static HttpClient Create(NetworkConfig? network, bool allowInsecureCertificate = false, TimeSpan? poolLifetime = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = poolLifetime ?? TimeSpan.FromMinutes(10),
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        // 代理：direct 时显式置空，绕开系统代理。
        // 这不是小事 —— 系统代理指向一个没在跑的本地端口（比如关了代理软件但注册表还留着
        // 127.0.0.1:12000）时，所有请求（自己的 API、自定义接口、模型列表）都会报
        // 「由于目标计算机积极拒绝，无法连接」。
        if (network is not null)
        {
            var proxy = network.BuildProxy();
            if (proxy is not null && network.FallbackToDirectOnProxyFailure)
            {
                // 「跟随系统代理」时先确认那个代理真的活着；死了就直接走直连，
                // 别让一条过期注册表把整个软件废掉。
                if (TryDescribeSystemProxy(out var proxyDesc, out var dead) && dead)
                {
                    _autoFallbackNote = "系统代理 " + proxyDesc + " 没有响应，已自动改走直连。";
                    proxy = null;
                }
            }

            handler.UseProxy = proxy is not null;
            if (proxy is not null)
                handler.Proxy = proxy;
        }

        if (allowInsecureCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var client = new HttpClient(handler)
        {
            // 超时由每次请求自己的 CancellationToken 控制，这里交给调用方
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 QBank/1.0");
        return client;
    }

    /// <summary>
    /// 看一眼系统代理是什么、是不是指向一个没人监听的本机端口。
    /// 返回 false 表示读不到系统代理（那就谈不上死不死）。
    /// </summary>
    public static bool DescribeSystemProxyState(out string description, out bool dead)
        => TryDescribeSystemProxy(out description, out dead);

    /// <summary>
    /// 看一眼系统代理是什么、是不是指向一个没人监听的本机端口。
    /// 返回 false 表示读不到系统代理（那就谈不上死不死）。
    /// <para>
    /// 两条路都走：`HttpClient.DefaultProxy`（.NET 自己的解析）+ 注册表。
    /// 只用 DefaultProxy 不够 —— 某些进程环境下它会返回 <c>HttpNoProxy</c>，
    /// 明明系统代理配着一个死端口却读不出来。
    /// </para>
    /// </summary>
    private static bool TryDescribeSystemProxy(out string description, out bool dead)
    {
        description = string.Empty;
        dead = false;

        // ① .NET 解析出来的（会尊重 NO_PROXY、PAC 等）
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy is not null &&
                proxy.GetProxy(new Uri("https://api.deepseek.com/v1")) is { } uri)
            {
                description = uri.ToString();
                dead = IsDeadLocalProxy(uri.Host, uri.Port);
                return true;
            }
        }
        catch
        {
            // 落到注册表
        }

        // ② 直接读注册表（Windows 上这才是「系统代理」的真相）
        try
        {
            if (TryReadRegistryProxy(out var proxyUri))
            {
                description = proxyUri;
                var asUri = new Uri(proxyUri);
                dead = IsDeadLocalProxy(asUri.Host, asUri.Port);
                return true;
            }
        }
        catch
        {
            // 读不到就算没有
        }

        return false;
    }

    /// <summary>只有「指向本机端口、且那个端口没人听」才算死代理。</summary>
    private static bool IsDeadLocalProxy(string host, int port)
    {
        var isLocal =
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.Ordinal) ||
            string.Equals(host, "[::1]", StringComparison.Ordinal);
        return isLocal && !IsPortListening(host, port);
    }

    /// <summary>读 HKCU 的 Internet Settings。只认 ProxyEnable=1 且 ProxyServer 有值。</summary>
    private static bool TryReadRegistryProxy(out string proxyUrl)
    {
        proxyUrl = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null) return false;

        // ProxyEnable 可能是 int 也可能是 string，都认
        var raw = key.GetValue("ProxyEnable");
        var enabled = raw switch
        {
            int i => i != 0,
            long l => l != 0,
            string s => s.Trim() is "1" or "true" or "True",
            _ => false,
        };
        if (!enabled) return false;

        var server = key.GetValue("ProxyServer") as string;
        if (string.IsNullOrWhiteSpace(server)) return false;

        var first = server.Split(';')[0].Trim();
        // 形如 http=127.0.0.1:7890;https=... 时取 http 那一段
        if (first.Contains('='))
        {
            var http = server.Split(';')
                .Select(p => p.Split('=', 2))
                .FirstOrDefault(p => p.Length == 2 &&
                    (p[0].Trim().Equals("http", StringComparison.OrdinalIgnoreCase) ||
                     p[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase)));
            if (http is null) return false;
            first = http[1].Trim();
        }

        proxyUrl = first.Contains("://") ? first : "http://" + first;
        return true;
    }

    /// <summary>本机端口有没有人 listen（200ms 超时，不拖慢启动）。</summary>
    public static bool IsPortListening(string host, int port)
    {
        if (port <= 0) return false;
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host.Trim('[', ']'), port);
            return task.Wait(200) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把网络异常翻译成看得懂的中文，尤其是「系统代理挂了」这种坑。
    /// </summary>
    public static string ExplainNetworkError(Exception ex, NetworkConfig? network = null)
    {
        var msg = ex.Message ?? string.Empty;
        var cfg = network ?? _network;

        // 连接被拒 + 本地端口 = 十有八九是系统代理指向了没开的软件
        var looksLikeDeadProxy =
            msg.Contains("积极拒绝", StringComparison.Ordinal) ||
            msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase);
        var mentionsLocalPort =
            msg.Contains("127.0.0.1:", StringComparison.Ordinal) ||
            msg.Contains("localhost:", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("[::1]:", StringComparison.Ordinal);

        if (looksLikeDeadProxy && mentionsLocalPort)
        {
            var mode = cfg?.Describe() ?? "跟随系统代理";
            return "网络请求失败：" + msg +
                   "\n\n看起来是系统代理指向了一个没在运行的本地端口（当前设置：" + mode + "）。" +
                   "\n· 打开代理软件，或者" +
                   "\n· 到「设置 → 网络出口」把代理改成「直连」，再点保存。";
        }

        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return "网络请求超时：" + msg;

        if (msg.Contains("证书", StringComparison.Ordinal) || msg.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            return "网络请求失败（证书校验没通过）：" + msg +
                   "\n如果是自建的 HTTPS 题库/网关，可以在服务商高级参数里允许不安全证书。";

        return "网络请求失败：" + msg;
    }

    /// <summary>当前实际生效的代理描述（诊断用）。</summary>
    public static string DescribeCurrentProxy()
    {
        var note = _autoFallbackNote;
        var text = DescribeCurrentProxyCore();
        return note is null ? text : text + "（" + note + "）";
    }

    private static string DescribeCurrentProxyCore()
    {
        try
        {
            var cfg = _network;
            if (cfg is not null && !string.Equals(cfg.ProxyMode, "auto", StringComparison.OrdinalIgnoreCase))
                return cfg.Describe();

            if (TryDescribeSystemProxy(out var desc, out var dead))
                return dead ? "跟随系统代理（" + desc + "，无人监听）" : "跟随系统代理（" + desc + "）";

            return "直连";
        }
        catch (Exception ex)
        {
            return "未知（" + ex.Message + "）";
        }
    }
}
