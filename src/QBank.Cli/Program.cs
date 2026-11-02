using System.Diagnostics;
using System.Text;
using QBank.Core;
using QBank.Core.Ai;
using QBank.Core.Bridge;
using QBank.Core.Lan;
using QBank.Core.Models;
using QBank.Core.Ocr;
using QBank.Core.Ocs;
using QBank.Core.Search;
using QBank.Core.Storage;

namespace QBank.Cli;

/// <summary>
/// 离线自测：用本机 MockServer 模拟 OCS 题库与 OpenAI 兼容接口，
/// 把 Core 里每条链路都真的跑一遍（不联网、不花 token）。
/// 用法：QBank.Cli [--verbose] [--only 名字片段]
/// 退出码 0 = 全部通过，1 = 有失败。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();
    private static bool _verbose;
    private static string? _only;

    private static async Task<int> Main(string[] args)
    {
        _verbose = args.Contains("--verbose");
        var onlyIndex = Array.IndexOf(args, "--only");
        if (onlyIndex >= 0 && onlyIndex + 1 < args.Length) _only = args[onlyIndex + 1];

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("QBank 自测开始\n");

        // 沙箱环境下 %TEMP% 可能不可写，统一放在程序目录下的 .selftest 里
        var workDir = Path.Combine(AppContext.BaseDirectory, ".selftest", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"临时目录：{workDir}\n");

        try
        {
            await RunAsync("相似度打分", TestSimilarityAsync);
            await RunAsync("题型推断", TestQuestionTypeInferenceAsync);
            await RunAsync("答案切分", TestAnswerSplitterAsync);
            await RunAsync("OCS 配置校验", TestWrapperValidationAsync);
            await RunAsync("JS handler 执行", TestJsRunnerAsync);
            await RunAsync("OCS 题库 GET 请求", () => TestOcsGetAsync(workDir));
            await RunAsync("OCS 题库 POST 请求", () => TestOcsPostAsync(workDir));
            await RunAsync("AI 答题", () => TestAiAnswerAsync(workDir));
            await RunAsync("AI 模型列表", () => TestModelCatalogAsync(workDir));
            await RunAsync("网络出口（死代理绕行）", TestNetworkConfigAsync);
            await RunAsync("本地题库增删查", () => TestLocalBankAsync(workDir));
            await RunAsync("题库导入导出", () => TestImportExportAsync(workDir));
            await RunAsync("历史记录", () => TestHistoryAsync(workDir));
            await RunAsync("局域网服务端", () => TestLanServerAsync(workDir));
            await RunAsync("OCS 对接口", () => TestOcsBridgeAsync(workDir));
            await RunAsync("OCR 题干切分", TestOcrParseAsync);
            if (args.Contains("--ocr")) await RunAsync("OCR 实机识别（Umi-OCR）", TestRealOcrAsync);
            await RunAsync("搜题总编排", () => TestSearchServiceAsync(workDir));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        if (Failures.Count > 0)
        {
            Console.WriteLine("\n失败明细：");
            foreach (var f in Failures) Console.WriteLine("  - " + f);
        }
        return _failed == 0 ? 0 : 1;
    }

    // ---------------- 测试框架 ----------------

    private static async Task RunAsync(string name, Func<Task> body)
    {
        if (_only is not null && !name.Contains(_only, StringComparison.OrdinalIgnoreCase)) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await body();
            sw.Stop();
            _passed++;
            Console.WriteLine($"  ok   {name}  ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _failed++;
            Failures.Add($"{name}：{ex.Message}");
            Console.WriteLine($"  FAIL {name}  ({sw.ElapsedMilliseconds} ms)");
            if (_verbose) Console.WriteLine(ex);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        if (_verbose) Console.WriteLine("       · " + message);
    }

    private static void CheckEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}：期望 [{expected}]，实际 [{actual}]");
        if (_verbose) Console.WriteLine($"       · {what} = {actual}");
    }

    private static AppConfig TempConfig(string workDir)
    {
        var cfg = AppConfig.CreateDefault();
        cfg.DataDir = workDir;
        cfg.Search.UseAi = false;
        cfg.Search.UseOcs = false;
        cfg.Search.UseLanShare = false;
        cfg.LanShare.Enabled = false;
        return cfg;
    }

    private static ConfigStore StoreWith(string workDir, AppConfig cfg)
    {
        var store = new ConfigStore(workDir);
        store.Config.OcsSources.Clear();
        store.Config.AiProviders.Clear();
        store.Config.Search.UseAi = cfg.Search.UseAi;
        store.Config.Search.UseOcs = cfg.Search.UseOcs;
        store.Config.Search.UseLanShare = cfg.Search.UseLanShare;
        store.Config.Search.UseLocalBank = cfg.Search.UseLocalBank;
        store.Config.Search.UseCache = cfg.Search.UseCache;
        store.Config.Search.CacheMinScore = cfg.Search.CacheMinScore;
        store.Config.Search.AutoCacheMinScore = cfg.Search.AutoCacheMinScore;
        store.Config.Search.MinDisplayScore = cfg.Search.MinDisplayScore;
        store.Config.LanShare = cfg.LanShare;
        store.Config.LocalBank = cfg.LocalBank;
        return store;
    }

    // ---------------- 各项自测 ----------------

    private static Task TestSimilarityAsync()
    {
        var a = SimilarityScorer.Score("中国的首都是哪座城市", "中国的首都是哪座城市");
        CheckEqual(1.0, Math.Round(a, 4), "完全相同的相似度");

        var b = SimilarityScorer.Score("中国的首都是哪座城市", "中国的首都是哪座城市？");
        Check(b > 0.85, $"标点差异后仍应高分，实际 {b:F3}");

        var c = SimilarityScorer.Score("中国的首都是哪座城市", "下列哪个是质数");
        Check(c < 0.4, $"不相关题目应低分，实际 {c:F3}");

        var d = SimilarityScorer.Score("马克思主义哲学的核心", "马克思主义哲学的核心是什么");
        Check(d >= 0.6, $"一方包含另一方应 >= 0.6，实际 {d:F3}");
        return Task.CompletedTask;
    }

    private static Task TestQuestionTypeInferenceAsync()
    {
        CheckEqual(QuestionType.Single, QuestionTypeInference.Infer("中国的首都是哪座城市?", "A. 上海\nB. 北京\nC. 广州"), "三选项 → 单选");
        CheckEqual(QuestionType.Judgement, QuestionTypeInference.Infer("地球是圆的", "A. 对\nB. 错"), "对错 → 判断");
        CheckEqual(QuestionType.Multiple, QuestionTypeInference.Infer("以下哪些是质数（多选）", "A. 2\nB. 3\nC. 4"), "多选关键词");
        CheckEqual(QuestionType.Completion, QuestionTypeInference.Infer("请填空：中国的首都是____", ""), "填空关键词");
        CheckEqual("判断题", QuestionType.Judgement.ToChinese(), "中文题型名");
        CheckEqual("multiple", QuestionType.Multiple.ToOcs(), "OCS 题型名");
        CheckEqual(QuestionType.Multiple, QuestionTypeExtensions.ParseOcs("多选题"), "解析中文题型");
        return Task.CompletedTask;
    }

    private static Task TestAnswerSplitterAsync()
    {
        var parts = AnswerSplitter.Split("A#B#C");
        CheckEqual(3, parts.Count, "按 # 切分");
        CheckEqual("A", parts[0], "第一段");

        Check(AnswerSplitter.IsPlainAnswer("ABD"), "ABD 应是纯字母答案");
        Check(!AnswerSplitter.IsPlainAnswer("ABAB"), "ABAB 有重复字母，不算");
        CheckEqual("ABD", AnswerSplitter.NormalizeChoiceAnswer("a,b d"), "字母答案归一化");
        CheckEqual("AC", AnswerSplitter.NormalizeChoiceAnswer("a#c"), "小写多选归一化");

        var code = "for(int i=0;i<10;i++){\n  sum += i;\n}";
        Check(AnswerSplitter.IsProgramAnswer(code), "带分号和花括号的应识别为代码题");

        var codeParts = AnswerSplitter.Split(code);
        CheckEqual(1, codeParts.Count, "代码题不应被分号切开");
        return Task.CompletedTask;
    }

    private static Task TestWrapperValidationAsync()
    {
        ExpectConfigError("[]", "题库为空！");
        ExpectConfigError("[{\"name\":\"x\",\"url\":\"http://a\",\"handler\":\"\"}]", "解析器(handler) 为空");
        ExpectConfigError("[{\"name\":\"\",\"url\":\"http://a\",\"handler\":\"x\"}]", "名字(name) 为空");
        ExpectConfigError("[{\"name\":\"x\",\"url\":\"\",\"handler\":\"x\"}]", "接口地址(url) 为空");
        ExpectConfigError("[{\"name\":\"x\",\"url\":\"http://a\",\"handler\":\"h\",\"method\":\"put\"}]", "method 必须为以下选项中的一个");
        ExpectConfigError("[{\"name\":\"x\",\"url\":\"http://a\",\"handler\":\"h\",\"contentType\":\"xml\"}]", "contentType 必须为以下选项中的一个");
        ExpectConfigError("[{\"name\":\"x\",\"url\":\"http://a\",\"handler\":\"h\",\"type\":\"xhr\"}]", "type 必须为以下选项中的一个");

        var ok = AnswerWrapperParser.FromJsonString(
            "[{\"name\":\"T\",\"url\":\"http://a\",\"handler\":\"return (res)=>[res,undefined]\"}]");
        CheckEqual(1, ok.Count, "合法配置解析条数");
        CheckEqual("get", ok[0].Method, "默认 method");
        CheckEqual("json", ok[0].ContentType, "默认 contentType");
        CheckEqual("fetch", ok[0].Type, "默认 type");
        CheckEqual("#", ok[0].Homepage ?? "#", "homepage 缺省 #");

        var exported = AnswererWrapper.ToOcsJson(ok);
        Check(!exported.Contains("_enabled"), "导出的 JSON 不该包含私有字段 _enabled");
        Check(exported.Contains("\"url\""), "导出的 JSON 应含 url");
        return Task.CompletedTask;
    }

    private static void ExpectConfigError(string json, string expectedFragment)
    {
        try
        {
            AnswerWrapperParser.FromJsonString(json);
            throw new InvalidOperationException($"配置 [{json}] 本该报错但没有");
        }
        catch (OcsConfigException ex)
        {
            Check(ex.Message.Contains(expectedFragment, StringComparison.Ordinal),
                $"错误信息应包含「{expectedFragment}」，实际「{ex.Message}」");
        }
    }

    private static Task TestJsRunnerAsync()
    {
        var jsonResult = JsScriptRunner.RunWithJsonArg(
            "return (res)=> [res.data.list.map(x=>x.q), undefined]",
            "{\"data\":{\"list\":[{\"q\":\"题目一\"},{\"q\":\"题目二\"}]}}");
        Check(jsonResult.Ok, "JS 执行应成功：" + jsonResult.Error);
        Check(jsonResult.Json is not null, "应返回 JSON 结果");
        Check(jsonResult.Json!.Contains("题目一"), "结果应包含解析出的题目");

        var undefinedResult = JsScriptRunner.RunWithJsonArg("return (res)=> undefined", "{}");
        Check(undefinedResult.Ok, "返回 undefined 也不算失败");
        Check(undefinedResult.IsUndefined, "应标记为 undefined");

        var errorResult = JsScriptRunner.RunWithJsonArg("return (res)=> { throw new Error('boom') }", "{}");
        Check(!errorResult.Ok, "抛异常应该被捕获");

        // localStorage 垫片：同一个 storage 字典在多次调用之间共享（OCS 里就是同一个页面的 localStorage）
        var storage = new Dictionary<string, string>();
        var write = JsScriptRunner.RunWithJsonArg(
            "return (res)=> { localStorage.setItem('k','v1'); return [1,2] }", "{}", storage);
        Check(write.Ok, "写 localStorage 应成功：" + write.Error);
        Check(write.Storage.TryGetValue("k", out var written) && written == "v1", "写回后 Storage 里应有 k=v1");

        var readBack = JsScriptRunner.RunWithJsonArg(
            "return (res)=> [localStorage.getItem('k'), undefined]", "{}", write.Storage);
        Check(readBack.Json?.Contains("v1") == true,
            "localStorage 应能跨调用读到值，实际 " + readBack.Json);
        return Task.CompletedTask;
    }

    private static async Task TestOcsGetAsync(string workDir)
    {
        using var mock = new MockServer();
        mock.Map("/search", (query, body) =>
        {
            var title = MockServer.QueryParam(query, "title") ?? string.Empty;
            var payload = $"{{\"code\":1,\"data\":[{{\"q\":\"{title}\",\"a\":\"B\"}}]}}";
            return (200, "application/json", payload);
        });

        const string handler = """
            return (res)=> {
                const list = res.data || [];
                if (!list.length) return undefined;
                return list.map(item => [item.q, item.a]);
            }
            """;

        var wrapper = new AnswererWrapper
        {
            Name = "测试题库",
            Url = mock.BaseUrl + "/search?title=${title}&type=${type}",
            Method = "get",
            ContentType = "json",
            Handler = handler,
        };

        var executor = new AnswerWrapperHandler();
        var env = new AnswerWrapperEnv { Title = "中国的首都是哪座城市", Type = "single" };
        var infos = await executor.HandleAsync(new[] { wrapper }, env);

        CheckEqual(1, infos.Count, "题库返回条数");
        Check(infos[0].Ok, "GET 请求应成功：" + infos[0].Error);
        CheckEqual(1, infos[0].Results.Count, "解析出的答案条数");
        CheckEqual("B", infos[0].Results[0].Answer, "答案");
        Check(infos[0].Results[0].Question!.Contains("中国的首都"), "题干应回传");

        var sent = mock.Requests.FirstOrDefault(r => r.Path == "/search");
        Check(sent.Path == "/search", "应该真的发到 /search");
        Check(sent.Query.Contains("title="), "查询串应带 title");
        return;
    }

    private static async Task TestOcsPostAsync(string workDir)
    {
        using var mock = new MockServer();
        mock.Map("/api/answer", (query, body) =>
        {
            // 回显收到的 JSON，用来断言 body 结构
            return (200, "application/json",
                "{\"ok\":true,\"echo\":" + (string.IsNullOrWhiteSpace(body) ? "null" : body) + "}");
        });

        var wrapper = new AnswererWrapper
        {
            Name = "POST 题库",
            Url = mock.BaseUrl + "/api/answer",
            Method = "post",
            ContentType = "json",
            Headers = { ["X-Test"] = "1" },
            Data = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["question"] = System.Text.Json.JsonDocument.Parse("\"${title}\"").RootElement.Clone(),
                ["kind"] = System.Text.Json.JsonDocument.Parse("\"${type}\"").RootElement.Clone(),
            },
            Handler = "return (res)=> res.echo ? [[res.echo.question, 'A']] : undefined",
        };

        var executor = new AnswerWrapperHandler();
        var env = new AnswerWrapperEnv { Title = "地球是圆的", Type = "judgement" };
        var infos = await executor.HandleAsync(new[] { wrapper }, env);

        Check(infos[0].Ok, "POST 请求应成功：" + infos[0].Error);
        CheckEqual(1, infos[0].Results.Count, "POST 解析答案条数");
        CheckEqual("A", infos[0].Results[0].Answer, "POST 答案");

        var sent = mock.Requests.First(r => r.Path == "/api/answer");
        Check(sent.Body.Contains("地球是圆的"), "请求体里应替换掉 ${title} 占位符");
        Check(sent.Body.Contains("judgement"), "请求体里应替换掉 ${type} 占位符");
        return;
    }

    private static async Task TestAiAnswerAsync(string workDir)
    {
        using var mock = new MockServer();
        mock.Map("/v1/chat/completions", (query, body) =>
        {
            const string content =
                "{\\\"answer\\\":\\\"B\\\",\\\"question\\\":\\\"中国的首都是哪座城市\\\",\\\"confidence\\\":0.95,\\\"explain\\\":\\\"北京是首都\\\"}";
            var payload = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]," +
                          "\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30}}";
            return (200, "application/json", payload);
        });

        var provider = new AiProviderConfig
        {
            Id = "mock-ai",
            Name = "Mock AI",
            ProviderKind = "custom",
            BaseUrl = mock.BaseUrl + "/v1",
            ApiKey = "sk-test",
            Model = "mock-chat",
            Enabled = true,
            UseJsonMode = true,
        };

        var service = new AiAnswerService(new AiChatClient());
        var req = new SearchRequest
        {
            Title = "中国的首都是哪座城市?",
            Options = "A. 上海\nB. 北京\nC. 广州",
            Type = QuestionType.Single,
        };

        var outcome = await service.SearchAsync(provider, req);
        Check(outcome.Ok, "AI 答题应成功：" + outcome.Error);
        CheckEqual(1, outcome.Hits.Count, "AI 命中条数");
        CheckEqual("B", outcome.Hits[0].Answer, "AI 答案");
        Check(outcome.Hits[0].Score > 0.9, $"AI 置信度应 > 0.9，实际 {outcome.Hits[0].Score}");

        var test = await service.TestAsync(provider);
        Check(test.Ok, "连接测试应成功：" + test.Error);

        // json mode 被 400 拒绝后应自动去掉 response_format 重试
        using var strictMock = new MockServer();
        var calls = 0;
        strictMock.Map("/v1/chat/completions", (query, body) =>
        {
            calls++;
            if (body.Contains("response_format"))
                return (400, "application/json", "{\"error\":\"response_format not supported\"}");
            return (200, "application/json",
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"A\\\",\\\"confidence\\\":0.6}\"}}]}");
        });

        var fallbackProvider = new AiProviderConfig
        {
            BaseUrl = strictMock.BaseUrl + "/v1",
            ApiKey = "sk-test",
            Model = "mock-chat",
            Enabled = true,
            UseJsonMode = true,
        };
        var fallbackOutcome = await service.SearchAsync(fallbackProvider, req);
        CheckEqual(2, calls, "json mode 被拒后应重试一次（共两次请求）");
        Check(fallbackOutcome.Ok, "去掉 response_format 后应成功：" + fallbackOutcome.Error);
        CheckEqual("A", fallbackOutcome.Hits[0].Answer, "重试后的答案");
    }

    private static async Task TestModelCatalogAsync(string workDir)
    {
        using var mock = new MockServer();
        mock.Map("/v1/models", (query, body) =>
            (200, "application/json",
                "{\"data\":[{\"id\":\"deepseek-chat\",\"owned_by\":\"deepseek\"}," +
                "{\"id\":\"qwen2.5-vl-7b\",\"owned_by\":\"alibaba\"}]}"));

        var catalog = new AiModelCatalog();
        var provider = new AiProviderConfig
        {
            ProviderKind = "deepseek",
            BaseUrl = mock.BaseUrl + "/v1",
            ApiKey = "sk-test",
            Model = "deepseek-chat",
        };

        var result = await catalog.FetchAsync(provider);
        Check(result.Ok, "拉取模型列表应成功：" + result.Error);
        CheckEqual(2, result.Models.Count, "模型数量");
        Check(result.Models.Any(m => m.Id == "deepseek-chat"), "应包含 deepseek-chat");
        Check(result.Models.Any(m => m.VisionCapable), "qwen2.5-vl 应被标记为支持图片");

        // Ollama 原生 /api/tags
        using var ollamaMock = new MockServer();
        ollamaMock.Map("/api/tags", (query, body) =>
            (200, "application/json",
                "{\"models\":[{\"name\":\"qwen3:8b\",\"size\":4931000000," +
                "\"details\":{\"parameter_size\":\"8.2B\",\"quantization_level\":\"Q4_K_M\"}}]}"));

        var ollamaProvider = new AiProviderConfig
        {
            ProviderKind = "ollama",
            BaseUrl = ollamaMock.BaseUrl + "/v1",
            Model = "qwen3:8b",
        };
        var ollamaResult = await catalog.FetchAsync(ollamaProvider);
        Check(ollamaResult.Ok, "Ollama 模型列表应成功：" + ollamaResult.Error);
        CheckEqual(1, ollamaResult.Models.Count, "Ollama 模型数量");
        CheckEqual("8.2B", ollamaResult.Models[0].ParameterSize, "参数量");
    }

    private static async Task TestNetworkConfigAsync()
    {
        // 1) 三种模式构建出来的 proxy 对象
        var direct = new NetworkConfig { ProxyMode = "direct" };
        Check(direct.BuildProxy() is null, "直连模式下不应该有代理对象");
        CheckEqual("直连（忽略系统代理）", direct.Describe(), "直连模式的描述");

        var custom = new NetworkConfig { ProxyMode = "custom", ProxyUrl = "http://127.0.0.1:7890" };
        var proxy = custom.BuildProxy() as System.Net.WebProxy;
        Check(proxy is not null, "自定义模式应构建出 WebProxy");
        Check(proxy!.Address?.ToString() == "http://127.0.0.1:7890/", "代理地址应正确，实际 " + proxy.Address);
        Check(proxy.BypassList.Length > 0, "应该带上免代理列表");
        Check(custom.Describe().Contains("7890"), "描述里应带代理地址");

        var emptyCustom = new NetworkConfig { ProxyMode = "custom", ProxyUrl = "" };
        Check(emptyCustom.BuildProxy() is null, "自定义模式没填地址应退回直连");

        // 2) 端口探活：先起一个监听，再关掉
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var livePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Check(HttpClients.IsPortListening("127.0.0.1", livePort), "活着的端口应被判为在监听");
        listener.Stop();
        Check(!HttpClients.IsPortListening("127.0.0.1", livePort), "关掉的端口应被判为没人听");

        // 3) 代理设置真的会生效：把代理指到一个死端口，请求应该连不上
        using var mock = new MockServer();
        mock.Map("/v1/models", (query, body) => (200, "application/json", "{\"data\":[{\"id\":\"m1\"}]}"));
        var deadProxy = new NetworkConfig
        {
            ProxyMode = "custom",
            ProxyUrl = "http://127.0.0.1:" + livePort,   // 刚刚关掉的那个端口
            FallbackToDirectOnProxyFailure = false,
        };

        var viaDeadProxy = await ProbeAsync(HttpClients.Create(deadProxy), mock.BaseUrl + "/v1/models");
        Check(!viaDeadProxy, "走死代理应该请求失败");

        var viaDirect = await ProbeAsync(HttpClients.Create(direct), mock.BaseUrl + "/v1/models");
        Check(viaDirect, "直连应该请求成功");

        // 4) auto 模式下，系统代理若是死的就要自动改走直连
        var auto = new NetworkConfig { ProxyMode = "auto", FallbackToDirectOnProxyFailure = true };
        var deadDetected = HttpClients.DescribeSystemProxyState(out var sysDesc, out var sysDead);
        if (deadDetected && sysDead)
        {
            var client = HttpClients.Create(auto);
            var ok = await ProbeAsync(client, mock.BaseUrl + "/v1/models");
            Check(ok, "系统代理是死的（" + sysDesc + "）时应自动绕过");
            Check(HttpClients.AutoFallbackNote is not null, "应记录自动绕行的说明");
            Console.WriteLine($"      系统代理 {sysDesc} 无人监听 → 已自动直连");
        }
        else
        {
            Console.WriteLine("      系统代理正常或未设置，跳过自动绕行断言");
        }

        // 5) 错误信息应该提到「网络出口」
        var explained = HttpClients.ExplainNetworkError(
            new HttpRequestException("由于目标计算机积极拒绝，无法连接。 (127.0.0.1:12000)"));
        Check(explained.Contains("网络出口"), "死代理的错误提示应指引到「网络出口」设置，实际：" + explained);
    }

    private static async Task<bool> ProbeAsync(HttpClient client, string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(url, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static Task TestLocalBankAsync(string workDir)
    {
        var dbPath = Path.Combine(workDir, "bank.db");
        var bank = new LocalBank(dbPath);

        var q = new Question
        {
            Title = "中国的首都是哪座城市？",
            Options = "A. 上海\nB. 北京\nC. 广州",
            Answer = "B",
            Type = QuestionType.Single,
            Source = "自测",
        };
        var id = bank.Upsert(q);
        Check(id > 0, "写入应返回自增 id");

        var all = bank.Query(null, null, 0, 100);
        CheckEqual(1, all.Count, "写入后应有 1 条");

        // 同指纹再写一次应更新而不是新增
        var same = new Question
        {
            Title = "中国的首都是哪座城市？",
            Answer = "B",
            Type = QuestionType.Single,
        };
        bank.Upsert(same);
        CheckEqual(1, bank.Count(), "重复写入不应新增记录");

        var hits = bank.Search("中国的首都是哪座城市", QuestionType.Unknown, 5, 0.5);
        CheckEqual(1, hits.Count, "本地搜索应命中");
        Check(hits[0].Score > 0.9, $"相似度应 > 0.9，实际 {hits[0].Score}");

        var stats = bank.Stats();
        CheckEqual(1, stats.Total, "统计总数");
        Check(stats.TotalHits >= 1, $"累计命中应 >= 1，实际 {stats.TotalHits}");

        var fetched = bank.GetById(id);
        Check(fetched is not null, "按 id 读回");
        CheckEqual("中国的首都是哪座城市？", fetched!.Title, "读回的题干");

        Check(bank.Delete(id), "删除应成功");
        CheckEqual(0, bank.Count(), "删除后应为 0 条");
        return Task.CompletedTask;
    }

    private static Task TestImportExportAsync(string workDir)
    {
        var bank = new LocalBank(Path.Combine(workDir, "import.db"));

        var json = "[{\"title\":\"题目甲\",\"answer\":\"A\"},{\"title\":\"题目乙\",\"answer\":\"B\"}]";
        var r1 = bank.Import(json);
        CheckEqual(2, r1.Added, "JSON 导入条数");

        var csv = "title,answer\n题目丙,C\n题目丁,D\n";
        var r2 = bank.Import(csv);
        CheckEqual(2, r2.Added, "CSV 导入条数");

        var txt = "题目戊#E\n题目己#F\n";
        var r3 = bank.Import(txt);
        CheckEqual(2, r3.Added, "TXT 导入条数");

        CheckEqual(6, bank.Count(), "导入后总数");

        var exported = bank.ExportJson();
        Check(exported.Contains("题目甲"), "导出的 JSON 应含题目甲");

        var csvOut = bank.ExportCsv();
        Check(csvOut.Contains("题目丙"), "导出的 CSV 应含题目丙");

        var txtOut = bank.ExportTxt();
        Check(txtOut.Contains("题目戊#E") || txtOut.Contains("题目戊"), "导出的 TXT 应含题目戊");
        return Task.CompletedTask;
    }

    private static Task TestHistoryAsync(string workDir)
    {
        var history = new HistoryStore(Path.Combine(workDir, "history.db"));
        var id = history.Add(new HistoryRecord
        {
            Title = "测试题干",
            Answer = "A",
            SourceName = "本地题库",
            SourceKind = "local",
            Score = 0.9,
            ElapsedMs = 12,
            Success = true,
        });
        Check(id > 0, "写历史应返回 id");

        var list = history.Query(null, null, false, null, 0, 20);
        CheckEqual(1, list.Count, "历史条数");
        CheckEqual("测试题干", list[0].Title, "历史题干");

        var stats = history.Stats();
        CheckEqual(1, stats.Total, "历史统计总数");
        CheckEqual(1, stats.SuccessCount, "成功条数");

        history.MarkSaved(id, true);
        CheckEqual(1, history.Clear(null), "清空应删掉 1 条");
        CheckEqual(0, history.Stats().Total, "清空后统计为 0");
        return Task.CompletedTask;
    }

    private static async Task TestLanServerAsync(string workDir)
    {
        var bank = new LocalBank(Path.Combine(workDir, "lan.db"));
        bank.Upsert(new Question
        {
            Title = "局域网测试题目",
            Answer = "C",
            Type = QuestionType.Single,
        });

        using var share = new LanShareService(bank, () => "selftest");
        var cfg = AppConfig.CreateDefault();
        cfg.LanShare.Enabled = true;
        cfg.LanShare.AllowLan = false;       // 只绑 127.0.0.1，避免弹防火墙
        cfg.LanShare.RespondToDiscovery = false;
        cfg.LanShare.Token = "test-token";
        cfg.LanShare.Port = 0;               // 让服务端自己挑空闲端口

        share.ApplyConfig(cfg);
        Check(share.IsServerRunning, "局域网服务应已启动");
        var port = share.Server!.Port;
        Check(port > 0, $"应拿到实际端口，实际 {port}");

        var peer = new LanPeerConfig { Host = "127.0.0.1", Port = port, Token = "test-token", Enabled = true };
        using var client = new LanBankClient(peer);

        var (ok, message) = await client.TestAsync();
        Check(ok, "节点测试应成功：" + message);

        var info = await client.PingAsync();
        Check(info is not null, "ping 应返回信息");
        CheckEqual("QBank", info!.App, "ping 应用名");

        var response = await client.QueryAsync(new LanQueryRequest
        {
            Title = "局域网测试题目",
            Limit = 3,
        });
        Check(response.Ok, "局域网查询应成功：" + response.Error);
        CheckEqual(1, response.Hits.Count, "局域网命中条数");
        CheckEqual("C", response.Hits[0].Answer, "局域网答案");

        // 错误口令应被拒
        using var badClient = new LanBankClient(new LanPeerConfig
        {
            Host = "127.0.0.1", Port = port, Token = "wrong", Enabled = true,
        });
        var badResponse = await badClient.QueryAsync(new LanQueryRequest { Title = "局域网测试题目" });
        Check(!badResponse.Ok, "错误口令应被拒绝");

        share.StopServer();
        Check(!share.IsServerRunning, "停止后服务端应关闭");
    }

    /// <summary>
    /// OCS 对接口：起一个真的 HTTP 服务，按 OCS 的方式问它，
    /// 并且把生成的 OCS 配置里的 handler 拿去 JsScriptRunner 里真的跑一遍，
    /// 确认 OCS 那边能解析成 [[题目, 答案]] —— 这条链路是整个功能的命根子。
    /// </summary>
    private static async Task TestOcsBridgeAsync(string workDir)
    {
        var bank = new LocalBank(Path.Combine(workDir, "bridge.db"));
        bank.Upsert(new Question
        {
            Title = "中国的首都是哪座城市",
            Answer = "B",
            Type = QuestionType.Single,
            Options = "A. 上海\nB. 北京",
        });

        using var bridge = new OcsBridgeService();
        bridge.Apply(new OcsBridgeConfig
        {
            Port = 0,                 // 让 MiniHttpServer 自己挑空闲端口
            AllowLan = false,         // 只绑 127.0.0.1，避免弹防火墙
            Token = "bridge-token",
            UseLocalBank = true,
            UseLanShare = false,
            UseAi = false,
            UseOcs = false,
            MaxResults = 3,
            MinScore = 0.3,
            RecordHistory = false,
        }, (req, ct) =>
        {
            // 用本地题库冒充搜题引擎，避开网络
            var hits = bank.Search(req.Title, req.Type, req.Limit, 0.3, registerHit: false);
            var response = new SearchResponse
            {
                Title = req.Title,
                Type = req.Type,
                Options = req.Options,
                ElapsedMs = 1,
            };
            var outcome = new SourceOutcome
            {
                SourceId = "local",
                SourceName = "本地题库",
                Kind = SourceKind.LocalBank,
                Ok = true,
            };
            foreach (var hit in hits)
            {
                outcome.Hits.Add(new SearchHit
                {
                    Question = hit.Question.Title,
                    Answer = hit.Question.Answer,
                    Score = hit.Score,
                });
            }
            response.Outcomes.Add(outcome);
            response.Ranked.AddRange(outcome.Hits);
            return Task.FromResult(response);
        });

        Check(bridge.IsRunning, "OCS 对接口应已启动");
        var port = bridge.Port;
        Check(port > 0, $"应拿到实际端口，实际 {port}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // 1) ping
        var ping = await http.GetStringAsync($"http://127.0.0.1:{port}/qbank/ocs/ping");
        Check(ping.Contains("\"ok\": true") || ping.Contains("\"ok\":true"), "ping 应返回 ok");

        // 2) 搜索命中：OCS 约定的 [[null, 答案]] 形态
        var hitJson = await http.GetStringAsync(
            $"http://127.0.0.1:{port}/qbank/ocs/search?title={Uri.EscapeDataString("中国的首都是哪座城市")}&type=single&token=bridge-token");
        if (_verbose) Console.WriteLine("       · 命中响应：" + hitJson);
        Check(hitJson.Contains("\"B\""), "命中的响应里应含答案 B：" + hitJson);

        // 3) 口令不对应被拒
        var badToken = await http.GetStringAsync(
            $"http://127.0.0.1:{port}/qbank/ocs/search?title=test&token=wrong");
        Check(badToken.Contains("口令"), "错误口令应返回提示：" + badToken);

        // 4) 未命中给 [提示, null]
        var missJson = await http.GetStringAsync(
            $"http://127.0.0.1:{port}/qbank/ocs/search?title={Uri.EscapeDataString("完全不存在的题目")}&token=bridge-token");
        if (_verbose) Console.WriteLine("       · 未命中响应：" + missJson);
        Check(missJson.Contains("null"), "未命中应返回 [提示, null]：" + missJson);

        // 5) POST JSON 体
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var postBody = "{\"title\":" + System.Text.Json.JsonSerializer.Serialize("中国的首都是哪座城市") +
                       ",\"type\":\"single\",\"token\":\"bridge-token\"}";
        var postResponse = await client.PostAsync(
            $"http://127.0.0.1:{port}/qbank/ocs/search",
            new StringContent(postBody, Encoding.UTF8, "application/json"));
        var postJson = await postResponse.Content.ReadAsStringAsync();
        Check(postJson.Contains("\"B\""), "POST 也应能命中：" + postJson);

        // 6) 生成的 OCS 配置本身要能被 OCS 的解析器接受
        var configJson = bridge.BuildOcsConfig();
        var wrappers = AnswerWrapperParser.FromJsonString(configJson);
        CheckEqual(1, wrappers.Count, "生成的配置应解析出 1 个题库");
        CheckEqual("题库工坊", wrappers[0].Name, "题库名");
        Check(wrappers[0].Url.Contains("${title}"), "URL 应带 ${title} 占位符：" + wrappers[0].Url);
        Check(wrappers[0].Url.Contains("bridge-token"), "URL 应带口令");

        // 7) 命根子：把生成的 handler 丢进 JS 引擎，喂真实响应，看能否还原成 OCS 结果
        var handler = wrappers[0].Handler;
        var parsed = JsScriptRunner.RunWithTextArg(handler, hitJson);
        Check(parsed.Ok, "生成的 handler 应当执行成功：" + parsed.Error);
        Check(parsed.Json is not null, "handler 应返回结果");
        var arr = System.Text.Json.JsonDocument.Parse(parsed.Json!).RootElement;
        CheckEqual(System.Text.Json.JsonValueKind.Array, arr.ValueKind, "handler 结果应是数组");
        CheckEqual(1, arr.GetArrayLength(), "handler 应返回 1 条答案");
        CheckEqual("B", arr[0][1].GetString(), "handler 还原出的答案");

        // 8) 未命中时 handler 应转成提示消息（OCS 会显示成通知）
        var parsedMiss = JsScriptRunner.RunWithTextArg(handler, missJson);
        Check(parsedMiss.Ok, "未命中时 handler 也应正常执行：" + parsedMiss.Error);
        var missArr = System.Text.Json.JsonDocument.Parse(parsedMiss.Json!).RootElement;
        CheckEqual(System.Text.Json.JsonValueKind.Array, missArr.ValueKind, "未命中结果应是数组");
        CheckEqual(2, missArr.GetArrayLength(), "未命中应返回 [消息, undefined]，元素个数 2");
        Check(missArr[0].ValueKind == System.Text.Json.JsonValueKind.String, "未命中第一条应是提示文字");

        // 9) 配置链接接口返回纯 JSON 数组
        var linkBody = await http.GetStringAsync($"http://127.0.0.1:{port}/qbank/ocs/ocs.json");
        Check(linkBody.TrimStart().StartsWith("["), "配置链接应直接返回 JSON 数组");

        bridge.Stop();
        Check(!bridge.IsRunning, "停止后对接口应关闭");
    }

    private static Task TestOcrParseAsync()
    {
        const string raw = """
            1、中国的首都是哪座城市?
            A. 上海
            B. 北京
            C. 广州
            """;

        var parsed = OcrQuestionParser.Parse(raw);
        Check(parsed.Title.Contains("中国的首都"), "应识别出题干：" + parsed.Title);
        CheckEqual(3, parsed.OptionLines.Count, "选项行数");
        Check(parsed.Options.Contains("北京"), "选项里应含北京");
        CheckEqual(QuestionType.Single, parsed.Type, "题型推断");

        var withType = OcrQuestionParser.Parse("（多选）以下哪些是质数\nA. 2\nB. 3\nC. 4");
        CheckEqual(QuestionType.Multiple, withType.Type, "多选识别");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 实机 OCR：生成一张带中文题目的图片，真的调用本机 Umi-OCR 的 PaddleOCR-json 引擎识别。
    /// 只在传了 --ocr 时跑（依赖本机装了 Umi-OCR）。
    /// </summary>
    private static async Task TestRealOcrAsync()
    {
        var exe = UmiOcrEngine.DetectPaddleExe();
        if (string.IsNullOrWhiteSpace(exe))
        {
            Console.WriteLine("       （本机没找到 Umi-OCR 的 PaddleOCR-json，跳过）");
            return;
        }
        if (_verbose) Console.WriteLine("       · 引擎：" + exe);

        // 沙箱下 %TEMP% 可能写不了，落盘到程序目录
        var imagePath = Path.Combine(AppContext.BaseDirectory, $"qbank-ocr-{Guid.NewGuid():n}.png");
        // 画一张 "1、中国的首都是哪座城市?" + 三个选项的题图
        using (var bmp = new System.Drawing.Bitmap(900, 300))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new System.Drawing.Font("Microsoft YaHei", 26, System.Drawing.FontStyle.Bold);
            g.DrawString("1、中国的首都是哪座城市?", font, System.Drawing.Brushes.Black, 20, 20);
            g.DrawString("A. 上海    B. 北京    C. 广州", font, System.Drawing.Brushes.Black, 20, 110);
            bmp.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            // 引擎内部也要落一个临时 png，沙箱里 %TEMP% 不可写，显式指定程序目录
            var tempDir = Path.Combine(AppContext.BaseDirectory, ".selftest", "ocr");
            Directory.CreateDirectory(tempDir);
            var result = await UmiOcrEngine.RecognizeViaEngineAsync(
                bytes, exe, "models/config_chinese.txt", tempDir);

            Check(result.Ok, "Umi-OCR 识别应成功：" + result.Error);
            Check(result.Text.Length > 0, "识别结果不应为空");
            Check(result.Lines.Count >= 2, $"应识别出至少 2 行，实际 {result.Lines.Count}");
            Check(result.Text.Contains("首都"), "识别文本应包含「首都」，实际：" + result.Text.Replace('\n', ' '));

            var parsed = OcrQuestionParser.Parse(result.Text);
            Check(parsed.Title.Length > 0, "应能从识别文本切出题干");
            if (_verbose)
            {
                Console.WriteLine("       · 识别文本：");
                foreach (var line in result.Lines) Console.WriteLine("         " + line.Text);
            }
        }
        finally
        {
            try { File.Delete(imagePath); } catch (Exception) { }
        }
    }

    private static async Task TestSearchServiceAsync(string workDir)
    {
        var seedCfg = TempConfig(workDir);
        seedCfg.Search.UseLocalBank = true;
        seedCfg.Search.UseOcs = true;
        seedCfg.Search.UseAi = true;
        seedCfg.LocalBank.AutoCache = true;
        seedCfg.LocalBank.AutoCacheKinds = new List<string> { "ai", "lan", "ocs" };
        var store = StoreWith(workDir, seedCfg);

        var bank = new LocalBank(store.DatabaseFile);
        var history = new HistoryStore(store.DatabaseFile);

        using var mock = new MockServer();
        mock.Map("/search", (query, body) =>
        {
            var title = MockServer.QueryParam(query, "title") ?? string.Empty;
            return (200, "application/json",
                $"{{\"data\":[{{\"question\":\"{title}\",\"answer\":\"B\"}}]}}");
        });
        mock.Map("/v1/chat/completions", (query, body) =>
            (200, "application/json",
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"A\\\",\\\"confidence\\\":0.4}\"}}]}"));

        store.Config.OcsSources.Add(new AnswererWrapper
        {
            Name = "Mock 题库",
            Url = mock.BaseUrl + "/search?title=${title}",
            Method = "get",
            ContentType = "json",
            Handler = "return (res)=> (res.data||[]).map(x=>[x.question,x.answer])",
            Enabled = true,
        });

        store.Config.AiProviders.Add(new AiProviderConfig
        {
            Id = "mock-ai",
            Name = "Mock AI",
            ProviderKind = "custom",
            BaseUrl = mock.BaseUrl + "/v1",
            ApiKey = "sk-test",
            Model = "mock-chat",
            Enabled = true,
            UseJsonMode = true,
        });

        var service = new SearchService(store, bank, history);

        var response = await service.SearchAsync(new SearchRequest
        {
            Title = "中国的首都是哪座城市",
            Options = "A. 上海\nB. 北京",
            Type = QuestionType.Single,
            UseLocalBank = true,
            UseOcs = true,
            UseAi = true,
            UseLanShare = false,
        });

        Check(response.Outcomes.Count >= 2, $"应至少有本地与 OCS 两个来源，实际 {response.Outcomes.Count}");
        Check(response.Ranked.Count > 0, "汇总结果不应为空");
        Check(response.BestAnswer is "A" or "B", $"最佳答案应是 A 或 B，实际 {response.BestAnswer}");

        var ocsOutcome = response.Outcomes.FirstOrDefault(o => o.Kind == SourceKind.Ocs);
        Check(ocsOutcome is not null, "应有 OCS 来源的结果");
        Check(ocsOutcome!.Ok, "OCS 来源应成功：" + ocsOutcome.Error);

        var aiOutcome = response.Outcomes.FirstOrDefault(o => o.Kind == SourceKind.Ai);
        Check(aiOutcome is not null, "应有 AI 来源的结果");
        Check(aiOutcome!.Ok, "AI 来源应成功：" + aiOutcome.Error);

        // 第二次搜同一题应走缓存短路
        var cached = await service.SearchAsync(new SearchRequest
        {
            Title = "中国的首都是哪座城市",
            Options = "A. 上海\nB. 北京",
            Type = QuestionType.Single,
            UseLocalBank = true,
            UseOcs = true,
            UseAi = true,
        });
        Check(cached.FromCache, "同一题第二次应命中缓存");
        CheckEqual(1, cached.Outcomes.Count, "缓存短路只应有一个来源");

        // 历史应有记录
        Check(history.Stats().Total >= 2, $"历史记录应 >= 2 条，实际 {history.Stats().Total}");

        // 空题干应报输入错误
        var empty = await service.SearchAsync(new SearchRequest { Title = "  " });
        CheckEqual("input", empty.Outcomes[0].SourceId, "空题干应给输入错误");
    }
}
