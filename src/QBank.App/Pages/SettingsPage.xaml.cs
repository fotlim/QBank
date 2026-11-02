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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace QBank.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly List<ProviderCard> _cards = new();
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_cards.Count > 0) return; // 页面被缓存，避免重复加载
        BuildPresetMenu();
        ReloadFromConfig();
        ApplyDebugScroll();
    }

    /// <summary>
    /// 调试用：环境变量 QBANK_SCROLL_TO=ocs 时，加载后自动滚到对应卡片，
    /// 方便截图核验长页面下半部分（自动化截图没法可靠地模拟滚轮）。
    /// </summary>
    private void ApplyDebugScroll()
    {
        var target = Environment.GetEnvironmentVariable("QBANK_SCROLL_TO");
        if (string.IsNullOrWhiteSpace(target)) return;

        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            switch (target.Trim().ToLowerInvariant())
            {
                case "ocs-bridge":
                case "ocsbridge":
                case "bridge":
                    OcsBridgeCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "ocr":
                    OcrCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "search":
                case "searchopts":
                    SearchOptionsCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "data":
                    DataCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "net":
                case "network":
                case "proxy":
                    NetworkCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                    break;
                case "ocs":
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

            RefreshStats();
        }
        finally
        {
            _loading = false;
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

            AppServices.Current.SaveConfig();
            UpdateOcrDescribe();
            UpdateOcsBridgeStatus();
            UpdateNetworkStatus();
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
