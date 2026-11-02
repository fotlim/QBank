using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QBank.Core.Models;

namespace QBank.Core.Lan;

/// <summary>
/// UDP 广播发现：服务端监听 DiscoveryPort 应答，客户端广播询问并收集应答。
/// 报文格式很简单：请求固定字符串 "QBANK_DISCOVER"（可带口令），应答是一段 LanInfo JSON。
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const string DiscoverMessage = "QBANK_DISCOVER";

    private readonly LanServerConfig _config;
    private readonly Func<LanInfo> _infoProvider;

    private UdpClient? _responder;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LanDiscoveryService(LanServerConfig config, Func<LanInfo> infoProvider)
    {
        _config = config;
        _infoProvider = infoProvider;
    }

    public bool IsRunning => _responder is not null;

    public int DiscoveryPort => _config.DiscoveryPort <= 0 ? 39991 : _config.DiscoveryPort;

    public event EventHandler<string>? Log;

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            var client = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
                ExclusiveAddressUse = false,
            };
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            _responder = client;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RespondLoopAsync(client, _cts.Token));
            Log?.Invoke(this, $"局域网发现已开启：UDP {DiscoveryPort}");
        }
        catch (SocketException ex)
        {
            Log?.Invoke(this, $"局域网发现启动失败（端口 {DiscoveryPort} 可能被占用）：{ex.Message}");
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _responder?.Dispose(); } catch (SocketException) { }
        _responder = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RespondLoopAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            try
            {
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (!text.StartsWith(DiscoverMessage, StringComparison.OrdinalIgnoreCase)) continue;

                var providedToken = text.Length > DiscoverMessage.Length
                    ? text[DiscoverMessage.Length..].Trim().TrimStart('|', ':').Trim()
                    : string.Empty;

                var expected = _config.Token?.Trim();
                if (!string.IsNullOrEmpty(expected) &&
                    !string.Equals(providedToken, expected, StringComparison.Ordinal))
                    continue;

                var info = _infoProvider();
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, LanJson.Options));
                await client.SendAsync(payload, result.RemoteEndPoint, ct);
            }
            catch (JsonException) { }
            catch (SocketException) { }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>广播一次并收集应答（阻塞至超时）。</summary>
    public static async Task<List<LanDiscoveredPeer>> DiscoverAsync(
        int discoveryPort,
        TimeSpan timeout,
        string? token = null,
        CancellationToken ct = default)
    {
        var results = new List<LanDiscoveredPeer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var message = string.IsNullOrWhiteSpace(token)
            ? DiscoverMessage
            : DiscoverMessage + "|" + token!.Trim();
        var payload = Encoding.UTF8.GetBytes(message);

        foreach (var endpoint in BroadcastTargets(discoveryPort <= 0 ? 39991 : discoveryPort))
        {
            try
            {
                await client.SendAsync(payload, endpoint, ct);
            }
            catch (SocketException) { }
            catch (IOException) { }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(timeoutCts.Token);
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (!text.StartsWith('{')) continue;

                LanInfo? info;
                try
                {
                    info = JsonSerializer.Deserialize<LanInfo>(text, LanJson.Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (info is null || !string.Equals(info.App, "QBank", StringComparison.OrdinalIgnoreCase)) continue;

                var host = result.RemoteEndPoint.Address.ToString();
                info.Host = host;
                if (!seen.Add(host + ":" + info.Port)) continue;

                results.Add(new LanDiscoveredPeer { Host = host, Info = info });
            }
        }
        catch (OperationCanceledException) { }

        return results;
    }

    /// <summary>广播目标：全局广播 + 各网卡的子网广播地址。</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets(int port)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var global = new IPEndPoint(IPAddress.Broadcast, port);
        seen.Add(global.ToString());
        yield return global;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null) continue;

                var address = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (address.Length != 4 || mask.Length != 4) continue;

                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(address[i] | (mask[i] ^ 0xFF));

                var endpoint = new IPEndPoint(new IPAddress(broadcast), port);
                if (seen.Add(endpoint.ToString()))
                    yield return endpoint;
            }
        }
    }
}