using System.Net;

namespace QBank.Core;

/// <summary>共享的 HTTP 客户端工厂。</summary>
public static class HttpClients
{
    private static HttpClient? _shared;

    /// <summary>默认客户端：自动解压、跟随跳转、Cookie 容器独立。</summary>
    public static HttpClient Shared => _shared ??= Create();

    public static HttpClient Create(bool allowInsecureCertificate = false, TimeSpan? poolLifetime = null)
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
}