using System.Diagnostics;
using System.Text.Json;
using QBank.Core.Models;

namespace QBank.Core.Ocr;

/// <summary>
/// OCR 总入口：按配置选用 Umi-OCR / Windows 内置 / Ollama 视觉模型 / 自定义 HTTP 接口，
/// 识别完成后还能顺手把整段文字拆成「题干 + 选项」。
/// </summary>
public sealed class OcrService
{
    private readonly Func<AppConfig> _configAccessor;

    public OcrService(Func<AppConfig> configAccessor)
    {
        _configAccessor = configAccessor;
    }

    public AppConfig Config => _configAccessor();

    public OcrEngineKind CurrentEngine => OcrEngineKindExtensions.ParseEngine(Config.Ocr?.Engine);

    /// <summary>识别一张图片，按当前配置选择引擎。</summary>
    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var ocr = Config.Ocr ?? new OcrConfig();
        var kind = OcrEngineKindExtensions.ParseEngine(ocr.Engine);

        return kind switch
        {
            OcrEngineKind.Umi => await RecognizeUmiAsync(ocr, imageBytes, ct),
            OcrEngineKind.Windows => await WindowsOcrEngine.RecognizeAsync(imageBytes, ocr.WindowsLanguage, ct),
            OcrEngineKind.Vision => await RecognizeVisionAsync(ocr, imageBytes, ct),
            OcrEngineKind.Ollama => await RecognizeOllamaAsync(ocr, imageBytes, ct),
            OcrEngineKind.Custom => await RecognizeCustomAsync(ocr.Custom, imageBytes, ct),
            _ => OcrResult.Fail("none", "还没有选择 OCR 引擎，请到「设置 → 图片识别」里挑一个"),
        };
    }

    /// <summary>Umi-OCR：先试 HTTP 服务，不通就退回直连 PaddleOCR-json.exe。</summary>
    private async Task<OcrResult> RecognizeUmiAsync(OcrConfig ocr, byte[] imageBytes, CancellationToken ct)
    {
        var umi = ocr.Umi ??= new UmiOcrConfig();

        // 用的是随程序内置的引擎，且用户没有另外指定 Umi-OCR —— 那就别去试它的 HTTP 服务了，
        // 反正也没开，白等一轮超时。直接调内置引擎。
        var usingBundled = UmiOcrEngine.HasBundledEngine
                           && string.IsNullOrWhiteSpace(umi.EngineExePath)
                           && string.IsNullOrWhiteSpace(umi.InstallDirectory);

        if (umi.UseHttp && !usingBundled)
        {
            var http = await UmiOcrEngine.RecognizeViaHttpAsync(imageBytes, umi.ApiBaseUrl, umi.Language, ct);
            if (http.Ok && http.Text.Length > 0) return http;

            // HTTP 不通（Umi-OCR 没开）时，如果本机有引擎文件就直接调引擎，避免让用户先去开软件
            if (!http.Ok && !string.IsNullOrWhiteSpace(http.Error) &&
                (http.Error.Contains("连不上") || http.Error.Contains("超时")))
            {
                var exe = ResolveExe(umi);
                if (exe is not null && File.Exists(exe))
                {
                    var viaEngine = await UmiOcrEngine.RecognizeViaEngineAsync(imageBytes, exe, umi.ConfigPath, null, ct);
                    viaEngine.Warnings.Insert(0, "HTTP 服务没开，已自动改用本地引擎直连");

                    if (viaEngine.Ok) return viaEngine;
                    viaEngine.Error = $"{http.Error}；本地引擎也失败：{viaEngine.Error}";
                    return viaEngine;
                }
            }

            return http;
        }

        var exePath = ResolveExe(umi);
        if (exePath is null)
            return OcrResult.Fail(UmiOcrEngine.EngineName,
                UmiOcrEngine.HasBundledEngine
                    ? "内置的 OCR 引擎文件不见了，建议重新安装一次「完整版」安装包"
                    : "没有找到离线 OCR 引擎。装的如果是「标准版」安装包，它不自带引擎 —— " +
                      "换「完整版」安装包最省事；也可以装 Umi-OCR 后在「设置 → 图片识别」里指定它的目录，" +
                      "或者把识别引擎换成「视觉大模型」（联网、不用下载任何东西）");

        return await UmiOcrEngine.RecognizeViaEngineAsync(imageBytes, exePath, umi.ConfigPath, null, ct);
    }

    /// <summary>确定要用的 PaddleOCR-json.exe：优先配置，其次自动探测。</summary>
    public string? ResolveExe(UmiOcrConfig? umi)
    {
        umi ??= new UmiOcrConfig();

        if (!string.IsNullOrWhiteSpace(umi.EngineExePath) && File.Exists(umi.EngineExePath))
            return umi.EngineExePath;

        if (!string.IsNullOrWhiteSpace(umi.InstallDirectory))
        {
            var found = UmiOcrEngine.FindPaddleExe(umi.InstallDirectory);
            if (found is not null) return found;
        }

        return UmiOcrEngine.DetectPaddleExe();
    }

    /// <summary>Ollama 本地视觉模型：走 OpenAI 兼容接口传图片，让它把图里文字读出来。</summary>
    private async Task<OcrResult> RecognizeOllamaAsync(OcrConfig ocr, byte[] imageBytes, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var config = Config;

        AiProviderConfig provider;
        if (!string.IsNullOrWhiteSpace(ocr.OllamaProviderId))
        {
            provider = config.AiProviders.FirstOrDefault(p => p.Id == ocr.OllamaProviderId)?.Clone()
                       ?? BuildOllamaProvider(ocr);
        }
        else
        {
            provider = BuildOllamaProvider(ocr);
        }

        provider.Model = string.IsNullOrWhiteSpace(ocr.OllamaModel) ? provider.Model : ocr.OllamaModel;
        provider.UseJsonMode = false;
        provider.Temperature = 0;
        provider.MaxTokens = Math.Max(provider.MaxTokens, 1024);

        var messages = new List<Ai.AiChatMessage>
        {
            Ai.AiChatMessage.UserWithImage(DefaultVisionPrompt, imageBytes, ScreenCapture.DetectMime(imageBytes)),
        };

        var client = new Ai.AiChatClient();
        var result = await client.CompleteAsync(provider, messages, jsonMode: false, ct);

        stopwatch.Stop();

        if (!result.Ok)
        {
            return OcrResult.Fail(OcrEngineKind.Ollama.ToConfigValue(),
                $"视觉模型调用失败：{result.Error}。请确认 Ollama 已启动（ollama serve）并已拉取模型：ollama pull {provider.Model}");
        }

        var text = (result.Content ?? string.Empty).Trim();
        text = text.Replace("```", string.Empty).Trim();

        return new OcrResult
        {
            Ok = true,
            Engine = OcrEngineKind.Ollama.ToConfigValue(),
            Text = text,
            Confidence = text.Length > 0 ? 0.7 : 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Warnings = text.Length == 0 ? new List<string> { "视觉模型没有读出文字" } : new List<string>(),
        };
    }

    private static AiProviderConfig BuildOllamaProvider(OcrConfig ocr) => new()
    {
        Id = "ocr-ollama",
        Name = "Ollama 视觉模型",
        ProviderKind = "ollama",
        BaseUrl = string.IsNullOrWhiteSpace(ocr.OllamaBaseUrl) ? "http://127.0.0.1:11434/v1" : ocr.OllamaBaseUrl,
        Model = string.IsNullOrWhiteSpace(ocr.OllamaModel) ? "qwen2.5vl:7b" : ocr.OllamaModel,
        UseJsonMode = false,
        Temperature = 0,
        MaxTokens = 1024,
        ApiKey = "ollama",
        TimeoutSeconds = 120,
        VisionCapable = true,
    };

    /// <summary>识别用的内置提示词（视觉大模型与 Ollama 共用）。</summary>
    public const string DefaultVisionPrompt =
        "请把图片里的所有文字按原来的行序逐行读出来，只输出文字本身，" +
        "不要翻译、不要总结、不要加任何解释或 Markdown 标记。如果图片里没有文字，就输出空。";

    /// <summary>
    /// 视觉大模型识别：走「模型与网络」里已经配好的服务商（OpenAI 兼容接口传图），
    /// 不用另填 key；也能用 BaseUrlOverride/ApiKeyOverride 直连任意兼容接口。
    /// </summary>
    private async Task<OcrResult> RecognizeVisionAsync(OcrConfig ocr, byte[] imageBytes, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var vision = ocr.Vision ?? new VisionOcrConfig();
        var config = Config;

        AiProviderConfig? provider = null;

        if (!string.IsNullOrWhiteSpace(vision.BaseUrlOverride))
        {
            provider = new AiProviderConfig
            {
                Id = "ocr-vision-direct",
                Name = "视觉大模型（自定义接口）",
                ProviderKind = "custom",
                BaseUrl = vision.BaseUrlOverride.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(vision.ApiKeyOverride) ? string.Empty : vision.ApiKeyOverride.Trim(),
                Model = string.IsNullOrWhiteSpace(vision.Model) ? "gpt-4o-mini" : vision.Model.Trim(),
                UseJsonMode = false,
                Temperature = 0,
                MaxTokens = 2048,
                TimeoutSeconds = 120,
                VisionCapable = true,
            };
        }
        else
        {
            var candidates = config.AiProviders ?? new List<AiProviderConfig>();

            if (string.IsNullOrWhiteSpace(vision.ProviderId))
            {
                // 自动挑：优先「支持图片 + 已启用」；退一步挑「支持图片」。
                // 绝不去挑一个明确不支持图片的模型 —— 那样请求发出去也是白报错。
                provider = candidates.FirstOrDefault(p => p.VisionCapable && p.Enabled)
                           ?? candidates.FirstOrDefault(p => p.VisionCapable);

                if (provider is null)
                {
                    return OcrResult.Fail(OcrEngineKind.Vision.ToConfigValue(),
                        "还没有可用的视觉模型（没有哪个服务商标了「支持图片」）。" +
                        "请到「设置 → 1 · 模型与网络」里配一个支持图片的模型" +
                        "（比如硅基流动的 Qwen/Qwen2.5-VL-72B-Instruct、百炼的 qwen-vl-max、OpenAI 的 gpt-4o-mini），" +
                        "再回这里选它；也可以直接在上面填接口地址，不依赖服务商列表");
                }
            }
            else
            {
                // 用户明确选了一个服务商就尊重它，只提醒不拦
                provider = candidates.FirstOrDefault(p => p.Id == vision.ProviderId);

                if (provider is null)
                {
                    return OcrResult.Fail(OcrEngineKind.Vision.ToConfigValue(),
                        "选中的视觉模型服务商已经不在列表里了（可能被删了），请回到「设置 → 图片识别」重新选一个");
                }
            }

            provider = provider.Clone();
            provider.UseJsonMode = false;
            provider.Temperature = 0;
            provider.MaxTokens = Math.Max(provider.MaxTokens, 2048);
            provider.VisionCapable = true;
        }

        if (!string.IsNullOrWhiteSpace(vision.Model) && string.IsNullOrWhiteSpace(vision.BaseUrlOverride))
            provider.Model = vision.Model.Trim();

        var prompt = string.IsNullOrWhiteSpace(vision.Prompt) ? DefaultVisionPrompt : vision.Prompt!.Trim();

        var messages = new List<Ai.AiChatMessage>
        {
            Ai.AiChatMessage.UserWithImage(prompt, imageBytes, ScreenCapture.DetectMime(imageBytes)),
        };

        var client = new Ai.AiChatClient();
        var result = await client.CompleteAsync(provider, messages, jsonMode: false, ct);

        stopwatch.Stop();

        if (!result.Ok)
        {
            return OcrResult.Fail(OcrEngineKind.Vision.ToConfigValue(),
                $"视觉模型「{provider.DisplayName} / {provider.Model}」调用失败：{result.Error}");
        }

        var text = (result.Content ?? string.Empty).Trim().Replace("```", string.Empty).Trim();

        var ocrResult = new OcrResult
        {
            Ok = true,
            Engine = OcrEngineKind.Vision.ToConfigValue(),
            Text = text,
            Confidence = text.Length > 0 ? 0.75 : 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Warnings = text.Length == 0 ? new List<string> { "视觉模型没有读出文字" } : new List<string>(),
        };

        if (!string.IsNullOrWhiteSpace(result.Note)) ocrResult.Warnings.Add(result.Note!);

        return ocrResult;
    }

    /// <summary>自定义 HTTP OCR：请求体模板里替换 {{image_base64}} / {{mime}}，再按点分路径取结果。</summary>
    private async Task<OcrResult> RecognizeCustomAsync(CustomOcrConfig custom, byte[] imageBytes, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(custom.Url))
            return OcrResult.Fail("custom", "还没有填写自定义 OCR 接口地址");

        var base64 = Convert.ToBase64String(imageBytes);
        var mime = ScreenCapture.DetectMime(imageBytes);
        var body = (custom.BodyTemplate ?? "{\"image\":\"{{image_base64}}\"}")
            .Replace("{{image_base64}}", base64)
            .Replace("{{mime}}", mime);

        try
        {
            using var http = HttpClients.Create();
            using var request = new HttpRequestMessage(
                string.Equals(custom.Method, "get", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post,
                custom.Url);

            foreach (var (key, value) in custom.Headers ?? new Dictionary<string, string>())
            {
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(key, value);
            }

            if (request.Method == HttpMethod.Post)
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return OcrResult.Fail("custom", $"接口返回 HTTP {(int)response.StatusCode}：{Trim(text, 200)}");

            var extracted = ExtractByPath(text, custom.ResultPath);
            if (extracted is null)
                return OcrResult.Fail("custom", $"接口返回里找不到路径 {custom.ResultPath}，原始内容：{Trim(text, 200)}");

            return new OcrResult
            {
                Ok = true,
                Engine = "custom",
                Text = extracted,
                Confidence = extracted.Length > 0 ? 0.8 : 0,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Warnings = extracted.Length == 0 ? new List<string> { "接口返回的文本为空" } : new List<string>(),
            };
        }
        catch (TaskCanceledException)
        {
            return OcrResult.Fail("custom", "请求超时");
        }
        catch (Exception ex)
        {
            return OcrResult.Fail("custom", "请求失败：" + ex.Message);
        }
    }

    /// <summary>按 data.text / result.0.text 这样的点分路径取值，也支持数组索引。</summary>
    public static string? ExtractByPath(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return json.Trim();
        }

        using (doc)
        {
            var current = doc.RootElement;
            if (string.IsNullOrWhiteSpace(path)) return ElementToString(current);

            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
                {
                    if (index < 0 || index >= current.GetArrayLength()) return null;
                    current = current[index];
                    continue;
                }

                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(segment, out var next))
                {
                    // 大小写不敏感地再找一遍
                    var match = current.EnumerateObject()
                        .FirstOrDefault(p => string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase));
                    if (match.Value.ValueKind == JsonValueKind.Undefined) return null;
                    next = match.Value;
                }
                current = next;
            }

            return ElementToString(current);
        }
    }

    private static string? ElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => string.Join("\n", element.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("text", out var t)
                ? t.GetString()
                : ElementToString(e))
            .Where(s => !string.IsNullOrEmpty(s))),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null,
    };

    private static string Trim(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + "…");

    /// <summary>识别并解析成题目。</summary>
    public async Task<(OcrResult Ocr, OcrQuestionParser.ParsedQuestion Question)> RecognizeQuestionAsync(
        byte[] imageBytes,
        CancellationToken ct = default)
    {
        var ocr = Config.Ocr ?? new OcrConfig();
        var result = await RecognizeAsync(imageBytes, ct);
        var parsed = ocr.AutoSplitTitleOptions
            ? OcrQuestionParser.Parse(result.Text)
            : new OcrQuestionParser.ParsedQuestion
            {
                Title = OcrQuestionParser.Flatten(result.Text),
                Type = QuestionTypeInference.Infer(OcrQuestionParser.Flatten(result.Text), null),
            };

        if (string.IsNullOrWhiteSpace(parsed.Title) && result.Text.Length > 0)
        {
            parsed.Title = OcrQuestionParser.Flatten(result.Text);
            parsed.Type = QuestionTypeInference.Infer(parsed.Title, parsed.Options);
        }

        return (result, parsed);
    }

    /// <summary>当前引擎的一句话说明，给设置页显示。</summary>
    public string DescribeCurrent()
    {
        var ocr = Config.Ocr ?? new OcrConfig();
        var kind = OcrEngineKindExtensions.ParseEngine(ocr.Engine);
        return kind switch
        {
            OcrEngineKind.Umi => DescribeUmi(ocr),
            OcrEngineKind.Windows => $"Windows 内置 OCR（语言 {ocr.WindowsLanguage}）",
            OcrEngineKind.Vision => DescribeVision(ocr),
            OcrEngineKind.Ollama => $"Ollama 视觉模型（{ocr.OllamaModel} @ {ocr.OllamaBaseUrl}）",
            OcrEngineKind.Custom => string.IsNullOrWhiteSpace(ocr.Custom?.Url) ? "自定义接口（未配置地址）" : $"自定义接口（{ocr.Custom!.Url}）",
            _ => "未启用",
        };
    }

    private string DescribeVision(OcrConfig ocr)
    {
        var vision = ocr.Vision ?? new VisionOcrConfig();

        if (!string.IsNullOrWhiteSpace(vision.BaseUrlOverride))
        {
            var model = string.IsNullOrWhiteSpace(vision.Model) ? "gpt-4o-mini" : vision.Model;
            return $"视觉大模型（自定义接口 {vision.BaseUrlOverride} · {model}）";
        }

        var providers = Config.AiProviders ?? new List<AiProviderConfig>();
        var provider = string.IsNullOrWhiteSpace(vision.ProviderId)
            ? providers.FirstOrDefault(p => p.VisionCapable && p.Enabled) ?? providers.FirstOrDefault(p => p.VisionCapable)
            : providers.FirstOrDefault(p => p.Id == vision.ProviderId);

        if (provider is null) return "视觉大模型（还没有配支持图片的模型）";

        var name = string.IsNullOrWhiteSpace(vision.Model) ? provider.Model : vision.Model;
        return $"视觉大模型（{provider.DisplayName} · {name}）";
    }

    private string DescribeUmi(OcrConfig ocr)
    {
        var umi = ocr.Umi ?? new UmiOcrConfig();
        var exe = ResolveExe(umi);

        if (exe is null)
        {
            return UmiOcrEngine.HasBundledEngine
                ? "Umi-OCR · 内置引擎（文件异常）"
                : "Umi-OCR · 没找到引擎（标准版不自带，或本机没装 Umi-OCR）";
        }

        var inner = UmiOcrEngine.EngineDirectory(exe) ?? string.Empty;
        var bundled = string.Equals(
            Path.GetFullPath(inner).TrimEnd('\\'),
            Path.GetFullPath(UmiOcrEngine.BundledEngineDirectory).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        // 内置引擎没有 Umi-OCR 主程序，HTTP 那套必然连不上，所以别在状态里写 HTTP 地址
        if (bundled) return "Umi-OCR · 内置引擎（不用另装，本机直连）";

        var mode = umi.UseHttp ? $"HTTP {UmiOcrEngine.NormalizeBase(umi.ApiBaseUrl)}" : "直连引擎";
        return $"Umi-OCR · {mode} · {Path.GetFileName(exe)}";
    }
}