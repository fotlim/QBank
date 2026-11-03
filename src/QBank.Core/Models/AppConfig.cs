using QBank.Core.Ocs;

namespace QBank.Core.Models;

/// <summary>应用配置（序列化到 config.json）。</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>数据目录；空 = 默认 %LOCALAPPDATA%\QBank。</summary>
    public string DataDir { get; set; } = string.Empty;

    /// <summary>主题：system / light / dark。</summary>
    public string Theme { get; set; } = "system";

    public SearchOptions Search { get; set; } = new();

    public LocalBankConfig LocalBank { get; set; } = new();

    public LanServerConfig LanShare { get; set; } = new();

    /// <summary>给 OCS 脚本用的对外题库服务（本机 HTTP 接口 + 生成的 OCS 配置）。</summary>
    public OcsBridgeConfig OcsBridge { get; set; } = new();

    public OcrConfig Ocr { get; set; } = new();

    /// <summary>OCS 格式题库配置（可整体导出粘回 OCS）。</summary>
    public List<AnswererWrapper> OcsSources { get; set; } = new();

    /// <summary>AI 服务商配置。</summary>
    public List<AiProviderConfig> AiProviders { get; set; } = new();

    /// <summary>手动添加 / 自动发现的局域网题库。</summary>
    public List<LanPeerConfig> LanPeers { get; set; } = new();

    /// <summary>网络出口设置（代理）。</summary>
    public NetworkConfig Network { get; set; } = new();

    /// <summary>窗口与托盘行为。</summary>
    public WindowConfig Window { get; set; } = new();

    /// <summary>首次使用引导的状态。</summary>
    public OnboardingConfig Onboarding { get; set; } = new();

    /// <summary>网页搜索（搜索引擎直连，命中可信答案时可省掉 AI 调用）。</summary>
    public WebSearchConfig WebSearch { get; set; } = new();

    /// <summary>自动标签分类。</summary>
    public AutoTagConfig AutoTag { get; set; } = new();

    /// <summary>首次运行写入演示与预设。</summary>
    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        cfg.AiProviders.AddRange(AiProviderConfig.CreatePresets());
        return cfg;
    }

    public IEnumerable<SourceDescriptor> DescribeSources()
    {
        foreach (var w in OcsSources)
            yield return new SourceDescriptor
            {
                Id = w.Id,
                Name = w.Name,
                Kind = SourceKind.Ocs,
                Enabled = w.Enabled,
                Priority = w.Priority,
                Detail = w.Method.ToUpperInvariant() + " " + w.Url,
            };

        foreach (var p in AiProviders)
            yield return new SourceDescriptor
            {
                Id = p.Id,
                Name = p.Name,
                Kind = SourceKind.Ai,
                Enabled = p.Enabled,
                Priority = p.Priority,
                Detail = p.ProviderKind + " · " + p.Model,
            };

        foreach (var peer in LanPeers)
            yield return new SourceDescriptor
            {
                Id = peer.Id,
                Name = peer.Name,
                Kind = SourceKind.LanShare,
                Enabled = peer.Enabled,
                Priority = peer.Priority,
                Detail = peer.BaseUrl,
            };
    }
}

public sealed class SearchOptions
{
    public bool UseCache { get; set; } = true;
    public bool UseLocalBank { get; set; } = true;
    public bool UseLanShare { get; set; } = true;
    public bool UseOcs { get; set; } = true;
    public bool UseAi { get; set; } = true;

    /// <summary>用搜索引擎在网页里找答案（命中可信答案时可跳过 AI，省 token）。</summary>
    public bool UseWeb { get; set; } = true;

    /// <summary>缓存直接命中的相似度阈值（0~1）。</summary>
    public double CacheMinScore { get; set; } = 0.72;

    /// <summary>自动写回本地题库的相似度阈值（0~1）。</summary>
    public double AutoCacheMinScore { get; set; } = 0.9;

    /// <summary>只有 AI 且置信度足够时才自动写回。</summary>
    public double AutoCacheMinAiConfidence { get; set; } = 0.7;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>并发请求上限。</summary>
    public int MaxParallel { get; set; } = 8;

    public bool RecordHistory { get; set; } = true;

    /// <summary>结果里相似度低于该值的不展示。</summary>
    public double MinDisplayScore { get; set; } = 0.25;

    public int DefaultLimit { get; set; } = 8;
}

public sealed class LocalBankConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>本地题库也参与搜题的相似度阈值。</summary>
    public double MinScore { get; set; } = 0.6;

    /// <summary>把 AI / 局域网命中结果自动缓存进本地题库。</summary>
    public bool AutoCache { get; set; } = true;

    /// <summary>自动缓存的来源白名单（"ai"、"lan"、"ocs"）。</summary>
    public List<string> AutoCacheKinds { get; set; } = new() { "ai", "lan" };
}

public sealed class LanServerConfig
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 18321;

    public int DiscoveryPort { get; set; } = 39991;

    /// <summary>共享口令；空 = 局域网内任何人可查。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>对外展示的名字，默认取机器名。</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>允许局域网访问（关闭则只监听 127.0.0.1）。</summary>
    public bool AllowLan { get; set; } = true;

    /// <summary>是否允许远端写入（默认只读，安全）。</summary>
    public bool AllowRemoteWrite { get; set; }

    /// <summary>单次查询最多返回多少条。</summary>
    public int MaxResultsPerQuery { get; set; } = 5;

    /// <summary>是否响应 UDP 局域网发现。</summary>
    public bool RespondToDiscovery { get; set; } = true;
}

/// <summary>
/// OCS 对接口：把本机的题库能力（本地题库 + 局域网 + AI + 在线题库）聚合成一个
/// OCS 能直接用的题库接口，并在设置页生成一段可粘贴进 OCS 的 AnswererWrapper 配置。
/// </summary>
public sealed class OcsBridgeConfig
{
    /// <summary>端口被占就自动往后挪，实际端口以运行时为准。</summary>
    public int Port { get; set; } = 18322;

    /// <summary>
    /// 允许局域网内的其它机器（别的电脑上的 OCS）调用；
    /// 关闭则只监听 127.0.0.1，只有本机的 OCS 脚本能连。
    /// </summary>
    public bool AllowLan { get; set; }

    /// <summary>访问口令；空 = 不校验。会写进生成的配置里。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>给 OCS 里的题库起什么名字。</summary>
    public string DisplayName { get; set; } = "题库工坊";

    // ---- 参与聚合并发给 OCS 的来源 ----

    public bool UseLocalBank { get; set; } = true;

    public bool UseLanShare { get; set; } = true;

    public bool UseAi { get; set; } = true;

    /// <summary>是否把 QBank 里已配置的 OCS 在线题库也转一手（默认关闭，避免绕圈）。</summary>
    public bool UseOcs { get; set; }

    /// <summary>最多返回几条候选给 OCS。</summary>
    public int MaxResults { get; set; } = 3;

    /// <summary>低于这个相似度的结果不返回给 OCS（0~1）。</summary>
    public double MinScore { get; set; } = 0.55;

    /// <summary>等 AI 结果的最长秒数；超时就用已有结果先回。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>是否把 OCS 传来的题目也记进搜题历史。</summary>
    public bool RecordHistory { get; set; } = true;

    /// <summary>
    /// 生成配置时用的主机地址。空 = 自动（本机用 127.0.0.1，开了局域网则用局域网 IP）。
    /// </summary>
    public string HostOverride { get; set; } = string.Empty;

    public string EffectiveHost => !string.IsNullOrWhiteSpace(HostOverride)
        ? HostOverride.Trim()
        : (AllowLan ? LanAddressGuess() : "127.0.0.1");

    /// <summary>挑一个非回环的 IPv4 地址；拿不到就回落 127.0.0.1。</summary>
    public static string LanAddressGuess()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var text = addr.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return text;
                }
            }
        }
        catch (Exception)
        {
            // 取不到网卡信息不致命
        }

        return "127.0.0.1";
    }
}

public sealed class LanPeerConfig
{    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "局域网题库";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 18321;
    public string Token { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public int QuestionCount { get; set; }
    public string? Version { get; set; }

    /// <summary>上次从这台机器同步（拉或推）成功的时间；增量同步用它当 since。</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>上次增量同步拉回/推出的题目数，仅供界面显示。</summary>
    public int LastSyncCount { get; set; }

    public string BaseUrl => $"http://{Host}:{Port}";
    public string DisplayName => $"{Name} ({Host}:{Port})";
}

public sealed class AiProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = "新模型";

    /// <summary>deepseek / siliconflow / bailian / openai / ollama / custom</summary>
    public string ProviderKind { get; set; } = "custom";

    /// <summary>OpenAI 兼容 base url，例如 https://api.deepseek.com/v1</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxTokens { get; set; } = 1024;

    /// <summary>发送 response_format=json_object 强制 JSON 输出。</summary>
    public bool UseJsonMode { get; set; } = true;

    public bool Enabled { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    public int Priority { get; set; }

    /// <summary>额外请求体参数（JSON 对象，会合并进请求），比如 {"enable_thinking": false}。</summary>
    public string ExtraParamsJson { get; set; } = string.Empty;

    /// <summary>覆盖默认答题提示词（留空用内置）。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>该模型支持图片输入（可用于 OCR）。</summary>
    public bool VisionCapable { get; set; }

    /// <summary>复制一份（字段都是值类型/不可变字符串，浅拷贝够用）。</summary>
    public AiProviderConfig Clone() => (AiProviderConfig)MemberwiseClone();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ProviderKind : Name;

    public static List<AiProviderConfig> CreatePresets() => new()
    {
        new AiProviderConfig
        {
            Name = "DeepSeek",
            ProviderKind = "deepseek",
            BaseUrl = "https://api.deepseek.com/v1",
            Model = "deepseek-chat",
            UseJsonMode = true,
            Priority = 10,
        },
        new AiProviderConfig
        {
            Name = "硅基流动 SiliconFlow",
            ProviderKind = "siliconflow",
            BaseUrl = "https://api.siliconflow.cn/v1",
            Model = "deepseek-ai/DeepSeek-V3",
            UseJsonMode = true,
            Priority = 20,
        },
        new AiProviderConfig
        {
            Name = "阿里云百炼 DashScope",
            ProviderKind = "bailian",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen-plus",
            UseJsonMode = true,
            Priority = 30,
        },
        new AiProviderConfig
        {
            Name = "OpenAI",
            ProviderKind = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            UseJsonMode = true,
            Priority = 40,
        },
        new AiProviderConfig
        {
            Name = "Ollama 本地",
            ProviderKind = "ollama",
            BaseUrl = "http://127.0.0.1:11434/v1",
            Model = "qwen3:8b",
            UseJsonMode = false,
            Temperature = 0.1,
            VisionCapable = false,
            Priority = 50,
        },
        new AiProviderConfig
        {
            Name = "自定义接口",
            ProviderKind = "custom",
            BaseUrl = "https://example.com/v1",
            Model = "your-model",
            UseJsonMode = false,
            Priority = 90,
        },
    };
}

public sealed class OcrConfig
{
    /// <summary>umi（Umi-OCR / PaddleOCR，默认）/ windows（系统内置）/ ollama（视觉模型）/ custom（自定义 HTTP）/ none</summary>
    public string Engine { get; set; } = "umi";

    /// <summary>Umi-OCR 相关设置。</summary>
    public UmiOcrConfig Umi { get; set; } = new();

    /// <summary>Windows OCR 语言，例如 zh-Hans-CN / zh-Hant-TW / en-US。</summary>
    public string WindowsLanguage { get; set; } = "zh-Hans-CN";

    /// <summary>Ollama 视觉模型使用的 AI 服务商 Id（留空则用 base url 直连）。</summary>
    public string OllamaProviderId { get; set; } = string.Empty;

    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434/v1";

    public string OllamaModel { get; set; } = "qwen2.5vl:7b";

    public CustomOcrConfig Custom { get; set; } = new();

    /// <summary>识别后自动按题型/选项切分（否则整段文本当题干）。</summary>
    public bool AutoSplitTitleOptions { get; set; } = true;
}

/// <summary>Umi-OCR 设置：优先走它的 HTTP 服务，没开就直接调 PaddleOCR-json.exe。</summary>
public sealed class UmiOcrConfig
{
    /// <summary>是否优先用 Umi-OCR 的 HTTP 服务（关掉就始终直连引擎）。</summary>
    public bool UseHttp { get; set; } = true;

    /// <summary>Umi-OCR HTTP 地址，默认本机 1224 端口。</summary>
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:1224";

    /// <summary>用户指定的 Umi-OCR 安装目录（留空自动探测常见路径）。</summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>直接指定 PaddleOCR-json.exe 路径（留空自动探测）。</summary>
    public string EngineExePath { get; set; } = string.Empty;

    /// <summary>语言配置，相对引擎目录，例如 models/config_chinese.txt。</summary>
    public string ConfigPath { get; set; } = "models/config_chinese.txt";

    /// <summary>语言短名，给 HTTP 接口的 ocr.language 用（留空则用 ConfigPath）。</summary>
    public string Language { get; set; } = string.Empty;
}

public sealed class CustomOcrConfig
{
    public string Url { get; set; } = string.Empty;

    public string Method { get; set; } = "post";

    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>请求体模板，支持占位符 {{image_base64}} 与 {{mime}}。</summary>
    public string BodyTemplate { get; set; } = "{\"image\":\"{{image_base64}}\"}";

    /// <summary>从响应 JSON 中取识别文本的点分路径，例如 data.text / result.0.text。</summary>
    public string ResultPath { get; set; } = "text";
}
/// <summary>网络出口设置：解决系统代理把请求送去没在跑的本地端口。</summary>
public sealed class NetworkConfig
{
    /// <summary>代理模式取值（避免各处手写字符串写错）。</summary>
    public const string ModeAuto = "auto";
    public const string ModeDirect = "direct";
    public const string ModeCustom = "custom";

    /// <summary>
    /// 代理模式：
    ///   auto   = 跟随系统代理（默认；Fiddler/Clash 开着就自动走）
    ///   direct = 直连，忽略系统代理（最常见的问题就是系统代理指向了没开的本地端口）
    ///   custom = 用下面填的代理地址
    /// </summary>
    public string ProxyMode { get; set; } = ModeAuto;

    /// <summary>custom 模式下的代理地址，例如 http://127.0.0.1:7890。</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>proxy 模式下不走代理的域名（分号分隔，支持 *.example.com）。</summary>
    public string BypassList { get; set; } = "localhost;127.0.0.1;*.local;10.*;192.168.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*";

    /// <summary>
    /// 「跟随系统代理」时，如果系统代理指向的本机端口根本没人监听，
    /// 就自动改走直连 —— 免得一条过期的注册表把整个软件废掉。
    /// </summary>
    public bool FallbackToDirectOnProxyFailure { get; set; } = true;

    /// <summary>构建 HttpClient 用的 WebProxy；返回 null 表示直连。</summary>
    public System.Net.IWebProxy? BuildProxy()
    {
        switch ((ProxyMode ?? "auto").Trim().ToLowerInvariant())
        {
            case "direct":
                return null;
            case "custom":
                if (string.IsNullOrWhiteSpace(ProxyUrl))
                    return null;
                return new System.Net.WebProxy(ProxyUrl.Trim()) { BypassList = ToBypassRegexes() };
            default:
                // auto：沿用系统代理（HttpClient 不显式设置时就是系统默认行为）
                return System.Net.Http.HttpClient.DefaultProxy;
        }
    }

    /// <summary>
    /// 把用户填的通配符（*.local、10.*、127.0.0.1）翻译成 WebProxy 要的正则。
    /// <para>
    /// ⚠️ <c>WebProxy.BypassList</c> 的元素是<b>正则表达式</b>，不是通配符 —— 直接把
    /// "*.local" 塞进去会在构造时抛 <c>RegexParseException: Quantifier '*' following nothing</c>，
    /// 让整个代理构建失败、连带所有请求报废。这个坑踩过一次，记住。
    /// </para>
    /// </summary>
    private string[] ToBypassRegexes()
    {
        var raw = (BypassList ?? string.Empty)
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>(raw.Length);
        foreach (var item in raw)
        {
            var regex = GlobToRegex(item);
            if (regex is null) continue;

            // 用户可能自己就填的正则（含模糊字面量），先验证一下，坏的直接丢掉，
            // 不能让一条笔误把整个网络出口搞崩。
            try
            {
                _ = new System.Text.RegularExpressions.Regex(regex);
                result.Add(regex);
            }
            catch (ArgumentException)
            {
                // 忽略这一条
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// 通配符 → 正则：<c>*</c> 换成 <c>.*</c>、<c>?</c> 换成 <c>.</c>，
    /// 其余字符转义。用户直接写正则（例如 <c>^10\.</c>）时原样保留。
    /// </summary>
    private static string? GlobToRegex(string pattern)
    {
        var p = pattern.Trim();
        if (p.Length == 0) return null;

        // 看起来已经是正则（有元字符且不是纯通配符），原样返回
        var looksLikeRegex = p.StartsWith('^') || p.EndsWith('$') ||
                             p.Contains("\\.") || p.Contains("(?") || p.Contains('|');
        if (looksLikeRegex) return p;

        var sb = new System.Text.StringBuilder("^");
        foreach (var ch in p)
        {
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
                    break;
            }
        }
        // 域名后缀（*.local）也要能匹配到 .local 本身和任意子域
        if (p.StartsWith("*."))
        {
            var bare = p[2..];
            sb.Clear();
            sb.Append("(^|.*\\.)").Append(System.Text.RegularExpressions.Regex.Escape(bare)).Append("$");
            return sb.ToString();
        }

        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>配置的一句话描述，界面上显示用。</summary>
    public string Describe() => (ProxyMode ?? "auto").Trim().ToLowerInvariant() switch
    {
        "direct" => "直连（忽略系统代理）",
        "custom" => string.IsNullOrWhiteSpace(ProxyUrl) ? "自定义代理（未填地址，按直连）" : "自定义代理 " + ProxyUrl.Trim(),
        _ => "跟随系统代理",
    };
}

/// <summary>
/// 窗口与托盘行为。
///
/// 关窗口默认只是隐藏到托盘 —— 程序本身还在跑局域网共享和 OCS 接口服务，
/// 关掉窗口就把服务停了会让别人的 OCS 脚本突然失效。
/// </summary>
public sealed class WindowConfig
{
    /// <summary>是否启用托盘图标。关掉就退回「关窗口 = 退出」的老行为。</summary>
    public bool EnableTray { get; set; } = true;

    /// <summary>关窗口时最小化到托盘而不是退出。EnableTray 为 false 时无效。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>最小化窗口时也收进托盘（不进任务栏）。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>第一次收进托盘时弹个气泡说明「程序还在后台跑」，只弹一次。</summary>
    public bool ShowTrayHint { get; set; } = true;

    /// <summary>记住上次退出时的窗口大小与位置。</summary>
    public bool RememberWindowPlacement { get; set; } = true;

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 840;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;

    /// <summary>是否为最大化启动/退出。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>一句话描述当前托盘策略，界面上显示用。</summary>
    public string Describe() => !EnableTray
        ? "不使用托盘（关窗口即退出）"
        : CloseToTray
            ? "收进系统托盘，后台继续跑"
            : "托盘图标常驻，关窗口仍会退出";

    /// <summary>把记下来的窗口尺寸夹到合理范围，防止配置被改坏后窗口不可见。</summary>
    public (int Width, int Height) SafeSize()
    {
        var w = WindowWidth < 720 ? 1280 : Math.Min(WindowWidth, 6000);
        var h = WindowHeight < 480 ? 840 : Math.Min(WindowHeight, 4000);
        return (w, h);
    }
}

/// <summary>首次使用引导的状态。走过一次就不再自动弹。</summary>
public sealed class OnboardingConfig
{
    /// <summary>是否已经看过引导（点了完成或跳过）。</summary>
    public bool Done { get; set; }

    /// <summary>走完引导的时间。</summary>
    public DateTime? DoneAt { get; set; }
}


/// <summary>网页搜索（用搜索引擎在网页里找答案）。</summary>
public sealed class WebSearchConfig
{
    /// <summary>总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>搜索引擎：bing / sogou / baidu / custom。</summary>
    public string Engine { get; set; } = "so360";

    /// <summary>自定义搜索引擎的地址模板，用 {q} 表示查询词。</summary>
    public string CustomUrlTemplate { get; set; } = "";

    /// <summary>抓取多少条搜索结果来判分。</summary>
    public int MaxResults { get; set; } = 8;

    /// <summary>判定「可信答案」的最低分；低于它不算命中，也就不会跳过 AI。</summary>
    public double MinScore { get; set; } = 0.62;

    /// <summary>网页里找到可信答案后跳过 AI 调用（省 token 的关键开关）。</summary>
    public bool SkipAiOnHit { get; set; } = true;

    /// <summary>查询词最多取题干前多少字。</summary>
    public int QueryChars { get; set; } = 48;

    /// <summary>是否把选项也拼进查询词。</summary>
    public bool QueryWithOptions { get; set; }

    public int TimeoutSeconds { get; set; } = 12;

    /// <summary>每条结果摘要最多取多少字参与判分。</summary>
    public int SnippetChars { get; set; } = 600;

    public string Describe()
    {
        if (!Enabled) return "已关闭";
        var engine = Engine switch
        {
            "bing" => "必应",
            "sogou" => "搜狗",
            "baidu" => "百度",
            "custom" => string.IsNullOrWhiteSpace(CustomUrlTemplate) ? "自定义（未填地址）" : "自定义",
            _ => Engine,
        };
        return $"用{engine}找答案" + (SkipAiOnHit ? " · 命中后就跳过 AI" : "");
    }
}

/// <summary>自动标签分类。</summary>
public sealed class AutoTagConfig
{
    /// <summary>新增/导入题目时自动打标签。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>打标签用的引擎：rule（本地词表，零成本）/ ai（调用模型）/ local（调用本机 Ollama）。</summary>
    public string Engine { get; set; } = "rule";

    /// <summary>ai/local 引擎用哪个服务商；留空表示用已启用的第一个。</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>一道题最多几个标签。</summary>
    public int MaxTags { get; set; } = 4;

    /// <summary>已经有标签的题目是否也重新打（关掉就只补空的）。</summary>
    public bool Overwrite { get; set; } = false;

    /// <summary>自定义提示词；留空用内置的。</summary>
    public string Prompt { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 30;

    public string Describe()
    {
        if (!Enabled) return "已关闭";
        var engine = Engine switch
        {
            "ai" => "用模型打标签",
            "local" => "用本机模型打标签",
            _ => "按词表打标签",
        };
        return engine + (Overwrite ? " · 覆盖已有标签" : " · 只补空标签");
    }
}
