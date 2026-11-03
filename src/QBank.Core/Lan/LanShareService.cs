using System.Diagnostics;
using QBank.Core.Models;
using QBank.Core.Storage;

namespace QBank.Core.Lan;

/// <summary>
/// 局域网共享总控：按配置起停本机服务端、响应发现广播、并把搜题请求扇出到所有已启用节点。
/// </summary>
public sealed class LanShareService : IDisposable
{
    private readonly LocalBank _bank;
    private readonly Func<string> _versionProvider;

    private LanBankServer? _server;
    private LanDiscoveryService? _discovery;

    public LanShareService(LocalBank bank, Func<string>? versionProvider = null)
    {
        _bank = bank;
        _versionProvider = versionProvider ?? (() => "1.0");
    }

    public LanBankServer? Server => _server;

    public LanDiscoveryService? Discovery => _discovery;

    public bool IsServerRunning => _server?.IsRunning == true;

    public bool IsDiscoveryRunning => _discovery?.IsRunning == true;

    public LanServerConfig Config { get; private set; } = new();

    public List<LanPeerConfig> KnownPeers { get; } = new();

    public event EventHandler<string>? Log;

    public string StatusText
    {
        get
        {
            if (!Config.Enabled) return "共享未开启";
            var parts = new List<string>();
            parts.Add(_server?.StatusText ?? "服务端未启动");
            parts.Add(_discovery?.IsRunning == true ? $"发现端口 {_discovery.DiscoveryPort}" : "发现未开启");
            if (Config.AllowLan) parts.Add(Config.AllowRemoteWrite ? "允许局域网读写" : "局域网只读");
            else parts.Add("仅本机");
            if (!string.IsNullOrWhiteSpace(Config.Token)) parts.Add("已设口令");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>按配置启动/停止服务端与发现；配置未开启则全部停下。</summary>
    public void ApplyConfig(AppConfig config)
    {
        Config = config.LanShare ??= new LanServerConfig();

        KnownPeers.Clear();
        KnownPeers.AddRange(config.LanPeers.Where(p => p.Enabled));

        if (!Config.Enabled)
        {
            StopServer();
            return;
        }

        try
        {
            if (_server is null)
            {
                _server = new LanBankServer(
                    Config,
                    HandleQuery,
                    BuildInfo,
                    Config.AllowRemoteWrite ? HandleRemoteAdd : null,
                    Config.AllowRemoteWrite ? (since, limit) => _bank.ExportJson(since, limit) : null);
                _server.Log += (_, message) => Log?.Invoke(this, message);
            }

            if (!_server.IsRunning) _server.Start();

            if (Config.RespondToDiscovery)
            {
                _discovery ??= new LanDiscoveryService(Config, BuildInfo);
                _discovery.Log += (_, message) => Log?.Invoke(this, message);
                if (!_discovery.IsRunning) _discovery.Start();
            }
            else
            {
                StopDiscovery();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, "启动共享服务失败：" + ex.Message);
        }
    }

    public void StopServer()
    {
        _server?.Stop();
        StopDiscovery();
    }

    private void StopDiscovery()
    {
        _discovery?.Stop();
    }

    public LanInfo BuildInfo() => new()
    {
        App = "QBank",
        Name = string.IsNullOrWhiteSpace(Config.ServerName) ? Environment.MachineName : Config.ServerName,
        Version = _versionProvider(),
        Id = Config.Token is { Length: > 0 } token ? Hash(token) : Environment.MachineName,
        Port = _server?.Port ?? (Config.Port <= 0 ? 18321 : Config.Port),
        QuestionCount = SafeCount(),
        AllowWrite = Config.AllowRemoteWrite,
        NeedToken = !string.IsNullOrWhiteSpace(Config.Token),
    };

    private int SafeCount()
    {
        try { return _bank.Count(); }
        catch (Exception) { return 0; }
    }

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private LanQueryResponse HandleQuery(LanQueryRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new LanQueryResponse
        {
            Ok = true,
            ServerName = string.IsNullOrWhiteSpace(Config.ServerName) ? Environment.MachineName : Config.ServerName,
            Version = _versionProvider(),
        };

        try
        {
            var type = QuestionTypeExtensions.ParseOcs(request.Type);
            if (type == QuestionType.Unknown)
                type = QuestionTypeInference.Infer(request.Title, request.Options);

            var limit = Math.Clamp(request.Limit <= 0 ? Config.MaxResultsPerQuery : request.Limit, 1, 50);
            var found = _bank.Search(request.Title, type, limit, 0.45, registerHit: true);

            foreach (var item in found)
                response.Hits.Add(LanQuestionMapper.ToHit(item.Question, item.Score));
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = "本机题库检索失败：" + ex.Message;
        }

        stopwatch.Stop();
        response.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return response;
    }

    private bool HandleRemoteAdd(LanHit hit)
    {
        try
        {
            var question = LanQuestionMapper.ToQuestion(hit);
            question.Source = "局域网共享";
            _bank.Upsert(question);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, "远端写库失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>广播发现局域网里的其他节点。</summary>
    public static async Task<List<LanDiscoveredPeer>> DiscoverAsync(
        LanServerConfig config,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        => await LanDiscoveryService.DiscoverAsync(
            config.DiscoveryPort <= 0 ? 39991 : config.DiscoveryPort,
            timeout ?? TimeSpan.FromSeconds(2.5),
            config.Token,
            ct);

    /// <summary>把搜题请求扇出到所有已启用节点，每个节点一条 SourceOutcome。</summary>
    public async Task<List<SourceOutcome>> QueryPeersAsync(Models.SearchRequest request, CancellationToken ct = default)
    {
        var peers = KnownPeers.ToList();
        var outcomes = new List<SourceOutcome>();
        if (peers.Count == 0) return outcomes;

        var tasks = peers.Select(peer => QueryOneAsync(peer, request, ct)).ToList();
        outcomes.AddRange(await Task.WhenAll(tasks));
        return outcomes;
    }

    private async Task<SourceOutcome> QueryOneAsync(LanPeerConfig peer, Models.SearchRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = peer.Id ?? peer.DisplayName,
            SourceName = string.IsNullOrWhiteSpace(peer.Name) ? peer.DisplayName : peer.Name,
            Kind = SourceKind.LanShare,
        };

        using var client = new LanBankClient(peer);
        try
        {
            var response = await client.QueryAsync(new LanQueryRequest
            {
                Title = request.Title,
                Type = request.Type.ToOcs(),
                Options = request.Options,
                Limit = Math.Max(request.Limit, 5),
            }, ct);

            outcome.Ok = response.Ok;
            outcome.Error = response.Error;

            if (response.Ok)
            {
                foreach (var hit in response.Hits)
                {
                    var question = LanQuestionMapper.ToQuestion(hit);
                    question.Source = $"局域网 · {outcome.SourceName}";
                    outcome.Hits.Add(new SearchHit
                    {
                        Question = hit.Title,
                        Answer = question.Answer,
                        Score = hit.Score <= 0 ? 0.7 : Math.Clamp(hit.Score, 0, 1),
                        Note = $"来自 {outcome.SourceName}" +
                               (string.IsNullOrWhiteSpace(response.ServerName) ? string.Empty : $" · {response.ServerName}"),
                    });
                }
            }

            peer.LastSeenAt = DateTimeOffset.Now;
            peer.QuestionCount = response.Hits.Count > 0 ? Math.Max(peer.QuestionCount, response.Hits.Count) : peer.QuestionCount;
        }
        catch (TaskCanceledException)
        {
            outcome.Ok = false;
            outcome.Error = "连接超时";
        }
        catch (Exception ex)
        {
            outcome.Ok = false;
            outcome.Error = "无法连接：" + ex.Message;
        }

        stopwatch.Stop();
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }

    /// <summary>从一个节点拉题库。incremental=true 时只拉对方自上次同步以后改动过的题目。</summary>
    public async Task<LanPullResult> PullFromAsync(
        LanPeerConfig peer, bool incremental, CancellationToken ct = default)
    {
        var result = new LanPullResult
        {
            PeerName = string.IsNullOrWhiteSpace(peer.Name) ? peer.DisplayName : peer.Name,
            Incremental = incremental,
        };

        var since = incremental ? peer.LastSyncAt : null;

        using var client = new LanBankClient(peer);
        try
        {
            var json = await client.ExportAsync(since, 0, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                result.Error = "节点没有返回内容（对方可能没开「允许局域网读写」）";
                return result;
            }

            var imported = _bank.Import(json, "json");
            result.Added = imported.Added;
            result.Updated = imported.Updated;
            result.Skipped = imported.Skipped;
            result.Ok = true;

            peer.LastSyncAt = DateTimeOffset.Now;
            peer.LastSyncCount = imported.Added + imported.Updated;
            peer.LastSeenAt = DateTimeOffset.Now;
            Log?.Invoke(this, $"从「{result.PeerName}」拉了 {result.Added + result.Updated} 道题" +
                              (incremental ? "（增量）" : "（全量）"));
        }
        catch (TaskCanceledException)
        {
            result.Error = "连接超时";
        }
        catch (Exception ex)
        {
            result.Error = "无法连接：" + ex.Message;
        }

        return result;
    }

    /// <summary>把「上次同步时间」清掉，下次拉取就会是全量。</summary>
    public int ForgetSyncHistory()
    {
        var count = 0;
        foreach (var peer in KnownPeers)
        {
            if (peer.LastSyncAt is null && peer.LastSyncCount == 0) continue;
            peer.LastSyncAt = null;
            peer.LastSyncCount = 0;
            count++;
        }
        return count;
    }

    /// <summary>把本地题目推送给远端（对方需允许写入）。</summary>
    public async Task<(int Ok, int Failed)> PushAsync(IEnumerable<Question> questions, CancellationToken ct = default)
    {
        var peers = KnownPeers.ToList();
        var ok = 0;
        var failed = 0;
        foreach (var question in questions)
        {
            var hit = LanQuestionMapper.ToHit(question, 1.0);
            foreach (var peer in peers)
            {
                using var client = new LanBankClient(peer);
                try
                {
                    if (await client.AddAsync(hit, ct)) ok++;
                    else failed++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }
        return (ok, failed);
    }

    public void Dispose()
    {
        _server?.Dispose();
        _discovery?.Dispose();
        _server = null;
        _discovery = null;
    }
}