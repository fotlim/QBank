using System.Diagnostics;
using QBank.Core.Ai;
using QBank.Core.Lan;
using QBank.Core.Models;
using QBank.Core.Ocs;
using QBank.Core.Storage;

namespace QBank.Core.Search;

/// <summary>
/// 搜题总编排：缓存 → 本地题库 → 局域网 → 在线题库 → 网页搜索 → AI 模型，最后统一打分排序。
/// 各来源并发执行，单个来源失败不影响其它来源。
/// 开了「网页命中就跳过 AI」时，会先等网页和其它免费来源跑完，网页给出可信答案就不再调模型（省 token）。
/// </summary>
public sealed class SearchService
{
    private readonly ConfigStore _configStore;
    private readonly LocalBank _bank;
    private readonly HistoryStore _history;
    private readonly LanShareService _lan;
    private readonly AiChatClient _ai;
    private readonly AnswerWrapperHandler _ocs;
    private readonly WebSearchSource _web;

    public SearchService(
        ConfigStore configStore,
        LocalBank bank,
        HistoryStore history,
        LanShareService? lan = null,
        AiChatClient? ai = null,
        AnswerWrapperHandler? ocs = null,
        WebSearchSource? web = null)
    {
        _configStore = configStore;
        _bank = bank;
        _history = history;
        _lan = lan ?? new LanShareService(bank);
        _ai = ai ?? new AiChatClient();
        _ocs = ocs ?? new AnswerWrapperHandler();
        _web = web ?? new WebSearchSource();
    }

    public AppConfig Config => _configStore.Config;

    /// <summary>最近一次搜索结果（方便界面直接读）。</summary>
    public SearchResponse? LastResponse { get; private set; }

    public event EventHandler<SourceOutcome>? OutcomeArrived;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var cfg = _configStore.Config;
        var sw = Stopwatch.StartNew();
        var response = new SearchResponse
        {
            Title = request.Title,
            Type = request.Type,
            Options = request.Options,
        };

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            response.ElapsedMs = 0;
            response.Outcomes.Add(new SourceOutcome
            {
                SourceId = "input",
                SourceName = "输入",
                Kind = SourceKind.Cache,
                Ok = false,
                Error = "题干为空",
            });
            return response;
        }

        if (request.Limit <= 0) request.Limit = Math.Clamp(cfg.Search.DefaultLimit, 1, 50);

        // 1) 缓存短路：本地题库高置信命中且不强制刷新时直接返回
        var cacheResult = await TryCacheAsync(request, cfg, ct).ConfigureAwait(false);
        if (cacheResult is { } cached)
        {
            response.Outcomes.Add(cached.Outcome);
            response.Ranked.AddRange(cached.Hits);
            response.FromCache = true;
            response.ElapsedMs = sw.ElapsedMilliseconds;
            LastResponse = response;
            await RecordAsync(request, response, cfg, ct).ConfigureAwait(false);
            return response;
        }

        // 2) 并发收集各来源
        var completed = new List<SourceOutcome>();
        var tasks = new List<Task<SourceOutcome>>();

        if (request.UseLocalBank && cfg.Search.UseLocalBank && cfg.LocalBank.Enabled)
            tasks.Add(Task.Run(() => SearchLocalBankAsync(request, cfg, ct), ct));

        if (request.UseLanShare && cfg.Search.UseLanShare && cfg.LanShare.Enabled)
            tasks.Add(Task.Run(() => SearchLanAsync(request, cfg, ct), ct));

        if (request.UseOcs && cfg.Search.UseOcs && cfg.OcsSources.Count > 0)
            tasks.Add(Task.Run(() => SearchOcsAsync(request, cfg, ct), ct));

        var useWeb = request.UseWeb && cfg.Search.UseWeb && cfg.WebSearch.Enabled
            && WebSearchEngines.IsUsable(cfg.WebSearch.Engine, cfg.WebSearch.CustomUrlTemplate);
        var webTask = useWeb ? Task.Run(() => _web.SearchAsync(request, cfg, ct), ct) : null;
        if (webTask is not null) tasks.Add(webTask);

        var useAi = request.UseAi && cfg.Search.UseAi;

        // 开了「网页命中就跳过 AI」：先等免费来源（含网页）跑完再决定要不要调模型
        if (useAi && webTask is not null && cfg.WebSearch.SkipAiOnHit)
        {
            completed.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            tasks.Clear();

            var webOutcome = completed.FirstOrDefault(o => o.Kind == SourceKind.Web);
            var confident = webOutcome is { Ok: true }
                && webOutcome.Hits.Any(h => h.Score >= cfg.WebSearch.MinScore);

            if (confident)
            {
                var skipped = new SourceOutcome
                {
                    SourceId = "ai",
                    SourceName = "AI 模型",
                    Kind = SourceKind.Ai,
                    Ok = true,
                };
                skipped.Notices.Add("网页已经找到可信答案，这次不调模型（省 token）。想强制走模型就把「网页命中就跳过 AI」关掉。");
                completed.Add(skipped);
            }
            else
            {
                completed.Add(await Task.Run(() => SearchAiAsync(request, cfg, ct), ct).ConfigureAwait(false));
            }
        }
        else
        {
            if (useAi) tasks.Add(Task.Run(() => SearchAiAsync(request, cfg, ct), ct));

            if (tasks.Count == 0)
            {
                response.Outcomes.Add(new SourceOutcome
                {
                    SourceId = "none",
                    SourceName = "来源",
                    Kind = SourceKind.Cache,
                    Ok = false,
                    Error = "没有启用任何题目来源，请到设置里开启至少一个",
                });
                response.ElapsedMs = sw.ElapsedMilliseconds;
                LastResponse = response;
                return response;
            }

            completed.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        }

        foreach (var outcome in completed)
        {
            response.Outcomes.Add(outcome);
            OutcomeArrived?.Invoke(this, outcome);
        }

        // 3) 汇总排序
        response.Ranked.AddRange(Rank(completed, request, cfg));

        response.ElapsedMs = sw.ElapsedMilliseconds;
        LastResponse = response;

        await AutoCacheAsync(response, cfg, ct).ConfigureAwait(false);
        await RecordAsync(request, response, cfg, ct).ConfigureAwait(false);
        return response;
    }

    private async Task<(SourceOutcome Outcome, List<SearchHit> Hits)?> TryCacheAsync(
        SearchRequest request, AppConfig cfg, CancellationToken ct)
    {
        if (!request.UseCache || request.ForceRefresh) return null;
        if (!cfg.Search.UseCache || !cfg.LocalBank.Enabled) return null;

        var stopwatch = Stopwatch.StartNew();
        var hits = await Task.Run(() => _bank.Search(
            request.Title,
            request.Type,
            Math.Max(request.Limit, 3),
            Math.Max(cfg.Search.CacheMinScore, 0.5),
            registerHit: true), ct).ConfigureAwait(false);

        stopwatch.Stop();
        if (hits.Count == 0) return null;

        var best = hits[0];
        if (best.Score < cfg.Search.CacheMinScore) return null;

        var outcome = new SourceOutcome
        {
            SourceId = "cache",
            SourceName = "本地缓存",
            Kind = SourceKind.Cache,
            Ok = true,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
        };

        foreach (var item in hits)
        {
            outcome.Hits.Add(new SearchHit
            {
                Question = item.Question.Title,
                Answer = item.Question.Answer,
                Score = item.Score,
                Note = "本地题库命中",
            });
        }

        return (outcome, outcome.Hits.ToList());
    }

    private async Task<SourceOutcome> SearchLocalBankAsync(SearchRequest request, AppConfig cfg, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = "local",
            SourceName = "本地题库",
            Kind = SourceKind.LocalBank,
        };

        try
        {
            var hits = await Task.Run(() => _bank.Search(
                request.Title,
                request.Type,
                request.Limit,
                Math.Max(cfg.LocalBank.MinScore, 0.3),
                registerHit: true), ct).ConfigureAwait(false);

            foreach (var item in hits)
            {
                outcome.Hits.Add(new SearchHit
                {
                    Question = item.Question.Title,
                    Answer = item.Question.Answer,
                    Score = item.Score,
                    Note = string.IsNullOrWhiteSpace(item.Question.Source) ? "本地题库" : item.Question.Source,
                });
            }

            outcome.Ok = true;
        }
        catch (OperationCanceledException)
        {
            outcome.Ok = false;
            outcome.Error = "已取消";
        }
        catch (Exception ex)
        {
            outcome.Ok = false;
            outcome.Error = ex.Message;
        }

        stopwatch.Stop();
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }

    private async Task<SourceOutcome> SearchLanAsync(SearchRequest request, AppConfig cfg, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = "lan",
            SourceName = "局域网共享",
            Kind = SourceKind.LanShare,
        };

        try
        {
            var peers = await _lan.QueryPeersAsync(request, ct).ConfigureAwait(false);
            foreach (var peer in peers)
            {
                outcome.Hits.AddRange(peer.Hits);
                if (!peer.Ok && !string.IsNullOrWhiteSpace(peer.Error))
                    outcome.Notices.Add($"{peer.SourceName}：{peer.Error}");
            }

            outcome.Ok = peers.Count == 0 || peers.Any(p => p.Ok);
            if (peers.Count == 0)
                outcome.Notices.Add("没有已连接的局域网节点");
        }
        catch (OperationCanceledException)
        {
            outcome.Ok = false;
            outcome.Error = "已取消";
        }
        catch (Exception ex)
        {
            outcome.Ok = false;
            outcome.Error = ex.Message;
        }

        stopwatch.Stop();
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }

    private async Task<SourceOutcome> SearchOcsAsync(SearchRequest request, AppConfig cfg, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = "ocs",
            SourceName = "在线题库",
            Kind = SourceKind.Ocs,
        };

        try
        {
            var wrappers = cfg.OcsSources.Where(w => w.Enabled).ToList();
            if (request.OnlySourceIds.Count > 0)
                wrappers = wrappers.Where(w => request.OnlySourceIds.Contains(w.Id)).ToList();

            if (wrappers.Count == 0)
            {
                outcome.Notices.Add("没有启用的在线题库");
                outcome.Ok = true;
                return outcome;
            }

            var env = new AnswerWrapperEnv
            {
                Title = request.Title,
                Options = request.Options,
                Type = request.Type.ToOcs(),
            };

            var results = await _ocs.HandleAsync(wrappers, env, ct).ConfigureAwait(false);
            foreach (var info in results)
            {
                if (info.Error is not null) outcome.Notices.Add($"{info.Name}：{info.Error}");
                foreach (var warning in info.Warnings) outcome.Notices.Add($"{info.Name}：{warning}");

                foreach (var r in info.Results)
                {
                    var answer = AnswerSplitter.NormalizeChoiceAnswer(r.Answer ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(answer)) continue;

                    outcome.Hits.Add(new SearchHit
                    {
                        Question = string.IsNullOrWhiteSpace(r.Question) ? string.Empty : r.Question!,
                        Answer = answer,
                        Score = OcsAnswerScorer.Score(request, r.Question),
                        Note = string.IsNullOrWhiteSpace(info.Name) ? "在线题库" : info.Name,
                    });
                }

                if (info.Ok && info.Results.Count == 0 && info.Error is null)
                    outcome.Notices.Add($"{info.Name}：未命中");
            }

            outcome.Ok = results.Any(r => r.Ok) || results.Count == 0;
            if (!outcome.Ok && results.Count > 0)
                outcome.Error = results.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        }
        catch (OperationCanceledException)
        {
            outcome.Ok = false;
            outcome.Error = "已取消";
        }
        catch (OcsConfigException ex)
        {
            outcome.Ok = false;
            outcome.Error = "题库配置有问题：" + ex.Message;
        }
        catch (Exception ex)
        {
            outcome.Ok = false;
            outcome.Error = ex.Message;
        }

        stopwatch.Stop();
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }

    private async Task<SourceOutcome> SearchAiAsync(SearchRequest request, AppConfig cfg, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = "ai",
            SourceName = "AI 模型",
            Kind = SourceKind.Ai,
        };

        try
        {
            var service = new AiAnswerService(_ai);
            var providers = AiAnswerService.SelectProviders(cfg.AiProviders);
            if (request.OnlySourceIds.Count > 0)
                providers = providers.Where(p => request.OnlySourceIds.Contains(p.Id)).ToList();

            if (providers.Count == 0)
            {
                outcome.Notices.Add("没有启用的 AI 服务");
                outcome.Ok = true;
                return outcome;
            }

            var subs = await Task.WhenAll(providers
                .Select(p => service.SearchAsync(p, request, ct))
                .ToList()).ConfigureAwait(false);

            foreach (var sub in subs)
            {
                outcome.Hits.AddRange(sub.Hits);
                if (!sub.Ok && !string.IsNullOrWhiteSpace(sub.Error))
                    outcome.Notices.Add($"{sub.SourceName}：{sub.Error}");
            }

            outcome.Ok = subs.Any(s => s.Ok);
            if (!outcome.Ok)
                outcome.Error = subs.Select(s => s.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        }
        catch (OperationCanceledException)
        {
            outcome.Ok = false;
            outcome.Error = "已取消";
        }
        catch (Exception ex)
        {
            outcome.Ok = false;
            outcome.Error = ex.Message;
        }

        stopwatch.Stop();
        outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return outcome;
    }

    /// <summary>汇总所有来源的候选，按相似度/置信度排序去重。</summary>
    public static List<SearchHit> Rank(IEnumerable<SourceOutcome> outcomes, SearchRequest request, AppConfig cfg)
    {
        var minScore = Math.Clamp(cfg.Search.MinDisplayScore, 0, 1);
        var all = new List<SearchHit>();

        foreach (var outcome in outcomes)
        {
            if (!outcome.Ok && outcome.Hits.Count == 0) continue;
            all.AddRange(outcome.Hits);
        }

        var filtered = all
            .Where(h => !string.IsNullOrWhiteSpace(h.Answer))
            .ToList();

        var result = new List<SearchHit>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in filtered.OrderByDescending(h => h.Score))
        {
            var key = NormalizeKey(hit.Answer);
            if (seen.TryGetValue(key, out var index))
            {
                // 同答案保留分高的那条，但把来源信息合并
                var existing = result[index];
                if (existing.Note is not null && hit.Note is not null && !existing.Note.Contains(hit.Note, StringComparison.Ordinal))
                    existing.Note += " | " + hit.Note;
                continue;
            }
            if (hit.Score < minScore && request.Type != QuestionType.Unknown) continue;
            seen[key] = result.Count;
            result.Add(hit);
        }

        return result.Take(Math.Max(request.Limit, 3)).ToList();
    }

    private static string NormalizeKey(string answer)
    {
        var text = (answer ?? string.Empty).Trim().ToUpperInvariant();
        return new string(text.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '，' && c != '、').ToArray());
    }

    private async Task AutoCacheAsync(SearchResponse response, AppConfig cfg, CancellationToken ct)
    {
        if (!cfg.LocalBank.AutoCache) return;
        var kinds = cfg.LocalBank.AutoCacheKinds;
        if (kinds.Count == 0) return;

        var threshold = Math.Clamp(cfg.Search.AutoCacheMinScore, 0.5, 1);
        var aiThreshold = Math.Clamp(cfg.Search.AutoCacheMinAiConfidence, 0.5, 1);

        var toSave = new List<Question>();
        foreach (var outcome in response.Outcomes)
        {
            if (!outcome.Ok) continue;
            var kindKey = outcome.Kind switch
            {
                SourceKind.Ai => "ai",
                SourceKind.LanShare => "lan",
                SourceKind.Ocs => "ocs",
                _ => outcome.Kind.ToString().ToLowerInvariant(),
            };
            if (!kinds.Contains(kindKey, StringComparer.OrdinalIgnoreCase)) continue;

            // AI 结果按置信度判定，其它按相似度
            var need = outcome.Kind == SourceKind.Ai ? aiThreshold : threshold;
            var best = outcome.Hits.Where(h => !string.IsNullOrWhiteSpace(h.Answer))
                .OrderByDescending(h => h.Score).FirstOrDefault();
            if (best is null || best.Score < need) continue;

            toSave.Add(new Question
            {
                Title = response.Title,
                Type = response.Type,
                Options = response.Options,
                Answer = best.Answer,
                Source = outcome.Kind.ToChinese() + " · " + outcome.SourceName,
                Tags = "自动缓存",
            });
        }

        if (toSave.Count == 0) return;

        try
        {
            await Task.Run(() => _bank.ImportMany(toSave), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 自动缓存失败不影响搜题结果
        }
    }

    private async Task RecordAsync(SearchRequest request, SearchResponse response, AppConfig cfg, CancellationToken ct)
    {
        if (!request.RecordHistory || !cfg.Search.RecordHistory) return;

        try
        {
            var best = response.Best;
            await Task.Run(() => _history.Add(new HistoryRecord
            {
                Title = request.Title,
                Options = request.Options,
                Type = request.Type,
                Answer = best?.Answer ?? string.Empty,
                SourceName = best is null ? string.Empty : (response.Outcomes.FirstOrDefault(o => o.Hits.Contains(best))?.SourceName ?? string.Empty),
                SourceKind = response.FromCache ? "cache" : (best is null ? "none" : "search"),
                Score = best?.Score ?? 0,
                ElapsedMs = response.ElapsedMs,
                Success = best is not null,
                Error = response.Outcomes.Select(o => o.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
            }), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 历史记录失败不影响搜题
        }
    }

    /// <summary>把当前配置的所有来源铺平成一个列表（界面「来源」面板用）。</summary>
    public List<SourceDescriptor> DescribeSources() => Config.DescribeSources().ToList();
}

/// <summary>在线题库返回的答案打分：题干相似度为主，题型吻合加分。</summary>
public static class OcsAnswerScorer
{
    public static double Score(SearchRequest request, string? returnedQuestion)
    {
        if (string.IsNullOrWhiteSpace(returnedQuestion)) return 0.8; // 题库没回题干，只能信它

        var score = SimilarityScorer.Score(request.Title, returnedQuestion!);
        if (request.Type != QuestionType.Unknown)
        {
            var inferred = QuestionTypeInference.Infer(returnedQuestion!, request.Options);
            if (inferred == request.Type) score = Math.Min(1.0, score + 0.05);
        }
        return Math.Clamp(score, 0, 1);
    }
}
