using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;
using QBank.Core.Lan;
using QBank.Core.Models;

namespace QBank.App.Pages;

public sealed partial class LanPage : Page
{
    public LanPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PeerList.Children.Count > 0) return;
        LoadFromConfig();
        RebuildPeerList();
    }

    private void LoadFromConfig()
    {
        var cfg = AppServices.Current.Config.Config.LanShare;
        EnableBox.IsChecked = cfg.Enabled;
        DiscoveryBox.IsChecked = cfg.RespondToDiscovery;
        AllowLanBox.IsChecked = cfg.AllowLan;
        AllowWriteBox.IsChecked = cfg.AllowRemoteWrite;
        PortBox.Value = cfg.Port;
        DiscoveryPortBox.Value = cfg.DiscoveryPort;
        MaxResultsBox.Value = cfg.MaxResultsPerQuery;
        ServerNameBox.Text = cfg.ServerName;
        TokenBox.Password = cfg.Token ?? string.Empty;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var lan = AppServices.Current.Lan;
        LanStatusText.Text = lan.StatusText;
    }

    private void OnSaveServer(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = AppServices.Current.Config.Config.LanShare;
            cfg.Enabled = EnableBox.IsChecked == true;
            cfg.RespondToDiscovery = DiscoveryBox.IsChecked == true;
            cfg.AllowLan = AllowLanBox.IsChecked == true;
            cfg.AllowRemoteWrite = AllowWriteBox.IsChecked == true;
            cfg.Port = double.IsNaN(PortBox.Value) ? 18321 : (int)PortBox.Value;
            cfg.DiscoveryPort = double.IsNaN(DiscoveryPortBox.Value) ? 39991 : (int)DiscoveryPortBox.Value;
            cfg.MaxResultsPerQuery = double.IsNaN(MaxResultsBox.Value) ? 5 : (int)MaxResultsBox.Value;
            cfg.ServerName = (ServerNameBox.Text ?? string.Empty).Trim();
            cfg.Token = TokenBox.Password ?? string.Empty;

            AppServices.Current.SaveConfig();
            UpdateStatus();
            SyncStatus.Text = $"已保存并应用 · {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        AppServices.Current.Config.Reload();
        LoadFromConfig();
        RebuildPeerList();
        SyncStatus.Text = "已重新加载";
    }

    private void RebuildPeerList()
    {
        PeerList.Children.Clear();
        var peers = AppServices.Current.Config.Config.LanPeers;

        if (peers.Count == 0)
        {
            PeerList.Children.Add(new TextBlock
            {
                Text = "还没有节点。点「搜索局域网节点」自动发现，或手动添加一个。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        foreach (var peer in peers)
        {
            var p = peer;
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
            info.Children.Add(new TextBlock { Text = p.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var detail = $"上次见到：{(p.LastSeenAt is null ? "从未" : p.LastSeenAt.Value.ToLocalTime().ToString("MM-dd HH:mm"))}";
            if (p.QuestionCount > 0) detail += $" · 约 {p.QuestionCount} 道题";
            detail += p.LastSyncAt is null
                ? " · 还没同步过"
                : $" · 上次同步 {p.LastSyncAt.Value.ToLocalTime():MM-dd HH:mm}（{p.LastSyncCount} 道）";
            info.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var test = new Button { Content = "测试", VerticalAlignment = VerticalAlignment.Center };
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                test.Content = "连接中…";
                using var client = new LanBankClient(p);
                var (ok, message) = await client.TestAsync();
                test.Content = ok ? "正常" : "失败";
                test.IsEnabled = true;
                SyncStatus.Text = $"{p.DisplayName}：{message}";
                await Task.Delay(1500);
                test.Content = "测试";
            };
            Grid.SetColumn(test, 1);
            grid.Children.Add(test);

            var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) =>
            {
                AppServices.Current.Config.Config.LanPeers.Remove(p);
                AppServices.Current.SaveConfig();
                RebuildPeerList();
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            border.Child = grid;
            PeerList.Children.Add(border);
        }
    }

    private async void OnDiscover(object sender, RoutedEventArgs e)
    {
        DiscoverButton.IsEnabled = false;
        DiscoverHint.Text = "正在广播搜索…";
        try
        {
            var cfg = AppServices.Current.Config.Config;
            var found = await LanShareService.DiscoverAsync(cfg.LanShare, TimeSpan.FromSeconds(3));

            var peers = cfg.LanPeers;
            var added = 0;
            foreach (var item in found)
            {
                var host = item.Host;
                var port = item.Info.Port <= 0 ? cfg.LanShare.Port : item.Info.Port;
                if (peers.Any(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase) && p.Port == port))
                    continue;

                peers.Add(new LanPeerConfig
                {
                    Name = string.IsNullOrWhiteSpace(item.Info.Name) ? host : item.Info.Name,
                    Host = host,
                    Port = port,
                    QuestionCount = item.Info.QuestionCount,
                    Version = item.Info.Version,
                    LastSeenAt = DateTimeOffset.Now,
                    Enabled = true,
                });
                added++;
            }

            if (added > 0) AppServices.Current.SaveConfig();
            RebuildPeerList();

            DiscoverHint.Text = found.Count == 0
                ? "没有发现节点。确认对方开着「题库工坊」并启用了共享，且在同一网段、防火墙放行了端口。"
                : $"发现 {found.Count} 个节点，新增 {added} 个。";
        }
        catch (Exception ex)
        {
            DiscoverHint.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }

    private async void OnAddPeerManually(object sender, RoutedEventArgs e)
    {
        var hostBox = new TextBox { Header = "对方地址", PlaceholderText = "192.168.1.20" };
        var portBox = new NumberBox { Header = "端口", Value = 18321, Minimum = 1024, Maximum = 65535 };
        var nameBox = new TextBox { Header = "显示名（可选）", PlaceholderText = "客厅电脑" };
        var tokenBox = new PasswordBox { Header = "口令（对方设了才填）", PasswordRevealMode = PasswordRevealMode.Peek };

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(nameBox);
        panel.Children.Add(hostBox);
        panel.Children.Add(portBox);
        panel.Children.Add(tokenBox);

        var dialog = new ContentDialog
        {
            Title = "手动添加节点",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var host = (hostBox.Text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            SyncStatus.Text = "地址不能为空";
            return;
        }

        var cfg = AppServices.Current.Config.Config;
        cfg.LanPeers.Add(new LanPeerConfig
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? host : nameBox.Text.Trim(),
            Host = host,
            Port = double.IsNaN(portBox.Value) ? 18321 : (int)portBox.Value,
            Token = tokenBox.Password ?? string.Empty,
            Enabled = true,
        });
        AppServices.Current.SaveConfig();
        RebuildPeerList();
        SyncStatus.Text = "已添加节点";
    }

    private async void OnPushAll(object sender, RoutedEventArgs e)
    {
        SyncStatus.Text = "正在推送…";
        try
        {
            var questions = AppServices.Current.Bank.Query(null, null, 0, 500, "updated");
            if (questions.Count == 0)
            {
                SyncStatus.Text = "本地题库是空的，没有可推送的题目";
                return;
            }
            var (ok, failed) = await AppServices.Current.Lan.PushAsync(questions);
            SyncStatus.Text = $"推送完成：成功 {ok}，失败 {failed}";
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "推送失败：" + ex.Message;
        }
    }

    private async void OnPullAll(object sender, RoutedEventArgs e)
    {
        var incremental = IncrementalBox.IsChecked == true;
        SyncStatus.Text = incremental ? "正在增量拉取…" : "正在全量拉取…";
        try
        {
            var peers = AppServices.Current.Config.Config.LanPeers.Where(p => p.Enabled).ToList();
            if (peers.Count == 0)
            {
                SyncStatus.Text = "先添加至少一个节点";
                return;
            }

            var total = 0;
            var lines = new List<string>();
            foreach (var peer in peers)
            {
                var result = await AppServices.Current.Lan.PullFromAsync(peer, incremental);
                total += result.Imported;
                lines.Add(result.Describe());
            }

            AppServices.Current.SaveConfig();
            RebuildPeerList();
            SyncStatus.Text = $"拉取完成：合计导入 {total} 道题\n· " + string.Join("\n· ", lines);
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "拉取失败：" + ex.Message;
        }
    }

    private void OnForgetSync(object sender, RoutedEventArgs e)
    {
        var count = AppServices.Current.Lan.ForgetSyncHistory();
        AppServices.Current.SaveConfig();
        RebuildPeerList();
        SyncStatus.Text = count == 0
            ? "本来就没有同步记录，下次拉取仍按节点上的全部题目。"
            : $"已清掉 {count} 个节点的同步记录，下次拉取会是全量。";
    }
}
