using System.Text.Json;
using QBank.Core.Models;

namespace QBank.Core.Storage;

/// <summary>数据目录与文件路径解析。</summary>
public static class AppPaths
{
    public const string AppFolderName = "QBank";

    public static string ExeDirectory => AppContext.BaseDirectory;

    /// <summary>便携模式：程序目录下放 portable.txt 或 portable 目录，数据就存在程序旁边。</summary>
    public static bool PortableMode =>
        File.Exists(Path.Combine(ExeDirectory, "portable.txt")) ||
        Directory.Exists(Path.Combine(ExeDirectory, "portable"));

    public static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string ResolveDataDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(ExeDirectory, expanded);
            return Path.GetFullPath(expanded);
        }
        return PortableMode ? Path.Combine(ExeDirectory, "data") : DefaultDataDir;
    }

    /// <summary>
    /// 选一个真正可用的数据目录：默认目录写不进去（权限/沙箱/只读盘）时，
    /// 依次回落到程序目录、临时目录，保证程序总能启动。
    /// </summary>
    public static string ResolveWritableDataDir(string? configured, out string? note)
    {
        note = null;
        var candidates = new List<string>();

        var primary = ResolveDataDir(configured);
        candidates.Add(primary);

        if (string.IsNullOrWhiteSpace(configured))
        {
            // %LOCALAPPDATA% 被策略限制时，再试漫游配置目录（标准做法）
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);
            if (!string.Equals(roaming, primary, StringComparison.OrdinalIgnoreCase))
                candidates.Add(roaming);

            candidates.Add(Path.Combine(ExeDirectory, "data"));
            candidates.Add(Path.Combine(Path.GetTempPath(), AppFolderName));
        }

        foreach (var dir in candidates)
        {
            if (TryEnsureDirectory(dir, out var reason))
            {
                Probe($"数据目录：{dir}（可用）");
                if (!string.Equals(dir, primary, StringComparison.OrdinalIgnoreCase))
                    note = $"默认数据目录不可用，已改用：{dir}";
                return dir;
            }
            Probe($"数据目录候选不可用：{dir} —— {reason}");
        }

        // 全都不行时仍然返回主目录，让上层报出真实错误
        note = $"数据目录不可写，已回落到临时目录：{candidates[^1]}";
        return candidates[^1];
    }

    /// <summary>诊断回调（启动日志用），未设置时不产生开销。</summary>
    public static Action<string>? ProbeLog { get; set; }

    private static void Probe(string message)
    {
        try { ProbeLog?.Invoke(message); } catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>试着创建目录并写一个探针文件，确认真的可写。</summary>
    public static bool TryEnsureDirectory(string dir) => TryEnsureDirectory(dir, out _);

    public static bool TryEnsureDirectory(string dir, out string? reason)
    {
        reason = null;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            reason = "建目录失败：" + ex.Message;
            return false;
        }

        try
        {
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = "写入探针失败：" + ex.Message;
            return false;
        }
    }

    public static string ConfigFile(string dataDir) => Path.Combine(dataDir, "config.json");
    public static string DatabaseFile(string dataDir) => Path.Combine(dataDir, "qbank.db");
    public static string LogFolder(string dataDir) => Path.Combine(dataDir, "logs");

    public static void EnsureDirectory(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}

/// <summary>配置读写（config.json），写入采用临时文件 + 替换，避免半截文件。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();

    public ConfigStore(string? dataDir = null, string? configFileName = null)
    {
        DataDir = AppPaths.ResolveWritableDataDir(dataDir, out var fallbackNote);
        FallbackNote = fallbackNote;
        AppPaths.EnsureDirectory(DataDir);
        ConfigFile = Path.Combine(DataDir, configFileName ?? "config.json");
        Config = Load();
    }

    /// <summary>数据目录回落到备用位置时的说明（null 表示用的就是期望位置）。</summary>
    public string? FallbackNote { get; }

    public string DataDir { get; }

    public string ConfigFile { get; }

    public string DatabaseFile => Path.Combine(DataDir, "qbank.db");

    public AppConfig Config { get; private set; }

    public event EventHandler? Changed;

    /// <summary>加载配置；文件不存在或损坏时用默认配置并落盘。</summary>
    public AppConfig Load()
    {
        lock (_lock)
        {
            if (File.Exists(ConfigFile))
            {
                try
                {
                    var json = File.ReadAllText(ConfigFile);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                    if (cfg is not null)
                    {
                        Normalize(cfg);
                        return cfg;
                    }
                }
                catch (Exception)
                {
                    // 配置损坏：留个备份，然后回到默认，避免程序起不来
                    try
                    {
                        var bad = ConfigFile + ".bad";
                        File.Copy(ConfigFile, bad, overwrite: true);
                    }
                    catch (IOException) { }
                }
            }

            var fresh = AppConfig.CreateDefault();
            Normalize(fresh);
            try
            {
                if (!File.Exists(ConfigFile)) WriteFile(fresh);
            }
            catch (IOException) { }
            return fresh;
        }
    }

    public void Reload()
    {
        Config = Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save() => Save(Config);

    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            Normalize(config);
            Config = config;
            WriteFile(config);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void WriteFile(AppConfig config)
    {
        try
        {
            AppPaths.EnsureDirectory(DataDir);
            var json = JsonSerializer.Serialize(config, Options);
            var temp = ConfigFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ConfigFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 磁盘只读/没权限时不能因为保存配置就把程序打挂
            LastSaveError = ex.Message;
        }
    }

    /// <summary>最近一次保存失败的原因（成功时为 null）。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>兜底配置：磁盘完全不可用时也不让程序起不来（数据不落盘）。</summary>
    public static ConfigStore CreateInMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName + "-mem");
        try
        {
            return new ConfigStore(dir);
        }
        catch (Exception)
        {
            // 连临时目录都不行：用一个必然不存在的路径构造一个配置对象
            return new ConfigStore(Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName + "-mem2"));
        }
    }

    /// <summary>补齐缺失的集合与默认项。</summary>
    private static void Normalize(AppConfig cfg)
    {
        cfg.OcsSources ??= new List<Ocs.AnswererWrapper>();
        cfg.AiProviders ??= new List<AiProviderConfig>();
        cfg.LanPeers ??= new List<LanPeerConfig>();
        cfg.Search ??= new SearchOptions();
        cfg.LocalBank ??= new LocalBankConfig();
        cfg.LanShare ??= new LanServerConfig();
        cfg.OcsBridge ??= new OcsBridgeConfig();
        cfg.Ocr ??= new OcrConfig();
        cfg.Network ??= new NetworkConfig();
        cfg.Window ??= new WindowConfig();
        cfg.Onboarding ??= new OnboardingConfig();
        cfg.WebSearch ??= new WebSearchConfig();
        cfg.AutoTag ??= new AutoTagConfig();
        cfg.Ocr.Umi ??= new UmiOcrConfig();
        cfg.Ocr.Vision ??= new VisionOcrConfig();
        cfg.Ocr.Custom ??= new CustomOcrConfig();
        cfg.LocalBank.AutoCacheKinds ??= new List<string> { "ai", "lan" };

        if (cfg.AiProviders.Count == 0)
            cfg.AiProviders.AddRange(AiProviderConfig.CreatePresets());

        if (string.IsNullOrWhiteSpace(cfg.DataDir))
            cfg.DataDir = string.Empty;
    }
}