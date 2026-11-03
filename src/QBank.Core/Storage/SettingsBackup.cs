using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QBank.Core.Models;

namespace QBank.Core.Storage;

/// <summary>备份包含哪些内容。</summary>
public enum BackupScope
{
    /// <summary>全部设置（AI 服务商、OCS 题库、OCR、网络、窗口、局域网…）。</summary>
    All = 0,

    /// <summary>只备份 AI 服务商与它们的 Key。</summary>
    AiOnly = 1,
}

/// <summary>恢复时怎么处理已有设置。</summary>
public enum BackupImportMode
{
    /// <summary>用备份覆盖（数据目录会保留当前值）。</summary>
    Replace = 0,

    /// <summary>AI 服务商与 OCS 题库按 Id 合并，其余设置仍然覆盖。</summary>
    Merge = 1,
}

/// <summary>导出选项。</summary>
public sealed class BackupOptions
{
    public BackupScope Scope { get; set; } = BackupScope.All;

    /// <summary>备份口令。留空则不加密（Key 会以明文存在文件里）。</summary>
    public string? Password { get; set; }

    /// <summary>备注，会写进文件、恢复时显示。</summary>
    public string? Note { get; set; }
}

/// <summary>一份备份的元信息（不需要口令就能读）。</summary>
public sealed class BackupInfo
{
    public string FilePath { get; set; } = string.Empty;

    public string FileName => string.IsNullOrEmpty(FilePath) ? "（剪贴板）" : Path.GetFileName(FilePath);

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public bool Encrypted { get; set; }

    public BackupScope Scope { get; set; }

    public int AiProviderCount { get; set; }

    public int OcsSourceCount { get; set; }

    public bool HasApiKeys { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string ScopeText => Scope == BackupScope.AiOnly ? "仅 AI 接口" : "全部设置";

    public string CreatedText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => SizeBytes < 1024
        ? SizeBytes + " B"
        : SizeBytes < 1024 * 1024
            ? (SizeBytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB"
            : (SizeBytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MB";

    /// <summary>列表里显示的一行摘要。</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            CreatedText,
            ScopeText,
            Encrypted ? "已加密" : (HasApiKeys ? "明文含 Key" : "无 Key"),
        };

        if (Scope == BackupScope.AiOnly)
        {
            parts.Add($"{AiProviderCount} 个服务商" + (HasApiKeys ? "（含 Key）" : string.Empty));
        }
        else
        {
            parts.Add($"{AiProviderCount} 个服务商 / {OcsSourceCount} 个题库");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>恢复结果。</summary>
public sealed class BackupImportResult
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<string> Changes { get; set; } = new();

    public string Describe() => Ok
        ? string.Join("；", Changes)
        : "恢复失败：" + (Error ?? "未知原因");
}

/// <summary>
/// 设置的备份与恢复。
///
/// 用途：换机器、重装系统、调坏了想退回上一版 —— 把 AI 接口和 Key 一行行重新填太痛苦。
/// 备份是一个自包含的 JSON 文件，可以用口令加密（AES-256-GCM + PBKDF2-SHA256）。
/// 口令一旦丢了就打不开，本程序不做任何找回。
/// </summary>
public static class SettingsBackup
{
    public const string FormatId = "qbank-settings";
    public const int FormatVersion = 1;
    public const string FileExtension = ".qbankcfg";

    private const int KdfIterations = 120_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── 导出 ────────────────────────────────────────────────────────────

    /// <summary>深拷贝一份配置（导出时用，避免把界面里的临时改动直接写进正在用的对象）。</summary>
    public static AppConfig Clone(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, Json);
        return JsonSerializer.Deserialize<AppConfig>(json, Json) ?? AppConfig.CreateDefault();
    }

    /// <summary>把配置导出成可保存/可粘贴的 JSON 文本。</summary>
    public static string Export(AppConfig config, BackupOptions? options = null)
    {
        options ??= new BackupOptions();

        var settingsNode = JsonSerializer.SerializeToNode(config, Json)!.AsObject();

        // 只备份 AI：其余字段全部丢掉，避免恢复时把别的设置也改了
        if (options.Scope == BackupScope.AiOnly)
        {
            var minimal = new JsonObject
            {
                ["aiProviders"] = settingsNode["aiProviders"]?.DeepClone(),
            };
            settingsNode = minimal;
        }

        var envelope = new JsonObject
        {
            ["format"] = FormatId,
            ["version"] = FormatVersion,
            ["appVersion"] = AppVersionText(),
            ["exportedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["scope"] = options.Scope == BackupScope.AiOnly ? "ai" : "all",
            ["encrypted"] = !string.IsNullOrEmpty(options.Password),
        };

        if (!string.IsNullOrWhiteSpace(options.Note)) envelope["note"] = options.Note.Trim();

        var summary = new JsonObject
        {
            ["aiProviders"] = config.AiProviders?.Count ?? 0,
            ["ocsSources"] = options.Scope == BackupScope.AiOnly ? 0 : (config.OcsSources?.Count ?? 0),
            ["hasApiKeys"] = HasAnyApiKey(config),
        };
        envelope["summary"] = summary;

        if (string.IsNullOrEmpty(options.Password))
        {
            envelope["settings"] = settingsNode;
        }
        else
        {
            var plain = Encoding.UTF8.GetBytes(settingsNode.ToJsonString(Json));
            var (salt, nonce, cipher, tag) = Encrypt(plain, options.Password);
            envelope["kdf"] = new JsonObject
            {
                ["alg"] = "pbkdf2-sha256",
                ["iterations"] = KdfIterations,
                ["salt"] = Convert.ToBase64String(salt),
            };
            envelope["cipher"] = new JsonObject
            {
                ["alg"] = "aes-256-gcm",
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
            };
            envelope["payload"] = Convert.ToBase64String(cipher);
        }

        return envelope.ToJsonString(Json);
    }

    /// <summary>写一份「快速备份」到数据目录下的 backups 文件夹，返回文件路径。</summary>
    public static string QuickBackup(AppConfig config, string dataDir, BackupOptions? options = null)
    {
        var folder = BackupFolder(dataDir);
        Directory.CreateDirectory(folder);

        var name = "qbank-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + FileExtension;
        var path = Path.Combine(folder, name);

        // 同一秒内连点两次也不覆盖
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder,
                $"qbank-settings-{DateTime.Now:yyyyMMdd-HHmmss}-{index}{FileExtension}");
            index++;
        }

        File.WriteAllText(path, Export(config, options), new UTF8Encoding(false));
        return path;
    }

    public static string BackupFolder(string dataDir)
        => Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? AppPaths.DefaultDataDir : dataDir, "backups");

    /// <summary>列出 backups 文件夹里的备份，按时间倒序。</summary>
    public static List<BackupInfo> ListQuickBackups(string dataDir)
    {
        var list = new List<BackupInfo>();
        var folder = BackupFolder(dataDir);

        try
        {
            if (!Directory.Exists(folder)) return list;

            foreach (var file in Directory.EnumerateFiles(folder, "*" + FileExtension))
            {
                var info = PeekFile(file);
                if (info is not null) list.Add(info);
            }
        }
        catch (Exception)
        {
            // 目录读不了就当没有备份
        }

        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception)
        {
            // 删不掉就当失败
        }

        return false;
    }

    // ─── 读取元信息 ──────────────────────────────────────────────────────

    /// <summary>读备份文件的元信息（不需要口令）。</summary>
    public static BackupInfo? PeekFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = Peek(File.ReadAllText(path));
            if (info is null) return null;
            info.FilePath = path;
            info.SizeBytes = new FileInfo(path).Length;
            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析备份文本的元信息（不需要口令）；不是本程序的备份就返回 null。</summary>
    public static BackupInfo? Peek(string content)
    {
        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null) return null;
            if (!string.Equals((string?)root["format"], FormatId, StringComparison.Ordinal)) return null;

            var info = new BackupInfo
            {
                Encrypted = (bool?)root["encrypted"] ?? (root["payload"] is not null),
                Scope = string.Equals((string?)root["scope"], "ai", StringComparison.OrdinalIgnoreCase)
                    ? BackupScope.AiOnly
                    : BackupScope.All,
                AppVersion = (string?)root["appVersion"] ?? string.Empty,
                Note = (string?)root["note"],
            };

            if (DateTimeOffset.TryParse((string?)root["exportedAt"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var exported))
                info.CreatedAt = exported;

            if (root["summary"] is JsonObject summary)
            {
                info.AiProviderCount = (int?)summary["aiProviders"] ?? 0;
                info.OcsSourceCount = (int?)summary["ocsSources"] ?? 0;
                info.HasApiKeys = (bool?)summary["hasApiKeys"] ?? false;
            }

            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool LooksLikeBackup(string? content) => !string.IsNullOrWhiteSpace(content) && Peek(content) is not null;

    // ─── 恢复 ────────────────────────────────────────────────────────────

    /// <summary>把备份应用到 <paramref name="target"/>（原地修改，调用方负责落盘）。</summary>
    public static BackupImportResult Import(
        string content,
        AppConfig target,
        BackupImportMode mode = BackupImportMode.Replace,
        string? password = null)
    {
        var result = new BackupImportResult();

        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null)
            {
                result.Error = "不是一个合法的 JSON 对象。";
                return result;
            }

            if (!string.Equals((string?)root["format"], FormatId, StringComparison.Ordinal))
            {
                result.Error = "这不是题库工坊的设置备份文件（format 字段对不上）。";
                return result;
            }

            var version = (int?)root["version"] ?? 0;
            if (version > FormatVersion)
            {
                result.Error = $"备份来自更新版本（格式 v{version}），当前程序只认到 v{FormatVersion}。请升级程序后再恢复。";
                return result;
            }

            var encrypted = (bool?)root["encrypted"] ?? (root["payload"] is not null);
            var scope = string.Equals((string?)root["scope"], "ai", StringComparison.OrdinalIgnoreCase)
                ? BackupScope.AiOnly
                : BackupScope.All;

            string settingsJson;
            if (encrypted)
            {
                if (string.IsNullOrEmpty(password))
                {
                    result.Error = "这份备份加了口令，请先填口令再恢复。";
                    return result;
                }

                var kdf = root["kdf"] as JsonObject;
                var cipher = root["cipher"] as JsonObject;
                if (kdf is null || cipher is null)
                {
                    result.Error = "备份文件的加密信息不完整，可能被改坏了。";
                    return result;
                }

                var iterations = (int?)kdf["iterations"] ?? KdfIterations;
                var salt = Convert.FromBase64String((string?)kdf["salt"] ?? string.Empty);
                var nonce = Convert.FromBase64String((string?)cipher["nonce"] ?? string.Empty);
                var tag = Convert.FromBase64String((string?)cipher["tag"] ?? string.Empty);
                var cipherBytes = Convert.FromBase64String((string?)root["payload"] ?? string.Empty);

                byte[] plain;
                try
                {
                    plain = Decrypt(cipherBytes, salt, nonce, tag, password, iterations);
                }
                catch (CryptographicException)
                {
                    result.Error = "口令不正确，或者文件被改动过。";
                    return result;
                }

                settingsJson = Encoding.UTF8.GetString(plain);
            }
            else
            {
                var node = root["settings"];
                if (node is null)
                {
                    result.Error = "备份里没有 settings 内容。";
                    return result;
                }

                settingsJson = node.ToJsonString(Json);
            }

            var incoming = JsonSerializer.Deserialize<AppConfig>(settingsJson, Json);
            if (incoming is null)
            {
                result.Error = "备份内容解析不出来。";
                return result;
            }

            Apply(incoming, target, mode, scope, result.Changes);
            result.Ok = true;
            return result;
        }
        catch (JsonException ex)
        {
            result.Error = "JSON 解析失败：" + ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>从文件恢复。</summary>
    public static BackupImportResult ImportFile(
        string path,
        AppConfig target,
        BackupImportMode mode = BackupImportMode.Replace,
        string? password = null)
    {
        try
        {
            if (!File.Exists(path))
                return new BackupImportResult { Error = "文件不存在：" + path };

            return Import(File.ReadAllText(path), target, mode, password);
        }
        catch (Exception ex)
        {
            return new BackupImportResult { Error = ex.Message };
        }
    }

    private static void Apply(
        AppConfig incoming,
        AppConfig target,
        BackupImportMode mode,
        BackupScope scope,
        List<string> changes)
    {
        // 数据目录是本机路径，永远保留当前值，不然换台机器会直接跑飞
        var keepDataDir = target.DataDir;

        if (scope == BackupScope.AiOnly)
        {
            var (updated, added) = MergeProviders(target, incoming.AiProviders ?? new(), mode == BackupImportMode.Merge);
            changes.Add($"AI 服务商：更新 {updated} 个" + (added > 0 ? $"，新增 {added} 个" : string.Empty));
            changes.Add(ApiKeySummary(target));
            return;
        }

        var oldProviderCount = target.AiProviders?.Count ?? 0;
        var oldOcsCount = target.OcsSources?.Count ?? 0;

        if (mode == BackupImportMode.Merge)
        {
            var (updated, added) = MergeProviders(target, incoming.AiProviders ?? new(), true);
            var (ocsUpdated, ocsAdded) = MergeOcsSources(target, incoming.OcsSources ?? new());
            changes.Add($"AI 服务商：更新 {updated} 个，新增 {added} 个");
            changes.Add($"OCS 题库：更新 {ocsUpdated} 个，新增 {ocsAdded} 个");

            // 其余设置直接覆盖，但保留刚合并好的集合
            var mergedProviders = target.AiProviders ?? new List<AiProviderConfig>();
            var mergedOcs = target.OcsSources ?? new List<Ocs.AnswererWrapper>();
            CopySettings(incoming, target);
            target.AiProviders = mergedProviders;
            target.OcsSources = mergedOcs;
        }
        else
        {
            CopySettings(incoming, target);
            changes.Add($"AI 服务商：{oldProviderCount} → {target.AiProviders?.Count ?? 0} 个");
            changes.Add($"OCS 题库：{oldOcsCount} → {target.OcsSources?.Count ?? 0} 个");
        }

        target.DataDir = keepDataDir;

        // 窗口位置换机器就没意义了，清掉让程序自己摆
        if (target.Window is not null)
        {
            target.Window.WindowX = int.MinValue;
            target.Window.WindowY = int.MinValue;
        }

        changes.Add(ApiKeySummary(target));
        changes.Add("OCR 引擎：" + (target.Ocr?.Engine ?? "umi"));
        changes.Add("网络出口：" + (target.Network?.Describe() ?? "跟随系统代理"));
    }

    /// <summary>把 incoming 的设置字段搬进 target（集合另行处理）。</summary>
    private static void CopySettings(AppConfig incoming, AppConfig target)
    {
        target.Search = incoming.Search ?? new SearchOptions();
        target.LocalBank = incoming.LocalBank ?? new LocalBankConfig();
        target.LanShare = incoming.LanShare ?? new LanServerConfig();
        target.OcsBridge = incoming.OcsBridge ?? new OcsBridgeConfig();
        target.Ocr = incoming.Ocr ?? new OcrConfig();
        target.Network = incoming.Network ?? new NetworkConfig();
        target.Window = incoming.Window ?? new WindowConfig();
        target.Theme = incoming.Theme;
        target.AiProviders = incoming.AiProviders ?? new List<AiProviderConfig>();
        target.OcsSources = incoming.OcsSources ?? new List<Ocs.AnswererWrapper>();
        target.LanPeers = incoming.LanPeers ?? new List<LanPeerConfig>();
    }

    /// <summary>按 Id 合并 AI 服务商：同 Id 更新（备份里的值覆盖），没有 Id 匹配就按 BaseUrl+Model 认，再没有就追加。</summary>
    private static (int Updated, int Added) MergeProviders(
        AppConfig target,
        List<AiProviderConfig> incoming,
        bool merge)
    {
        target.AiProviders ??= new List<AiProviderConfig>();

        if (!merge)
        {
            target.AiProviders = incoming;
            return (incoming.Count, 0);
        }

        var updated = 0;
        var added = 0;

        foreach (var provider in incoming)
        {
            var existing = target.AiProviders.FirstOrDefault(p => p.Id == provider.Id)
                           ?? target.AiProviders.FirstOrDefault(p =>
                               string.Equals(p.BaseUrl?.Trim(), provider.BaseUrl?.Trim(), StringComparison.OrdinalIgnoreCase)
                               && string.Equals(p.Model?.Trim(), provider.Model?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                target.AiProviders.Add(provider);
                added++;
                continue;
            }

            existing.Name = provider.Name;
            existing.ProviderKind = provider.ProviderKind;
            existing.BaseUrl = provider.BaseUrl;
            existing.Model = provider.Model;
            existing.Temperature = provider.Temperature;
            existing.MaxTokens = provider.MaxTokens;
            existing.UseJsonMode = provider.UseJsonMode;
            existing.Enabled = provider.Enabled;
            existing.TimeoutSeconds = provider.TimeoutSeconds;
            existing.Priority = provider.Priority;
            existing.ExtraParamsJson = provider.ExtraParamsJson;
            existing.SystemPrompt = provider.SystemPrompt;
            existing.VisionCapable = provider.VisionCapable;

            // Key 只在新值非空时覆盖，避免拿一份没填 Key 的备份把现有 Key 抹掉
            if (!string.IsNullOrWhiteSpace(provider.ApiKey)) existing.ApiKey = provider.ApiKey;

            updated++;
        }

        return (updated, added);
    }

    private static (int Updated, int Added) MergeOcsSources(AppConfig target, List<Ocs.AnswererWrapper> incoming)
    {
        target.OcsSources ??= new List<Ocs.AnswererWrapper>();

        var updated = 0;
        var added = 0;

        foreach (var source in incoming)
        {
            var existing = target.OcsSources.FirstOrDefault(s => s.Id == source.Id)
                           ?? target.OcsSources.FirstOrDefault(s =>
                               string.Equals(s.Url?.Trim(), source.Url?.Trim(), StringComparison.OrdinalIgnoreCase)
                               && string.Equals(s.Name?.Trim(), source.Name?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                target.OcsSources.Add(source);
                added++;
                continue;
            }

            existing.Name = source.Name;
            existing.Url = source.Url;
            existing.Homepage = source.Homepage;
            existing.Data = source.Data;
            existing.Method = source.Method;
            existing.ContentType = source.ContentType;
            existing.Type = source.Type;
            existing.Headers = source.Headers;
            existing.Handler = source.Handler;
            updated++;
        }

        return (updated, added);
    }

    private static string ApiKeySummary(AppConfig config)
    {
        var providers = config.AiProviders ?? new List<AiProviderConfig>();
        var withKey = providers.Count(p => !string.IsNullOrWhiteSpace(p.ApiKey));
        return withKey == 0
            ? "API Key：一个都没填"
            : $"API Key：{withKey} 个服务商已填写";
    }

    private static bool HasAnyApiKey(AppConfig config)
        => (config.AiProviders ?? new List<AiProviderConfig>()).Any(p => !string.IsNullOrWhiteSpace(p.ApiKey));

    public static string AppVersionText()
    {
        var v = typeof(SettingsBackup).Assembly.GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ─── 加解密 ──────────────────────────────────────────────────────────

    private static (byte[] Salt, byte[] Nonce, byte[] Cipher, byte[] Tag) Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, KdfIterations);

        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag);

        CryptographicOperations.ZeroMemory(key);
        return (salt, nonce, cipher, tag);
    }

    private static byte[] Decrypt(byte[] cipher, byte[] salt, byte[] nonce, byte[] tag, string password, int iterations)
    {
        var key = DeriveKey(password, salt, iterations);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);

        CryptographicOperations.ZeroMemory(key);
        return plain;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
}
