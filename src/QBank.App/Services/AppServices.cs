using QBank.Core.Ai;
using QBank.Core.Bridge;
using QBank.Core.Lan;
using QBank.Core.Models;
using QBank.Core.Ocr;
using QBank.Core.Ocs;
using QBank.Core.Search;
using QBank.Core.Storage;

namespace QBank.App.Services;

/// <summary>应用级服务容器：配置、题库、历史、局域网、搜索、OCR 都从这里拿。</summary>
public sealed class AppServices
{
    private static readonly Lazy<AppServices> Lazy = new(() => new AppServices());
    public static AppServices Current => Lazy.Value;

    private readonly List<string> _log = new();
    private readonly object _logLock = new();

    private AppServices() { }

    public ConfigStore Config { get; private set; } = null!;
    public LocalBank Bank { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;
    public LanShareService Lan { get; private set; } = null!;
    public SearchService Search { get; private set; } = null!;
    public OcrService Ocr { get; private set; } = null!;
    public AiModelCatalog ModelCatalog { get; private set; } = null!;
    public OcsBridgeService OcsBridge { get; private set; } = null!;

    public bool Initialized { get; private set; }

    public void Initialize()
    {
        if (Initialized) return;

        try
        {
            AppPaths.ProbeLog = msg => App.Trace(msg);
            Config = new ConfigStore();
            Config.Load();
        }
        catch (Exception ex)
        {
            Log("配置加载失败：" + ex.Message);

            // 主目录不可写时换到临时目录，保证程序能起来
            var fallbackDir = Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName);
            try
            {
                Config = new ConfigStore(fallbackDir);
                Config.Load();
                Log("已回落到临时数据目录：" + fallbackDir);
            }
            catch (Exception second)
            {
                Log("临时目录也不可用，使用纯内存配置：" + second.Message);
                Config = ConfigStore.CreateInMemory();
            }
        }

        if (Config.FallbackNote is not null) Log(Config.FallbackNote);

        try
        {
            Bank = new LocalBank(Config.DatabaseFile);
        }
        catch (Exception ex)
        {
            Log("本地题库打开失败，改用临时库：" + ex.Message);
            Bank = new LocalBank(Path.Combine(Path.GetTempPath(), "qbank-temp.db"));
        }

        try
        {
            History = new HistoryStore(Config.DatabaseFile);
        }
        catch (Exception ex)
        {
            Log("历史库打开失败：" + ex.Message);
            History = new HistoryStore(Path.Combine(Path.GetTempPath(), "qbank-temp.db"));
        }

        Lan = new LanShareService(Bank);
        Search = new SearchService(Config, Bank, History, Lan);
        Ocr = new OcrService(() => Config.Config);
        ModelCatalog = new AiModelCatalog();
        OcsBridge = new OcsBridgeService();

        try
        {
            Lan.ApplyConfig(Config.Config);
        }
        catch (Exception ex)
        {
            Log("局域网服务启动失败：" + ex.Message);
        }

        ApplyOcsBridge();

        Initialized = true;
        Log("应用已启动，数据目录：" + Config.DataDir);
    }

    public void Log(string message)
    {
        lock (_logLock)
        {
            _log.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
    }

    public IReadOnlyList<string> Logs
    {
        get { lock (_logLock) return _log.ToList(); }
    }

    /// <summary>保存配置并让局域网服务、OCS 对接口跟着变更。</summary>
    public void SaveConfig()
    {
        try
        {
            Config.Save();
            Lan.ApplyConfig(Config.Config);
            ApplyOcsBridge();
        }
        catch (Exception ex)
        {
            Log("保存配置失败：" + ex.Message);
        }
    }

    /// <summary>按配置启动/重启给 OCS 用的对接口。</summary>
    public void ApplyOcsBridge()
    {
        try
        {
            OcsBridge.Log -= OnOcsBridgeLog;
            OcsBridge.Log += OnOcsBridgeLog;
            OcsBridge.Apply(Config.Config.OcsBridge, (req, ct) => Search.SearchAsync(req, ct));
        }
        catch (Exception ex)
        {
            Log("OCS 对接口启动失败：" + ex.Message);
        }
    }

    private void OnOcsBridgeLog(object? sender, string message) => Log(message);
}
