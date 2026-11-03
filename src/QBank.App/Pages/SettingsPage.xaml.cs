using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QBank.App.Controls;
using QBank.App.Services;
using QBank.Core.Ai;
using QBank.Core.Models;
using QBank.Core.Ocr;
using QBank.Core.Ocs;
using QBank.Core.Search;
using QBank.Core.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace QBank.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly List<ProviderCard> _cards = new();
    private bool _loading;

    private List<SettingsGroup> _groups = new();
    private bool _navSuppressed;

    /// <summary>左侧分类的一项。</summary>
    private sealed class SettingsGroup
    {
        public string Key { get; set; } = string.Empty;
        public string Glyph { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
    }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_cards.Count > 0) return; // 页面被缓存，避免重复加载
        BuildPresetMenu();
        BuildGroupNav();
        ReloadFromConfig();
        ApplyDebugScroll();

        // 从引导跳进来时指定了分类，等控件都建好了再切
        if (_pendingGroup is not null)
        {
            ShowGroup(_pendingGroup);
            _pendingGroup = null;
        }
    }

    private string? _pendingGroup;

    /// <summary>外部（上手引导、快捷跳转）指定要显示哪一类。</summary>
    public void FocusGroup(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var normalized = key.Trim().ToLowerInvariant();
        if (_groups.Count == 0)
        {
            _pendingGroup = normalized; // 页面还没加载完，存着等 OnLoaded
            return;
        }

        ShowGroup(normalized);
    }

    private void OnReplayOnboarding(object sender, RoutedEventArgs e)
    {
        (App.MainAppWindow as MainWindow)?.ReplayOnboarding();
    }

    /// <summary>
    /// 建左侧分类。设置项一多，一列滚到底就找不到东西了，所以按用途分成五类，
    /// 并且按「第一次该怎么配」的顺序编号，从上往下过一遍就配完了。
    /// </summary>
    private void BuildGroupNav()
    {
        _groups = new List<SettingsGroup>
        {
            new()
            {
                Key = "models", Glyph = "\uE774", Title = "1 · 模型与网络",
                Subtitle = "配好一个模型就能查题；连不上也在这里查",
            },
            new()
            {
                Key = "sources", Glyph = "\uE8F1", Title = "2 · 题库来源",
                Subtitle = "别人的题库搬进来，自己的题库给出去",
            },
            new()
            {
                Key = "ocr", Glyph = "\uE91B", Title = "3 · 图片识题",
                Subtitle = "截图或选图片，自动认成题干",
            },
            new()
            {
                Key = "prefs", Glyph = "\uE713", Title = "4 · 使用偏好",
                Subtitle = "搜题行为、窗口与托盘",
            },
            new()
            {
                Key = "data", Glyph = "\uE8B7", Title = "5 · 数据与备份",
                Subtitle = "换机器、重装系统不用重填",
            },
        };

        GroupNav.ItemsSource = _groups;
        GroupNav.SelectedIndex = 0;
    }

    /// <summary>切换右侧显示的分类。</summary>
    private void ShowGroup(string key)
    {
        GroupModels.Visibility = key == "models" ? Visibility.Visible : Visibility.Collapsed;
        GroupSources.Visibility = key == "sources" ? Visibility.Visible : Visibility.Collapsed;
        GroupOcr.Visibility = key == "ocr" ? Visibility.Visible : Visibility.Collapsed;
        GroupPrefs.Visibility = key == "prefs" ? Visibility.Visible : Visibility.Collapsed;
        GroupData.Visibility = key == "data" ? Visibility.Visible : Visibility.Collapsed;

        // 同步左导航选中（若这次是导航触发的就不用再设回去，避免递归）
        var idx = _groups.FindIndex(g => g.Key == key);
        if (idx >= 0 && GroupNav.SelectedIndex != idx && !_navSuppressed)
        {
            _navSuppressed = true;
            GroupNav.SelectedIndex = idx;
            _navSuppressed = false;
        }

        // 换了一组，回到顶部，否则会停在上次的滚动位置
        ContentScroll.ChangeView(null, 0, null, true);
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSuppressed) return;
        if (GroupNav.SelectedItem is SettingsGroup group) ShowGroup(group.Key);
    }

    /// <summary>
    /// 调试用：环境变量 QBANK_SCROLL_TO=ocs 时，加载后自动切到对应分类并滚到对应卡片，
    /// 方便截图核验（自动化截图没法可靠地模拟滚轮）。
    /// </summary>
    private void ApplyDebugScroll()
    {
        var target = Environment.GetEnvironmentVariable("QBANK_SCROLL_TO");
        if (string.IsNullOrWhiteSpace(target)) return;

        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            switch (target.Trim().ToLowerInvariant())
            {
                case "models":
                case "ai":
                    ShowGroup("models");
                    break;
                case "ocs-bridge":
                case "ocsbridge":
                case "bridge":
                    ShowGroup("sources");
                    OcsBridgeCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "ocr":
                    ShowGroup("ocr");
                    break;
                case "search":
                case "searchopts":
                    ShowGroup("prefs");
                    SearchOptionsCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "prefs":
                case "preferences":
                    ShowGroup("prefs");
                    break;
                case "data":
                    ShowGroup("data");
                    DataCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "tray":
                case "window":
                    ShowGroup("prefs");
                    WindowCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "backup":
                case "restore":
                    ShowGroup("data");
                    BackupCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "net":
                case "network":
                case "proxy":
                    ShowGroup("models");
                    NetworkCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "web":
                case "websearch":
                    ShowGroup("sources");
                    WebSearchCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "tag":
                case "autotag":
                    ShowGroup("prefs");
                    AutoTagCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "ocs":
                    ShowGroup("sources");
                    OcsListCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                default:
                    if (double.TryParse(target, out var offset))
                    {
                        ContentScroll.ChangeView(null, offset, null, true);
                    }
                    break;
            }
        });
    }

    private void BuildPresetMenu()
    {
        PresetFlyout.Items.Clear();
        foreach (var preset in AiProviderConfig.CreatePresets())
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{preset.Name}（{preset.Model}）",
                Tag = preset.ProviderKind,
            };
            item.Click += (_, _) => AddProvider(preset.ProviderKind);
            PresetFlyout.Items.Add(item);
        }

        PresetFlyout.Items.Add(new MenuFlyoutSeparator());
        var blank = new MenuFlyoutItem { Text = "空白服务商" };
        blank.Click += (_, _) => AddProvider("custom");
        PresetFlyout.Items.Add(blank);
    }

    private void ReloadFromConfig()
    {
        _loading = true;
        try
        {
            var cfg = AppServices.Current.Config.Config;

            ProviderList.Children.Clear();
            _cards.Clear();

            foreach (var provider in cfg.AiProviders)
                AddProviderCard(provider);

            ProviderEmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // OCS 题库
            RebuildOcsList();

            // OCR
            SelectOcrEngine(cfg.Ocr.Engine);
            UmiUseHttp.IsChecked = cfg.Ocr.Umi.UseHttp;
            UmiApiUrl.Text = cfg.Ocr.Umi.ApiBaseUrl;
            UmiInstallDir.Text = cfg.Ocr.Umi.InstallDirectory;
            UmiExePath.Text = cfg.Ocr.Umi.EngineExePath;
            UmiConfigPath.Text = cfg.Ocr.Umi.ConfigPath;
            OllamaBaseUrl.Text = cfg.Ocr.OllamaBaseUrl;
            OllamaModelBox.Text = cfg.Ocr.OllamaModel;
            CustomOcrUrl.Text = cfg.Ocr.Custom.Url;
            CustomOcrBody.Text = cfg.Ocr.Custom.BodyTemplate;
            CustomOcrPath.Text = cfg.Ocr.Custom.ResultPath;
            OcrSplitBox.IsChecked = cfg.Ocr.AutoSplitTitleOptions;

            FillWindowsLanguages(cfg.Ocr.WindowsLanguage);
            UpdateOcrPanels();
            UpdateOcrDescribe();

            // 搜索选项
            TimeoutBox.Value = cfg.Search.TimeoutSeconds;
            ParallelBox.Value = cfg.Search.MaxParallel;
            CacheScoreBox.Value = cfg.Search.CacheMinScore;
            AutoCacheBox.Value = cfg.Search.AutoCacheMinScore;
            AutoCacheSwitch.IsChecked = cfg.LocalBank.AutoCache;
            RecordHistorySwitch.IsChecked = cfg.Search.RecordHistory;

            // OCS 对接口
            var bridge = cfg.OcsBridge ??= new OcsBridgeConfig();
            OcsPortBox.Value = bridge.Port;
            OcsNameBox.Text = bridge.DisplayName;
            OcsTokenBox.Password = bridge.Token ?? string.Empty;
            OcsMaxResultsBox.Value = bridge.MaxResults;
            OcsMinScoreBox.Value = bridge.MinScore;
            OcsTimeoutBox.Value = bridge.TimeoutSeconds;
            OcsEnableLanBox.IsChecked = bridge.AllowLan;
            OcsUseLocalBox.IsChecked = bridge.UseLocalBank;
            OcsUseLanBox.IsChecked = bridge.UseLanShare;
            OcsUseAiBox.IsChecked = bridge.UseAi;
            OcsUseOcsBox.IsChecked = bridge.UseOcs;
            OcsRecordHistoryBox.IsChecked = bridge.RecordHistory;
            UpdateOcsBridgeStatus();

            DataDirText.Text = "数据目录：" + AppServices.Current.Config.DataDir;

            // 网络出口
            var net = cfg.Network ??= new NetworkConfig();
            SelectProxyMode(net.ProxyMode);
            ProxyUrlBox.Text = net.ProxyUrl ?? string.Empty;
            ProxyBypassBox.Text = net.BypassList ?? string.Empty;
            ProxyFallbackBox.IsChecked = net.FallbackToDirectOnProxyFailure;
            UpdateProxyModeUi();
            UpdateNetworkStatus();

            // 窗口与托盘
            var win = cfg.Window ??= new WindowConfig();
            TrayEnableBox.IsChecked = win.EnableTray;
            TrayCloseBox.IsChecked = win.CloseToTray;
            TrayMinimizeBox.IsChecked = win.MinimizeToTray;
            TrayHintBox.IsChecked = win.ShowTrayHint;
            RememberPlacementBox.IsChecked = win.RememberWindowPlacement;
            UpdateWindowTrayUi();

            // 备份与恢复：范围默认「全部设置」，列表重读一次
            if (BackupScopeBox.SelectedIndex < 0) BackupScopeBox.SelectedIndex = 0;
            ReloadBackupList();

            RefreshStats();

            // 网页搜索
            var web = cfg.WebSearch ??= new WebSearchConfig();
            WebEnableBox.IsChecked = web.Enabled;
            SelectWebEngine(web.Engine);
            WebMaxBox.Value = web.MaxResults;
            WebMinScoreBox.Value = web.MinScore;
            WebQueryCharsBox.Value = web.QueryChars;
            WebTimeoutBox.Value = web.TimeoutSeconds;
            WebSkipAiBox.IsChecked = web.SkipAiOnHit;
            WebWithOptionsBox.IsChecked = web.QueryWithOptions;
            WebCustomBox.Text = web.CustomUrlTemplate ?? string.Empty;
            UpdateWebEngineUi();

            // 自动标签
            var tag = cfg.AutoTag ??= new AutoTagConfig();
            AutoTagEnableBox.IsChecked = tag.Enabled;
            AutoTagOverwriteBox.IsChecked = tag.Overwrite;
            AutoTagMaxBox.Value = tag.MaxTags;
            SelectTagEngine(tag.Engine);
            FillTagProviders(cfg, tag.ProviderId);
            UpdateAutoTagUi();
        }
        finally
        {
            _loading = false;
        }
    }

    // ============ 网页搜索 ============

    private void FillWebEngineCombo()
    {
        if (WebEngineBox.Items.Count > 0) return;
        foreach (var engine in WebSearchEngines.All)
            WebEngineBox.Items.Add(new ComboBoxItem { Content = WebSearchEngines.DisplayName(engine), Tag = engine });
    }

    private void SelectWebEngine(string? engine)
    {
        FillWebEngineCombo();
        var want = string.IsNullOrWhiteSpace(engine) ? WebSearchEngines.Default : engine;
        for (var i = 0; i < WebEngineBox.Items.Count; i++)
        {
            if (WebEngineBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            {
                WebEngineBox.SelectedIndex = i;
                return;
            }
        }
        WebEngineBox.SelectedIndex = 0;
    }

    private string CurrentWebEngine() =>
        (WebEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? WebSearchEngines.Default;

    private void OnWebEngineChanged(object sender, SelectionChangedEventArgs e) => UpdateWebEngineUi();

    private void OnWebOptionChanged(object sender, RoutedEventArgs e) => UpdateWebEngineUi();

    private void UpdateWebEngineUi()
    {
        if (WebCustomBox is null) return;
        var engine = CurrentWebEngine();
        WebCustomBox.Visibility = engine == WebSearchEngines.Custom ? Visibility.Visible : Visibility.Collapsed;

        var limit = WebSearchEngines.DescribeLimitation(engine);
        var curl = WebSearchSource.FindCurl();
        var suffix = curl is null ? "（本机没有 curl.exe，个别站点可能抓不到）" : string.Empty;
        if (WebStatusText.Text.Length == 0 || !WebStatusText.Text.StartsWith("正在", StringComparison.Ordinal))
            WebStatusText.Text = limit + suffix;
    }

    private void ApplyWebToConfig()
    {
        var web = AppServices.Current.Config.Config.WebSearch ??= new WebSearchConfig();
        web.Enabled = WebEnableBox.IsChecked == true;
        web.Engine = CurrentWebEngine();
        web.CustomUrlTemplate = WebCustomBox.Text?.Trim() ?? string.Empty;
        web.MaxResults = double.IsNaN(WebMaxBox.Value) ? 8 : (int)WebMaxBox.Value;
        web.MinScore = double.IsNaN(WebMinScoreBox.Value) ? 0.62 : WebMinScoreBox.Value;
        web.QueryChars = double.IsNaN(WebQueryCharsBox.Value) ? 48 : (int)WebQueryCharsBox.Value;
        web.TimeoutSeconds = double.IsNaN(WebTimeoutBox.Value) ? 12 : (int)WebTimeoutBox.Value;
        web.SkipAiOnHit = WebSkipAiBox.IsChecked == true;
        web.QueryWithOptions = WebWithOptionsBox.IsChecked == true;
    }

    /// <summary>拿一道示例题真去搜一次，把过程和结论都摊开给用户看。</summary>
    private async void OnTestWebSearch(object sender, RoutedEventArgs e)
    {
        ApplyWebToConfig();
        AppServices.Current.SaveConfig();

        var cfg = AppServices.Current.Config.Config;
        var engineName = WebSearchEngines.DisplayName(cfg.WebSearch.Engine);

        var request = new SearchRequest
        {
            Title = "中国的首都是哪座城市？",
            Options = "A. 上海\nB. 北京\nC. 广州\nD. 深圳",
            Type = QuestionType.Single,
        };

        WebTestButton.IsEnabled = false;
        WebStatusText.Text = $"正在用{engineName}搜「中国的首都是哪座城市？」…";
        try
        {
            var outcome = await AppServices.Current.Web.SearchAsync(request, cfg);

            if (outcome.Hits.Count > 0)
            {
                var hit = outcome.Hits[0];
                WebStatusText.Text = $"✅ 用{engineName}搜到了：{hit.Answer}（把握 {hit.Score:P0}）";
                await ShowDialogAsync("网页搜索测试",
                    $"引擎：{engineName}\n耗时：{outcome.ElapsedMs}ms\n\n题干：中国的首都是哪座城市？\nA. 上海　B. 北京　C. 广州　D. 深圳\n\n" +
                    $"判定答案：{hit.Answer}\n把握：{hit.Score:P0}（可信线 {cfg.WebSearch.MinScore:P0}）\n\n理由：\n{hit.Note}",
                    wide: true);
                return;
            }

            var detail = outcome.Notices.Count > 0
                ? string.Join("\n· ", outcome.Notices)
                : "没有解析出可用的结果。";
            WebStatusText.Text = $"⚠️ 这次没搜到可信答案（{outcome.ElapsedMs}ms）";
            await ShowDialogAsync("网页搜索测试",
                $"引擎：{engineName}\n耗时：{outcome.ElapsedMs}ms\n\n这次没搜到可信答案：\n· {detail}\n\n" +
                "可以换个搜索引擎再试（搜狗 / 360 / 必应 的可用性随网络环境变），或者把可信线调低一点。",
                wide: true);
        }
        catch (Exception ex)
        {
            WebStatusText.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            WebTestButton.IsEnabled = true;
        }
    }

    // ============ 自动标签 ============

    private static (string Key, string Title, string Hint)[] TagEngines => new[]
    {
        ("rule", "内置词表（离线 · 零成本）", "按学科关键词和题型判断，不花 token"),
        ("local", "本机模型（Ollama 等）", "用本机跑的小模型判断，不花云端 token"),
        ("ai", "云端模型", "用你配置的 AI 服务判断，最灵活"),
    };

    private void FillTagEngineCombo()
    {
        if (AutoTagEngineBox.Items.Count > 0) return;
        foreach (var (key, title, hint) in TagEngines)
            AutoTagEngineBox.Items.Add(new ComboBoxItem { Content = title, Tag = key });
    }

    private void SelectTagEngine(string? engine)
    {
        FillTagEngineCombo();
        var want = string.IsNullOrWhiteSpace(engine) ? "rule" : engine;
        for (var i = 0; i < AutoTagEngineBox.Items.Count; i++)
        {
            if (AutoTagEngineBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            {
                AutoTagEngineBox.SelectedIndex = i;
                return;
            }
        }
        AutoTagEngineBox.SelectedIndex = 0;
    }

    private string CurrentTagEngine() =>
        (AutoTagEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "rule";

    private void FillTagProviders(AppConfig cfg, string? pinned)
    {
        AutoTagProviderBox.Items.Clear();
        foreach (var provider in cfg.AiProviders)
        {
            AutoTagProviderBox.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(provider.Name) ? provider.ProviderKind : provider.Name,
                Tag = provider.Id,
            });
        }

        if (AutoTagProviderBox.Items.Count == 0)
        {
            AutoTagProviderBox.Items.Add(new ComboBoxItem { Content = "（还没配模型）", Tag = string.Empty });
        }

        var index = 0;
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            for (var i = 0; i < AutoTagProviderBox.Items.Count; i++)
            {
                if (AutoTagProviderBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, pinned, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
        }
        AutoTagProviderBox.SelectedIndex = index;
    }

    private void OnAutoTagEngineChanged(object sender, SelectionChangedEventArgs e) => UpdateAutoTagUi();

    private void UpdateAutoTagUi()
    {
        if (AutoTagProviderBox is null) return;
        var engine = CurrentTagEngine();
        AutoTagProviderBox.Visibility = engine is "ai" or "local" ? Visibility.Visible : Visibility.Collapsed;
        AutoTagButton.IsEnabled = AutoTagEnableBox.IsChecked == true;
    }

    private void ApplyAutoTagToConfig()
    {
        var tag = AppServices.Current.Config.Config.AutoTag ??= new AutoTagConfig();
        tag.Enabled = AutoTagEnableBox.IsChecked == true;
        tag.Overwrite = AutoTagOverwriteBox.IsChecked == true;
        tag.Engine = CurrentTagEngine();
        tag.MaxTags = double.IsNaN(AutoTagMaxBox.Value) ? 4 : (int)AutoTagMaxBox.Value;
        tag.ProviderId = (AutoTagProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }

    private async void OnAutoTagBatch(object sender, RoutedEventArgs e)
    {
        ApplyAutoTagToConfig();
        AppServices.Current.SaveConfig();

        var cfg = AppServices.Current.Config.Config;
        var bank = AppServices.Current.Bank;

        List<Question> pending;
        try
        {
            pending = cfg.AutoTag.Overwrite
                ? bank.Query(null, null, 0, 200)
                : bank.QueryUntagged(200);
        }
        catch (Exception ex)
        {
            AutoTagStatusText.Text = "读取题库失败：" + ex.Message;
            return;
        }

        if (pending.Count == 0)
        {
            AutoTagStatusText.Text = cfg.AutoTag.Overwrite
                ? "题库里还没有题目。"
                : "所有题目都有标签了，没有要处理的。";
            return;
        }

        AutoTagButton.IsEnabled = false;
        AutoTagStatusText.Text = $"正在给 {pending.Count} 道题打标签…";

        try
        {
            var known = bank.AllTags();
            var (tagged, skipped, failed, errors) = await AppServices.Current.Tagger.TagManyAsync(
                pending, cfg.AutoTag, cfg, known);

            bank.SaveTags(pending.Where(q => !string.IsNullOrWhiteSpace(q.Tags)).ToList());

            AutoTagStatusText.Text = $"完成：打上标签 {tagged} 道，跳过 {skipped} 道，失败 {failed} 道。";
            if (errors.Count > 0) AutoTagStatusText.Text += " 例：" + errors[0];
            RefreshStats();
        }
        catch (Exception ex)
        {
            AutoTagStatusText.Text = "打标签失败：" + ex.Message;
        }
        finally
        {
            UpdateAutoTagUi();
        }
    }

    private void AddProvider(string kind)
    {
        var preset = AiProviderConfig.CreatePresets()
            .FirstOrDefault(p => string.Equals(p.ProviderKind, kind, StringComparison.OrdinalIgnoreCase))
            ?? new AiProviderConfig { ProviderKind = kind, Name = "新模型" };

        var clone = new AiProviderConfig
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = preset.Name,
            ProviderKind = preset.ProviderKind,
            BaseUrl = preset.BaseUrl,
            Model = preset.Model,
            UseJsonMode = preset.UseJsonMode,
            Temperature = preset.Temperature,
            MaxTokens = preset.MaxTokens,
            TimeoutSeconds = preset.TimeoutSeconds,
            Priority = preset.Priority,
            Enabled = true,
        };

        AppServices.Current.Config.Config.AiProviders.Add(clone);
        AddProviderCard(clone);
        ProviderEmptyHint.Visibility = Visibility.Collapsed;
    }

    private void AddProviderCard(AiProviderConfig provider)
    {
        var card = new ProviderCard(provider);
        card.Deleted += (_, _) =>
        {
            ProviderList.Children.Remove(card);
            _cards.Remove(card);
            AppServices.Current.Config.Config.AiProviders.Remove(provider);
            ProviderEmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        card.Edited += (_, _) => { };
        _cards.Add(card);
        ProviderList.Children.Add(card);
    }

    private void OnAddProvider(object sender, RoutedEventArgs e)
    {
        // 按钮的 Flyout 会自动打开；这里不用做事
    }

    // ── OCS 题库 ──────────────────────────────────────────

    private void RebuildOcsList()
    {
        var cfg = AppServices.Current.Config.Config;
        OcsList.Children.Clear();

        foreach (var wrapper in cfg.OcsSources)
        {
            var w = wrapper;
            var border = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock { Text = w.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            info.Children.Add(new TextBlock
            {
                Text = w.DisplayUrl,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var enable = new CheckBox { Content = "启用", IsChecked = w.Enabled, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            enable.Checked += (_, _) => w.Enabled = true;
            enable.Unchecked += (_, _) => w.Enabled = false;
            Grid.SetColumn(enable, 1);
            grid.Children.Add(enable);

            var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) =>
            {
                cfg.OcsSources.Remove(w);
                RebuildOcsList();
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            border.Child = grid;
            OcsList.Children.Add(border);
        }

        OcsEmptyHint.Visibility = cfg.OcsSources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnImportOcs(object sender, RoutedEventArgs e)
    {
        var text = (OcsImportBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            await ShowDialogAsync("没有内容", "请先粘贴题库配置（JSON / 链接 / Base64）。");
            return;
        }

        try
        {
            var wrappers = await AnswerWrapperParser.FromAsync(text, QBank.Core.HttpClients.Shared);
            if (wrappers.Count == 0)
            {
                await ShowDialogAsync("导入失败", "解析出来是空的，检查配置内容。");
                return;
            }

            var cfg = AppServices.Current.Config.Config;
            var added = 0;
            foreach (var w in wrappers)
            {
                if (cfg.OcsSources.Any(x => string.Equals(x.Url, w.Url, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(x.Name, w.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                cfg.OcsSources.Add(w);
                added++;
            }

            AppServices.Current.SaveConfig();
            RebuildOcsList();
            OcsImportBox.Text = string.Empty;
            HeaderHint.Text = $"已导入 {added} 个题库（共 {cfg.OcsSources.Count} 个）";
        }
        catch (OcsConfigException ex)
        {
            await ShowDialogAsync("配置格式有误", ex.Message);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导入失败", ex.Message);
        }
    }

    private async void OnPasteOcsClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                await ShowDialogAsync("剪贴板里没有文字", "先把题库配置复制到剪贴板。");
                return;
            }
            OcsImportBox.Text = await content.GetTextAsync();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("读取剪贴板失败", ex.Message);
        }
    }

    private async void OnExportOcs(object sender, RoutedEventArgs e)
    {
        var cfg = AppServices.Current.Config.Config;
        if (cfg.OcsSources.Count == 0)
        {
            await ShowDialogAsync("没有题库", "先导入至少一个题库再导出。");
            return;
        }

        var json = AnswererWrapper.ToOcsJson(cfg.OcsSources);
        var package = new DataPackage();
        package.SetText(json);
        try { Clipboard.SetContent(package); } catch (Exception) { }

        await ShowDialogAsync("已复制到剪贴板", json.Length > 1200 ? json[..1200] + "…" : json);
    }

    // ── OCR ──────────────────────────────────────────────

    private void SelectOcrEngine(string? engine)
    {
        foreach (var item in OcrEngineCombo.Items)
        {
            if (item is ComboBoxItem ci &&
                string.Equals(ci.Tag as string, engine, StringComparison.OrdinalIgnoreCase))
            {
                OcrEngineCombo.SelectedItem = ci;
                return;
            }
        }
        OcrEngineCombo.SelectedIndex = 0;
    }

    private string CurrentOcrEngine() => (OcrEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "umi";

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateOcrPanels();
        UpdateOcrDescribe();
    }

    private void UpdateOcrPanels()
    {
        var engine = CurrentOcrEngine();
        UmiPanel.Visibility = engine == "umi" ? Visibility.Visible : Visibility.Collapsed;
        WindowsOcrPanel.Visibility = engine == "windows" ? Visibility.Visible : Visibility.Collapsed;
        OllamaOcrPanel.Visibility = engine == "ollama" ? Visibility.Visible : Visibility.Collapsed;
        CustomOcrPanel.Visibility = engine == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillWindowsLanguages(string? current)
    {
        WindowsLangCombo.Items.Clear();
        var languages = WindowsOcrEngine.AvailableLanguages();
        if (languages.Count == 0)
        {
            WindowsLangCombo.Items.Add("（系统未安装任何 OCR 语言包）");
            WindowsLangCombo.SelectedIndex = 0;
            return;
        }

        foreach (var lang in languages) WindowsLangCombo.Items.Add(lang);

        var index = languages.FindIndex(l => string.Equals(l, current, StringComparison.OrdinalIgnoreCase));
        WindowsLangCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void UpdateOcrDescribe()
    {
        try
        {
            OcrDescribe.Text = AppServices.Current.Ocr.DescribeCurrent();
        }
        catch (Exception ex)
        {
            OcrDescribe.Text = "识别引擎状态读取失败：" + ex.Message;
        }
    }

    private async void OnDetectUmi(object sender, RoutedEventArgs e)
    {
        var exe = UmiOcrEngine.DetectPaddleExe();
        if (exe is null)
        {
            await ShowDialogAsync(
                "没找到 Umi-OCR",
                "没有在常见位置找到 PaddleOCR-json.exe。\n请安装 Umi-OCR 后把它的安装目录填到「安装目录」里，或直接填 exe 路径。");
            return;
        }

        UmiExePath.Text = exe;
        var dir = UmiOcrEngine.EngineDirectory(exe);
        if (dir is not null) UmiInstallDir.Text = Path.GetDirectoryName(Path.GetDirectoryName(dir)) ?? string.Empty;

        var models = UmiOcrEngine.AvailableModels(exe);
        var lines = string.Join("\n", models.Select(m => $"· {m.Label}  →  {m.File}"));
        await ShowDialogAsync("找到 PaddleOCR 引擎",
            $"{exe}\n\n可用语言模型（可在「语言模型」里填文件名）：\n{lines}");
    }

    private async void OnOcrSelfTest(object sender, RoutedEventArgs e)
    {
        ApplyOcrSettingsToConfig();
        HeaderHint.Text = "正在自检识别引擎…";
        try
        {
            var service = AppServices.Current.Ocr;
            var sample = CreateSampleImage();
            var result = await service.RecognizeAsync(sample);

            if (result.Ok)
            {
                HeaderHint.Text = $"自检通过 · {result.Engine} · {result.Lines.Count} 行 · {result.ElapsedMs}ms";
                await ShowDialogAsync("识别自检通过",
                    $"引擎：{result.Engine}\n耗时：{result.ElapsedMs}ms\n识别到的文字：\n{result.Text}");
            }
            else
            {
                HeaderHint.Text = "自检失败：" + result.Error;
                await ShowDialogAsync("识别自检失败", result.Error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "自检异常：" + ex.Message;
            await ShowDialogAsync("自检异常", ex.Message);
        }
    }

    /// <summary>生成一张写着「题库工坊」的测试图，用来验证识别链路。</summary>
    private static byte[] CreateSampleImage()
    {
        using var bitmap = new System.Drawing.Bitmap(560, 140);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new System.Drawing.Font("Microsoft YaHei", 34, System.Drawing.GraphicsUnit.Pixel);
            g.DrawString("题库工坊 OCR TEST", font, System.Drawing.Brushes.Black, 24, 30);
            using var font2 = new System.Drawing.Font("Microsoft YaHei", 26, System.Drawing.GraphicsUnit.Pixel);
            g.DrawString("A. 北京   B. 上海", font2, System.Drawing.Brushes.Black, 24, 84);
        }
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private async void OnFetchOllamaModels(object sender, RoutedEventArgs e)
    {
        var provider = new AiProviderConfig
        {
            ProviderKind = "ollama",
            BaseUrl = (OllamaBaseUrl.Text ?? string.Empty).Trim(),
            TimeoutSeconds = 15,
            Name = "Ollama",
        };

        HeaderHint.Text = "正在拉取本机 Ollama 模型…";
        var result = await AppServices.Current.ModelCatalog.FetchAsync(provider);
        if (!result.Ok)
        {
            HeaderHint.Text = "拉取失败：" + result.Error;
            return;
        }

        var vision = result.Models.Where(m => m.VisionCapable).ToList();
        HeaderHint.Text = $"本机有 {result.Models.Count} 个模型，其中 {vision.Count} 个可能支持图片";
        if (vision.Count > 0) OllamaModelBox.Text = vision[0].Id;

        await ShowDialogAsync("本机模型",
            string.Join("\n", result.Models.Select(m =>
                "· " + m.Id + (string.IsNullOrWhiteSpace(m.DetailText) ? string.Empty : "  (" + m.DetailText + ")"))) +
            "\n\n识图请选带 -vl / llava / vision 的模型。");
    }

    private void ApplyOcrSettingsToConfig()    {
        var cfg = AppServices.Current.Config.Config;
        cfg.Ocr.Engine = CurrentOcrEngine();
        cfg.Ocr.Umi.UseHttp = UmiUseHttp.IsChecked == true;
        cfg.Ocr.Umi.ApiBaseUrl = (UmiApiUrl.Text ?? string.Empty).Trim();
        cfg.Ocr.Umi.InstallDirectory = (UmiInstallDir.Text ?? string.Empty).Trim();
        cfg.Ocr.Umi.EngineExePath = (UmiExePath.Text ?? string.Empty).Trim();
        cfg.Ocr.Umi.ConfigPath = (UmiConfigPath.Text ?? string.Empty).Trim();
        cfg.Ocr.OllamaBaseUrl = (OllamaBaseUrl.Text ?? string.Empty).Trim();
        cfg.Ocr.OllamaModel = (OllamaModelBox.Text ?? string.Empty).Trim();
        cfg.Ocr.Custom.Url = (CustomOcrUrl.Text ?? string.Empty).Trim();
        cfg.Ocr.Custom.BodyTemplate = CustomOcrBody.Text ?? string.Empty;
        cfg.Ocr.Custom.ResultPath = (CustomOcrPath.Text ?? string.Empty).Trim();
        cfg.Ocr.AutoSplitTitleOptions = OcrSplitBox.IsChecked == true;
        if (WindowsLangCombo.SelectedItem is string lang) cfg.Ocr.WindowsLanguage = lang;
    }

    // ── 网络出口 ──────────────────────────────────────────

    private string CurrentProxyMode() =>
        (ProxyModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

    private void SelectProxyMode(string? mode)
    {
        var want = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        foreach (var item in ProxyModeBox.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            {
                ProxyModeBox.SelectedItem = ci;
                return;
            }
        }
        ProxyModeBox.SelectedIndex = 0;
    }

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProxyModeUi();
        if (!_loading) UpdateNetworkStatus();
    }

    private void UpdateProxyModeUi()
    {
        var mode = CurrentProxyMode();
        var custom = mode == "custom";
        ProxyUrlBox.IsEnabled = custom;
        ProxyFallbackBox.IsEnabled = mode == "auto";
        if (!custom && string.IsNullOrWhiteSpace(ProxyUrlBox.Text))
            ProxyUrlBox.PlaceholderText = "只有在「自定义代理」模式下才生效";
        else
            ProxyUrlBox.PlaceholderText = "http://127.0.0.1:7890";
    }

    private void ApplyNetworkToConfig()
    {
        var cfg = AppServices.Current.Config.Config;
        var net = cfg.Network ??= new NetworkConfig();
        net.ProxyMode = CurrentProxyMode();
        net.ProxyUrl = (ProxyUrlBox.Text ?? string.Empty).Trim();
        net.BypassList = (ProxyBypassBox.Text ?? string.Empty).Trim();
        net.FallbackToDirectOnProxyFailure = ProxyFallbackBox.IsChecked == true;
    }

    private void UpdateNetworkStatus()
    {
        try
        {
            var cfg = AppServices.Current.Config.Config.Network;
            var mode = cfg?.ProxyMode ?? "auto";
            var lines = new List<string>
            {
                "当前生效：" + QBank.Core.HttpClients.DescribeCurrentProxy()
            };

            var note = QBank.Core.HttpClients.AutoFallbackNote;
            if (!string.IsNullOrEmpty(note)) lines.Add("· " + note);

            if (mode == "auto")
            {
                var dead = DetectDeadSystemProxy(out var desc);
                if (string.IsNullOrEmpty(desc))
                    lines.Add("· 系统没有设置代理。");
                else if (dead)
                    lines.Add($"· 检测到系统代理 {desc}，但这个端口没人监听 —— 保存后本程序会自动绕过它。");
                else
                    lines.Add($"· 系统代理 {desc} 可访问。");
            }

            lines.Add("· 这一项对 AI 模型、自定义接口、在线题库、模型列表全部生效，不用重启。");
            NetworkStatusText.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = "读取网络状态失败：" + ex.Message;
        }
    }

    private static bool DetectDeadSystemProxy(out string description)
        => QBank.Core.HttpClients.DescribeSystemProxyState(out description, out var dead) && dead;

    private void OnRefreshNetwork(object sender, RoutedEventArgs e) => UpdateNetworkStatus();

    private async void OnTestNetwork(object sender, RoutedEventArgs e)
    {
        NetworkStatusText.Text = "正在用第一个启用的模型试一下…";
        try
        {
            var cfg = AppServices.Current.Config.Config;
            var provider = AiAnswerService.SelectProviders(cfg.AiProviders).FirstOrDefault();
            if (provider is null)
            {
                NetworkStatusText.Text = "没有启用的服务商，先在「AI 模型管理」里勾一个。";
                return;
            }

            var catalog = AppServices.Current.ModelCatalog;
            var result = await catalog.FetchAsync(provider);
            if (result.Ok)
            {
                NetworkStatusText.Text = $"通了 ✓  {provider.Name} 返回 {result.Models.Count} 个模型（{QBank.Core.HttpClients.DescribeCurrentProxy()}）";
            }
            else
            {
                NetworkStatusText.Text = $"{provider.Name} 还是不通：{result.Error}\n当前出口：{QBank.Core.HttpClients.DescribeCurrentProxy()}";
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = "测试失败：" + ex.Message;
        }
    }

    // ── OCS 对接口 ────────────────────────────────────────

    private void UpdateOcsBridgeStatus()
    {
        var bridge = AppServices.Current.OcsBridge;
        OcsBridgeStatusText.Text = bridge.StatusText;
        OcsUrlText.Text = bridge.IsRunning
            ? $"接口地址：http://{bridge.Config.EffectiveHost}:{bridge.Port}/qbank/ocs/search"
            : "接口没在运行，点「保存设置」后会按上面的端口重新启动。";
    }

    private void ApplyOcsBridgeToConfig()
    {
        var cfg = AppServices.Current.Config.Config;
        var bridge = cfg.OcsBridge ??= new OcsBridgeConfig();
        bridge.Port = double.IsNaN(OcsPortBox.Value) ? 18322 : (int)OcsPortBox.Value;
        bridge.DisplayName = (OcsNameBox.Text ?? string.Empty).Trim();
        bridge.Token = OcsTokenBox.Password ?? string.Empty;
        bridge.MaxResults = double.IsNaN(OcsMaxResultsBox.Value) ? 3 : (int)OcsMaxResultsBox.Value;
        bridge.MinScore = double.IsNaN(OcsMinScoreBox.Value) ? 0.55 : OcsMinScoreBox.Value;
        bridge.TimeoutSeconds = double.IsNaN(OcsTimeoutBox.Value) ? 30 : (int)OcsTimeoutBox.Value;
        bridge.AllowLan = OcsEnableLanBox.IsChecked == true;
        bridge.UseLocalBank = OcsUseLocalBox.IsChecked == true;
        bridge.UseLanShare = OcsUseLanBox.IsChecked == true;
        bridge.UseAi = OcsUseAiBox.IsChecked == true;
        bridge.UseOcs = OcsUseOcsBox.IsChecked == true;
        bridge.RecordHistory = OcsRecordHistoryBox.IsChecked == true;
    }

    private async void OnCopyOcsConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyOcsBridgeToConfig();
            AppServices.Current.SaveConfig();
            UpdateOcsBridgeStatus();

            var json = AppServices.Current.OcsBridge.BuildOcsConfig();
            await CopyToClipboardAsync(json);
            HeaderHint.Text = "OCS 配置已复制，粘到 OCS 的题库设置里即可";
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "生成配置失败：" + ex.Message;
        }
    }

    private async void OnPreviewOcsConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyOcsBridgeToConfig();
            AppServices.Current.SaveConfig();
            UpdateOcsBridgeStatus();

            var json = AppServices.Current.OcsBridge.BuildOcsConfig();
            await ShowDialogAsync("OCS 题库配置",
                "把下面这段 JSON 复制到 OCS 的「题库设置」里（或用「一键导入 OCS」）。\n\n" + json, wide: true);
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "生成配置失败：" + ex.Message;
        }
    }

    /// <summary>OCS 的「题库配置链接」：直接把对接口生成的配置地址给它，粘链接就能导入。</summary>
    private string BuildConfigLink()
    {
        var bridge = AppServices.Current.OcsBridge;
        var host = bridge.Config.EffectiveHost;
        var port = bridge.IsRunning ? bridge.Port : bridge.Config.Port;
        return $"http://{host}:{port}/qbank/ocs/ocs.json";
    }

    private async void OnImportOcsViaUrl(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyOcsBridgeToConfig();
            AppServices.Current.SaveConfig();
            UpdateOcsBridgeStatus();

            var bridge = AppServices.Current.OcsBridge;
            if (!bridge.IsRunning)
            {
                await ShowDialogAsync("导入 OCS", "对接口没在运行，请检查端口是否被占用后再试。");
                return;
            }

            var link = BuildConfigLink();
            await CopyToClipboardAsync(link);
            await ShowDialogAsync("导入 OCS",
                "已复制配置链接。打开 OCS 的题库设置，把链接粘进输入框点「导入」即可（OCS 会自己去拉配置）。\n\n" +
                link +
                "\n\n如果 OCS 说拉不到，改用「复制 OCS 配置」把 JSON 直接粘进去。", wide: true);
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "生成导入链接失败：" + ex.Message;
        }
    }

    private async void OnTestOcsBridge(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyOcsBridgeToConfig();
            AppServices.Current.SaveConfig();
            UpdateOcsBridgeStatus();

            var bridge = AppServices.Current.OcsBridge;
            if (!bridge.IsRunning)
            {
                await ShowDialogAsync("测试对接口", "接口没在运行，请检查端口是否被占用。");
                return;
            }

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://127.0.0.1:{bridge.Port}/qbank/ocs/ping";
            var text = await http.GetStringAsync(url);
            await ShowDialogAsync("测试对接口", "接口在线，返回：\n\n" + text, wide: true);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("测试对接口", "连不上：" + ex.Message);
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        await Task.CompletedTask;
    }

    // ── 保存 / 其它 ───────────────────────────────────────

    // ── 备份与恢复 ────────────────────────────────────────

    /// <summary>把界面上还没保存的改动先写进内存配置，保证备份的是眼前这份。</summary>
    private void FlushUiToConfig()
    {
        foreach (var card in _cards) card.ApplyToProvider();
        ApplyOcrSettingsToConfig();
        ApplyOcsBridgeToConfig();
        ApplyNetworkToConfig();
        ApplyWindowToConfig();
        ApplyWebToConfig();
        ApplyAutoTagToConfig();
    }

    private BackupScope CurrentBackupScope()
        => (BackupScopeBox.SelectedItem as ComboBoxItem)?.Tag as string == "ai"
            ? BackupScope.AiOnly
            : BackupScope.All;

    private void OnBackupScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        // 加密与备注对两种范围都适用，这里只是刷新一下状态行
        if (_loading) return;
        UpdateBackupStatus("当前选择：" + (CurrentBackupScope() == BackupScope.AiOnly
            ? "只备份 AI 接口与 Key。"
            : "备份全部设置（AI 接口、OCS 题库、OCR、网络、窗口、局域网）。"));
    }

    private void UpdateBackupStatus(string text) => BackupStatusText.Text = text;

    /// <summary>读口令框；两次不一致就返回 null 并提示。</summary>
    private async Task<(bool Ok, string? Password)> ReadBackupPasswordAsync(bool requireMatch)
    {
        var pwd = BackupPasswordBox.Password ?? string.Empty;

        if (string.IsNullOrEmpty(pwd))
        {
            if (requireMatch && BackupPasswordAgainBox.Password is { Length: > 0 })
            {
                await ShowDialogAsync("口令不一致", "第一栏没填口令，第二栏却填了。要么两栏都留空（不加密），要么两栏填一样的。");
                return (false, null);
            }

            return (true, null);
        }

        if (requireMatch && pwd != (BackupPasswordAgainBox.Password ?? string.Empty))
        {
            await ShowDialogAsync("口令不一致", "两次输入的口令不一样，请重新输入。");
            return (false, null);
        }

        return (true, pwd);
    }

    private BackupOptions BuildBackupOptions(BackupScope scope, string? password)
        => new()
        {
            Scope = scope,
            Password = password,
            Note = string.IsNullOrWhiteSpace(BackupNoteBox.Text) ? null : BackupNoteBox.Text.Trim(),
        };

    /// <summary>把界面上的改动刷进内存配置对象（不落盘），得到一份可导出/可备份的快照。</summary>
    private AppConfig BuildConfigSnapshot()
    {
        FlushUiToConfig();
        return SettingsBackup.Clone(AppServices.Current.Config.Config);
    }

    private async void OnQuickBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, password) = await ReadBackupPasswordAsync(requireMatch: true);
            if (!ok) return;

            var scope = CurrentBackupScope();
            var options = BuildBackupOptions(scope, password);
            var snapshot = BuildConfigSnapshot();
            var path = SettingsBackup.QuickBackup(snapshot, AppServices.Current.Config.DataDir, options);

            var info = SettingsBackup.PeekFile(path);
            UpdateBackupStatus("已备份：" + path + (info is null ? string.Empty : "\n" + info.Describe()));
            ReloadBackupList();

            if (string.IsNullOrEmpty(password))
            {
                HeaderHint.Text = "已快速备份（未加密）";
                await ShowDialogAsync("备份好了",
                    "文件：" + path + "\n\n" +
                    "这里面是明文，里面有你的 API Key。别随手发给别人，也别放在公共网盘里。\n" +
                    "想加密的话，在「加密口令」里填一个再备份一次。");
            }
            else
            {
                HeaderHint.Text = "已快速备份（已加密）";
                UpdateBackupStatus(UpdateBackupStatusText(path));
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("备份失败", ex.Message);
        }
    }

    private string UpdateBackupStatusText(string path)
        => "已备份（已加密）：" + path + "\n· 恢复时需要同一个口令，忘了就打不开了 —— 请自己记好。";

    private async void OnExportBackupFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, password) = await ReadBackupPasswordAsync(requireMatch: true);
            if (!ok) return;

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "qbank-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("题库工坊设置备份", new List<string> { SettingsBackup.FileExtension });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var json = SettingsBackup.Export(BuildConfigSnapshot(), BuildBackupOptions(CurrentBackupScope(), password));
            await File.WriteAllTextAsync(file.Path, json, new System.Text.UTF8Encoding(false));

            UpdateBackupStatus("已导出：" + file.Path);
            HeaderHint.Text = "已导出备份文件";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导出失败", ex.Message);
        }
    }

    private async void OnCopyBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, password) = await ReadBackupPasswordAsync(requireMatch: true);
            if (!ok) return;

            var json = SettingsBackup.Export(BuildConfigSnapshot(), BuildBackupOptions(CurrentBackupScope(), password));
            await CopyToClipboardAsync(json);

            UpdateBackupStatus(string.IsNullOrEmpty(password)
                ? "已复制到剪贴板（明文，含 Key，粘到安全的地方）。"
                : "已复制到剪贴板（已加密，换台机器粘回去 + 同一个口令就能恢复）。");
            HeaderHint.Text = "备份已复制";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("复制失败", ex.Message);
        }
    }

    private async void OnRestoreQuickBackup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        await RestoreFromAsync(() => Task.FromResult<string?>(File.Exists(path) ? File.ReadAllText(path) : null), path);
    }

    private async void OnDeleteQuickBackup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;

        var dialog = new ContentDialog
        {
            Title = "删除这份备份？",
            Content = new TextBlock
            {
                Text = "文件会被直接删掉，删了就没了：\n\n" + path,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (SettingsBackup.Delete(path))
        {
            UpdateBackupStatus("已删除：" + Path.GetFileName(path));
            ReloadBackupList();
        }
        else
        {
            await ShowDialogAsync("删除失败", "文件可能被占用或没有权限：\n" + path);
        }
    }

    private async void OnImportBackupFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(SettingsBackup.FileExtension);
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await RestoreFromAsync(async () => await File.ReadAllTextAsync(file.Path), file.Path);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("读取失败", ex.Message);
        }
    }

    private async void OnPasteBackupClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = Clipboard.GetContent();
            if (!package.Contains(StandardDataFormats.Text))
            {
                await ShowDialogAsync("剪贴板里没有文字", "先把备份 JSON 复制到剪贴板，再来点这个按钮。");
                return;
            }

            var text = await package.GetTextAsync();
            await RestoreFromAsync(() => Task.FromResult<string?>(text), "（剪贴板）");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("读取剪贴板失败", ex.Message);
        }
    }

    /// <summary>恢复的公共流程：看元信息 → 问口令 → 问覆盖还是合并 → 应用 → 重新加载界面。</summary>
    private async Task RestoreFromAsync(Func<Task<string?>> loader, string sourceLabel)
    {
        string? content;
        try
        {
            content = await loader();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("读取失败", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            await ShowDialogAsync("文件读不到内容", "来源：" + sourceLabel);
            return;
        }

        var info = SettingsBackup.Peek(content);
        if (info is null)
        {
            await ShowDialogAsync("不是本程序的备份",
                "这个文件里没有 qbank-settings 标记，看起来不是题库工坊导出的设置备份。\n\n" +
                "如果你要导入的是题库题目，请去「本地题库」页用导入功能。");
            return;
        }

        // 加密的先要口令
        string? password = null;
        if (info.Encrypted)
        {
            password = await AskPasswordAsync(info);
            if (password is null) return;
        }

        var mode = await AskImportModeAsync(info);
        if (mode is null) return;

        var result = SettingsBackup.Import(content, AppServices.Current.Config.Config, mode.Value, password);
        if (!result.Ok)
        {
            await ShowDialogAsync("恢复失败", result.Error ?? "未知原因");
            return;
        }

        AppServices.Current.SaveConfig();

        // 配置对象换过内容了，界面必须重读，否则显示的旧值
        _cards.Clear();
        ReloadFromConfig();
        (App.MainAppWindow as MainWindow)?.ApplyTraySettings();

        HeaderHint.Text = "已恢复设置";
        UpdateBackupStatus("已从 " + (info.FileName == "（剪贴板）" ? "剪贴板" : info.FileName) + " 恢复：\n· " +
                           string.Join("\n· ", result.Changes));

        await ShowDialogAsync("恢复完成",
            "已应用：" + (mode.Value == BackupImportMode.Merge ? "合并" : "覆盖") + "\n\n" +
            "· " + string.Join("\n· ", result.Changes) + "\n\n" +
            "设置已保存。局域网共享、OCS 接口、网络出口都已按新配置重启。", wide: true);
    }

    private async Task<string?> AskPasswordAsync(BackupInfo info)
    {
        var box = new PasswordBox { PlaceholderText = "这份备份加了口令", Width = 320 };
        var text = new TextBlock
        {
            Text = "备份信息：" + info.Describe() +
                   (string.IsNullOrWhiteSpace(info.Note) ? string.Empty : "\n备注：" + info.Note),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 10),
        };

        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "输入备份口令",
            Content = panel,
            PrimaryButtonText = "解密",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var pwd = box.Password ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            await ShowDialogAsync("口令是空的", "这份备份加密过，必须填口令。");
            return null;
        }

        return pwd;
    }

    private async Task<BackupImportMode?> AskImportModeAsync(BackupInfo info)
    {
        var text = new TextBlock
        {
            Text = "备份内容：" + info.Describe() +
                   (string.IsNullOrWhiteSpace(info.Note) ? string.Empty : "\n备注：" + info.Note) + "\n\n" +
                   "· 覆盖：当前设置完全变成备份里的样子（备份里没填 Key 的服务商会变空）\n" +
                   "· 合并：AI 服务商和 OCS 题库按条目更新，没填 Key 的条目不会把现有 Key 抹掉；其余设置仍然被覆盖",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
        };

        var dialog = new ContentDialog
        {
            Title = "覆盖还是合并？",
            Content = text,
            PrimaryButtonText = "覆盖",
            SecondaryButtonText = "合并",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot,
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => BackupImportMode.Replace,
            ContentDialogResult.Secondary => BackupImportMode.Merge,
            _ => null,
        };
    }

    private void ReloadBackupList()
    {
        try
        {
            var items = SettingsBackup.ListQuickBackups(AppServices.Current.Config.DataDir);
            BackupList.ItemsSource = items;
            BackupList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            BackupEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (items.Count > 0)
                UpdateBackupStatus($"backups 文件夹里有 {items.Count} 份快速备份。");
        }
        catch (Exception ex)
        {
            BackupEmptyHint.Visibility = Visibility.Visible;
            BackupEmptyHint.Text = "读取备份列表失败：" + ex.Message;
        }
    }

    // ── 窗口与托盘 ────────────────────────────────────────

    private void ApplyWindowToConfig()
    {
        var win = AppServices.Current.Config.Config.Window ??= new WindowConfig();
        win.EnableTray = TrayEnableBox.IsChecked == true;
        win.CloseToTray = TrayCloseBox.IsChecked == true;
        win.MinimizeToTray = TrayMinimizeBox.IsChecked == true;
        win.ShowTrayHint = TrayHintBox.IsChecked == true;
        win.RememberWindowPlacement = RememberPlacementBox.IsChecked == true;
    }

    private void OnWindowTrayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateWindowTrayUi();
    }

    private void UpdateWindowTrayUi()
    {
        var enabled = TrayEnableBox.IsChecked == true;
        TrayCloseBox.IsEnabled = enabled;
        TrayMinimizeBox.IsEnabled = enabled;
        TrayHintBox.IsEnabled = enabled;

        if (!enabled)
        {
            TrayStatusText.Text = "· 不使用托盘 —— 关掉窗口就是退出程序，局域网共享和 OCS 接口服务会一起停。\n" +
                                  "· 如果你只想要普通单机使用，这样最省资源。";
            return;
        }

        var closeToTray = TrayCloseBox.IsChecked == true;
        var minimizeToTray = TrayMinimizeBox.IsChecked == true;

        // 托盘能不能真用，得看运行时有没有登记成功
        var reason = MainWindow.TrayUnavailableReason;
        if (reason is not null)
        {
            TrayStatusText.Text = "· 托盘图标没能登记成功：" + reason + "\n" +
                                  "· 这种情况下关窗口会直接退出，不会留在后台。程序本身功能不受影响。";
            return;
        }

        var lines = new List<string>
        {
            closeToTray
                ? "· 关窗口 → 收进托盘，程序继续在后台跑"
                : "· 关窗口 → 直接退出程序（托盘图标在运行期间仍然常驻）",
        };

        if (minimizeToTray) lines.Add("· 最小化 → 也收进托盘，不占任务栏");

        lines.Add("· 托盘图标：双击 = 打开主界面，右键 = 打开数据目录 / 退出程序");
        lines.Add("· 退出请走托盘菜单的「退出题库工坊」，这样会正常保存配置");
        TrayStatusText.Text = string.Join("\n", lines);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var card in _cards) card.ApplyToProvider();

            var cfg = AppServices.Current.Config.Config;
            cfg.Search.TimeoutSeconds = double.IsNaN(TimeoutBox.Value) ? 60 : (int)TimeoutBox.Value;
            cfg.Search.MaxParallel = double.IsNaN(ParallelBox.Value) ? 8 : (int)ParallelBox.Value;
            cfg.Search.CacheMinScore = double.IsNaN(CacheScoreBox.Value) ? 0.72 : CacheScoreBox.Value;
            cfg.Search.AutoCacheMinScore = double.IsNaN(AutoCacheBox.Value) ? 0.9 : AutoCacheBox.Value;
            cfg.LocalBank.AutoCache = AutoCacheSwitch.IsChecked == true;
            cfg.Search.RecordHistory = RecordHistorySwitch.IsChecked == true;

            ApplyOcrSettingsToConfig();
            ApplyOcsBridgeToConfig();
            ApplyNetworkToConfig();
            ApplyWindowToConfig();
            ApplyWebToConfig();
            ApplyAutoTagToConfig();

            AppServices.Current.SaveConfig();
            UpdateOcrDescribe();
            UpdateOcsBridgeStatus();
            UpdateNetworkStatus();
            UpdateWindowTrayUi();
            (App.MainAppWindow as MainWindow)?.ApplyTraySettings();
            HeaderHint.Text = $"已保存 · {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        try
        {
            AppServices.Current.Config.Reload();
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "重新加载失败：" + ex.Message;
            return;
        }
        _cards.Clear();
        ReloadFromConfig();
        HeaderHint.Text = "已重新加载配置";
    }

    private void RefreshStats()
    {
        try
        {
            var bank = AppServices.Current.Bank.Stats();
            var history = AppServices.Current.History.Stats();
            StatsText.Text = $"本地题库：{bank.Total} 道（今日新增 {bank.TodayAdded}，累计命中 {bank.TotalHits}）\n" +
                             $"搜题历史：{history.Total} 条，成功率 {history.SuccessRate:F1}%";
        }
        catch (Exception ex)
        {
            StatsText.Text = "统计读取失败：" + ex.Message;
        }
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppServices.Current.Config.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            HeaderHint.Text = "打开目录失败：" + ex.Message;
        }
    }

    private async void OnBackupDatabase(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"qbank-backup-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("SQLite 数据库", new List<string> { ".db" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            File.Copy(AppServices.Current.Config.DatabaseFile, file.Path, overwrite: true);
            HeaderHint.Text = "已备份到 " + file.Path;
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("备份失败", ex.Message);
        }
    }

    private async Task ShowDialogAsync(string title, string message, bool wide = false)
    {
        var box = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = box, MaxHeight = 460, MaxWidth = wide ? 720 : double.PositiveInfinity },
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        if (wide) dialog.MaxWidth = 780;
        await dialog.ShowAsync();
    }
}
