using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QBank.Core.Ocr;

/// <summary>
/// Umi-OCR（PaddleOCR-json 引擎）接入。
/// 两条通道：
///   ① HTTP：Umi-OCR 开了「HTTP 服务」时，POST /api/ocr 传 base64（推荐，常驻进程、速度快）；
///   ② 直连引擎：没开 HTTP 服务时，直接调用它自带的 PaddleOCR-json.exe（单图模式，进程用完即退）。
/// 会自动在常见安装位置里找 Umi-OCR，也可以在设置里手动指定路径。
/// </summary>
public static class UmiOcrEngine
{
    public const string EngineName = "umi";

    /// <summary>Umi-OCR 默认 HTTP 端口（可在其全局设置里改）。</summary>
    public const int DefaultHttpPort = 1224;

    /// <summary>常见安装位置。</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var roots = new List<string>();

        foreach (var drive in new[] { "C:", "D:", "E:", "F:" })
        {
            roots.Add($"{drive}\\Umi-OCR");
            roots.Add($"{drive}\\Program Files\\Umi-OCR");
            roots.Add($"{drive}\\Program Files (x86)\\Umi-OCR");
            roots.Add($"{drive}\\Tools\\Umi-OCR");
            roots.Add($"{drive}\\Software\\Umi-OCR");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roots.Add(Path.Combine(localAppData, "Programs", "Umi-OCR"));
        roots.Add(Path.Combine(localAppData, "Umi-OCR"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(userProfile, "Umi-OCR"));
        roots.Add(Path.Combine(userProfile, "Desktop", "Umi-OCR"));
        roots.Add(Path.Combine(userProfile, "Downloads", "Umi-OCR"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            roots.Add(Path.Combine(programFiles, "Umi-OCR"));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>在给定根目录下找 PaddleOCR-json.exe。</summary>
    public static string? FindPaddleExe(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var direct = Path.Combine(root, "UmiOCR-data", "plugins");
        if (!Directory.Exists(direct)) return null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(direct))
            {
                var name = Path.GetFileName(dir);
                if (!name.Contains("PaddleOCR", StringComparison.OrdinalIgnoreCase)) continue;

                var exe = Path.Combine(dir, "PaddleOCR-json.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    /// <summary>自动探测本机的 PaddleOCR-json.exe，找不到返回 null。</summary>
    public static string? DetectPaddleExe()
    {
        foreach (var root in CandidateRoots())
        {
            var exe = FindPaddleExe(root);
            if (exe is not null) return exe;
        }
        return null;
    }

    /// <summary>引擎目录（exe 所在目录），models 目录就在它下面。</summary>
    public static string? EngineDirectory(string? exePath)
        => string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);

    /// <summary>列出引擎目录下可用的语言配置（models/config_*.txt）。</summary>
    public static List<(string File, string Label)> AvailableModels(string? exePath)
    {
        var list = new List<(string, string)>();
        var dir = EngineDirectory(exePath);
        if (dir is null) return list;

        var models = Path.Combine(dir, "models");
        if (!Directory.Exists(models)) return list;

        try
        {
            foreach (var file in Directory.EnumerateFiles(models, "config_*.txt"))
            {
                var name = Path.GetFileNameWithoutExtension(file).Replace("config_", string.Empty);
                var label = name switch
                {
                    "chinese" => "简体中文",
                    "chinese_cht" => "繁體中文",
                    "en" => "English",
                    "japan" => "日本語",
                    "korean" => "한국어",
                    "cyrillic" => "Русский",
                    _ => name,
                };
                list.Add(($"models/{Path.GetFileName(file)}", label));
            }
        }
        catch (Exception)
        {
            // 目录读不了就当没有
        }

        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>探测 Umi-OCR 的 HTTP 服务是否在线。</summary>
    public static async Task<bool> PingHttpAsync(string? apiBase, CancellationToken ct = default)
    {
        var baseUrl = NormalizeBase(apiBase);
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2.5) };
            using var response = await http.GetAsync($"{baseUrl}/api/ocr/get_options", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string NormalizeBase(string? apiBase)
    {
        var text = string.IsNullOrWhiteSpace(apiBase) ? $"http://127.0.0.1:{DefaultHttpPort}" : apiBase!.Trim();
        text = text.TrimEnd('/');
        if (text.EndsWith("/api/ocr", StringComparison.OrdinalIgnoreCase))
            text = text[..^"/api/ocr".Length];
        else if (text.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            text = text[..^"/api".Length];

        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            text = "http://" + text;

        return text.TrimEnd('/');
    }

    /// <summary>通过 Umi-OCR 的 HTTP 接口识别（需要 Umi-OCR 正在运行且开启 HTTP 服务）。</summary>
    public static async Task<OcrResult> RecognizeViaHttpAsync(
        byte[] imageBytes,
        string? apiBase = null,
        string? language = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OcrResult { Engine = EngineName };

        if (imageBytes is null || imageBytes.Length == 0)
            return OcrResult.Fail(EngineName, "图片内容为空");

        var baseUrl = NormalizeBase(apiBase);
        try
        {
            var payload = BuildHttpPayload(imageBytes, language);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{baseUrl}/api/ocr", content, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return OcrResult.Fail(EngineName, $"Umi-OCR 返回 HTTP {(int)response.StatusCode}：{Shorten(body, 200)}");

            return ParseHttpResponse(body, stopwatch, result);
        }
        catch (TaskCanceledException)
        {
            return OcrResult.Fail(EngineName, $"连接 Umi-OCR 超时（{baseUrl}）。请确认 Umi-OCR 已启动并在「全局设置 → 高级 → HTTP 服务」里允许服务");
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            return OcrResult.Fail(EngineName,
                $"连不上 Umi-OCR（{baseUrl}）：{ex.Message}。请在 Umi-OCR 的「全局设置 → 高级」里勾选允许 HTTP 服务，并确认端口一致");
        }
        catch (Exception ex)
        {
            return OcrResult.Fail(EngineName, "调用 Umi-OCR 失败：" + ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static string BuildHttpPayload(byte[] imageBytes, string? language)
    {
        var options = new Dictionary<string, object>
        {
            ["data.format"] = "dict",
            ["tbpu.parser"] = "multi_line",
        };
        if (!string.IsNullOrWhiteSpace(language))
            options["ocr.language"] = language!.Trim();

        var payload = new Dictionary<string, object>
        {
            ["base64"] = Convert.ToBase64String(imageBytes),
            ["options"] = options,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static OcrResult ParseHttpResponse(string body, Stopwatch stopwatch, OcrResult result)
    {
        // Umi-OCR 的返回里可能带真实换行，偶尔会让严格 JSON 解析失败，先清洗一遍
        var clean = body.Replace("\r\n", "\\n").Replace("\n", "\\n");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(clean);
        }
        catch (JsonException)
        {
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return OcrResult.Fail(EngineName, "Umi-OCR 返回的内容不是合法 JSON：" + Shorten(body, 160) + " (" + ex.Message + ")");
            }
        }

        using (doc)
        {
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var c) ? c : -1;

            if (code != 100)
            {
                var message = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                if (code == 101)
                {
                    result.Text = string.Empty;
                    result.Confidence = 0;
                    result.Warnings.Add("Umi-OCR 没有在图片里检测到文字");
                    return result;
                }

                return OcrResult.Fail(EngineName, $"Umi-OCR 识别失败（code={code}）：{message ?? "未知错误"}");
            }

            if (!root.TryGetProperty("data", out var data))
                return OcrResult.Fail(EngineName, "Umi-OCR 返回里没有 data 字段");

            var texts = new List<string>();
            var scores = new List<double>();

            if (data.ValueKind == JsonValueKind.String)
            {
                texts.Add(data.GetString() ?? string.Empty);
            }
            else if (data.ValueKind == JsonValueKind.Array)
            {
                ParseBlocks(data, texts, scores);
            }

            result.Text = string.Join("\n", texts.Where(t => t.Length > 0));
            result.Confidence = scores.Count > 0 ? scores.Average() : (result.Text.Length > 0 ? 0.85 : 0);
            if (result.Text.Length == 0)
                result.Warnings.Add("Umi-OCR 没有识别出文字");
        }

        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static void ParseBlocks(JsonElement data, List<string> texts, List<double> scores)
    {
        foreach (var block in data.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;

            var text = block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;

            if (block.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var score))
                scores.Add(score);

            var end = block.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : "\n";

            texts.Add(text + (end ?? string.Empty));
        }
    }

    /// <summary>
    /// 不经过 Umi-OCR 主程序，直接调用它自带的 PaddleOCR-json.exe（单图模式）。
    /// 每次调用都会起一个进程，首次要加载模型（约 1~3 秒），优点是不依赖 Umi-OCR 是否打开。
    /// </summary>
    public static async Task<OcrResult> RecognizeViaEngineAsync(
        byte[] imageBytes,
        string exePath,
        string? configPath = null,
        string? tempDirectory = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OcrResult { Engine = EngineName };

        if (imageBytes is null || imageBytes.Length == 0)
            return OcrResult.Fail(EngineName, "图片内容为空");

        if (!File.Exists(exePath))
            return OcrResult.Fail(EngineName, "找不到 PaddleOCR-json.exe：" + exePath);

        var engineDir = Path.GetDirectoryName(exePath);
        if (engineDir is null)
            return OcrResult.Fail(EngineName, "引擎路径无效：" + exePath);

        string? tempFile = null;
        try
        {
            var tempDir = tempDirectory ?? Path.GetTempPath();
            AppPathsEnsure(tempDir);
            tempFile = Path.Combine(tempDir, $"qbank-ocr-{Guid.NewGuid():n}.png");
            await File.WriteAllBytesAsync(tempFile, imageBytes, ct);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            startInfo.ArgumentList.Add("--config_path");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(configPath) ? "models/config_chinese.txt" : configPath!);
            startInfo.ArgumentList.Add("--image_path");
            startInfo.ArgumentList.Add(tempFile);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return OcrResult.Fail(EngineName, "无法启动 PaddleOCR-json.exe");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // 让它自己读完，避免管道写满卡死

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return OcrResult.Fail(EngineName, "PaddleOCR 识别超时（90 秒）");
            }

            var stdout = await stdoutTask;

            var jsonLine = stdout
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith('{'));

            if (jsonLine is null)
                return OcrResult.Fail(EngineName, "引擎没有输出识别结果。原始输出：" + Shorten(stdout, 300));

            return ParseEngineJson(jsonLine, result, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Fail(EngineName, "已取消");
        }
        catch (Exception ex)
        {
            return OcrResult.Fail(EngineName, "调用 PaddleOCR 引擎失败：" + ex.Message);
        }
        finally
        {
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); } catch (IOException) { }
            }
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static OcrResult ParseEngineJson(string jsonLine, OcrResult result, Stopwatch stopwatch)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonLine);
        }
        catch (JsonException ex)
        {
            return OcrResult.Fail(EngineName, "引擎输出解析失败：" + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var c) ? c : -1;

            if (code != 100)
            {
                var message = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : "未知错误";

                if (code == 101)
                {
                    result.Text = string.Empty;
                    result.Warnings.Add("引擎没有在图片里检测到文字");
                    return result;
                }

                return OcrResult.Fail(EngineName, $"引擎识别失败（code={code}）：{message}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return OcrResult.Fail(EngineName, "引擎返回里没有 data 数组");

            var texts = new List<string>();
            var scores = new List<double>();

            foreach (var block in data.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;

                var text = block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                if (block.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var score))
                    scores.Add(score);

                var line = new OcrTextLine { Text = text };
                if (block.TryGetProperty("box", out var box) && box.ValueKind == JsonValueKind.Array)
                    ApplyBox(line, box);

                result.Lines.Add(line);
                texts.Add(text);
            }

            result.Text = string.Join("\n", texts.Where(x => x.Length > 0));
            result.Confidence = scores.Count > 0 ? scores.Average() : (result.Text.Length > 0 ? 0.85 : 0);
            if (result.Text.Length == 0)
                result.Warnings.Add("引擎没有识别出文字");
        }

        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static void ApplyBox(OcrTextLine line, JsonElement box)
    {
        // PaddleOCR 的 box 是顺时针四个角：[左上, 右上, 右下, 左下]
        var points = new List<(double X, double Y)>();
        foreach (var point in box.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array) continue;
            var values = point.EnumerateArray().Select(v => v.TryGetDouble(out var d) ? d : 0).ToList();
            if (values.Count >= 2) points.Add((values[0], values[1]));
        }

        if (points.Count < 4) return;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        line.Left = minX;
        line.Top = minY;
        line.Width = points.Max(p => p.X) - minX;
        line.Height = points.Max(p => p.Y) - minY;
    }

    private static void AppPathsEnsure(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + "…");

    /// <summary>一条给用户看的探测结果说明。</summary>
    public static async Task<string> DescribeAsync(string? apiBase, string? exePath, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var baseUrl = NormalizeBase(apiBase);
        var online = await PingHttpAsync(baseUrl, ct);
        lines.Add(online ? $"✅ HTTP 服务在线：{baseUrl}" : $"⛔ HTTP 服务未响应：{baseUrl}");

        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            lines.Add($"✅ 引擎文件：{exePath}");
        else
        {
            var detected = DetectPaddleExe();
            lines.Add(detected is null ? "⛔ 没有找到 PaddleOCR-json.exe" : $"✅ 已自动找到引擎：{detected}");
        }

        return string.Join("\n", lines);
    }
}