using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QBank.App.Pages;
using QBank.App.Services;
using Windows.Graphics;

namespace QBank.App;

public sealed partial class MainWindow : Window
{
    private string _currentTag = string.Empty;

    private AppWindow? _appWindow;
    private TrayIcon? _tray;
    /// <summary>为 true 时关闭窗口就是真退出，不再拦。</summary>
    private bool _allowExit;
    private bool _balloonShown;
    private bool _trayWarningLogged;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        SetupTray();
        try
        {
            var page = StartupPage();
            _currentTag = TagOf(page);
            ContentFrame.Navigate(page);
            SyncNavSelection(_currentTag);
            App.CrashLog("MainWindow: 初始导航成功 -> " + _currentTag, null);
        }
        catch (Exception ex)
        {
            App.CrashLog("MainWindow: 初始导航失败", ex);
        }
    }

    private static string TagOf(Type page)
    {
        if (page == typeof(SettingsPage)) return "settings";
        if (page == typeof(BankPage)) return "bank";
        if (page == typeof(HistoryPage)) return "history";
        if (page == typeof(LanPage)) return "lan";
        return "search";
    }

    /// <summary>把左侧导航的高亮同步到当前页，避免初始 SelectionChanged 把页面冲掉。</summary>
    private void SyncNavSelection(string tag)
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                if (!ReferenceEquals(Nav.SelectedItem, nvi)) Nav.SelectedItem = nvi;
                return;
            }
        }
    }

    /// <summary>支持 --page settings/bank/history/lan 或环境变量 QBANK_PAGE 启动到指定页，方便逐页截图核验。</summary>
    private static Type StartupPage()
    {
        string? page = Environment.GetEnvironmentVariable("QBANK_PAGE");

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                page = args[i + 1];
                break;
            }
            if (args[i].StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                page = args[i]["--page=".Length..];
                break;
            }
        }

        App.CrashLog($"StartupPage: args=[{string.Join(" ", args)}] page={page}", null);

        return (page ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "settings" => typeof(SettingsPage),
            "bank" => typeof(BankPage),
            "history" => typeof(HistoryPage),
            "lan" => typeof(LanPage),
            _ => typeof(SearchPage),
        };
    }

    private void ConfigureWindow()
    {
        try
        {
            Title = "QBank 题库工坊";
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            _appWindow = appWindow;

            var cfg = AppServices.Current.Config.Config.Window;
            var (width, height) = cfg.SafeSize();
            appWindow.Resize(new SizeInt32(width, height));
            TryRestorePlacement(appWindow, cfg);

            TrySetWindowIcon(appWindow);

            // 关窗口先收进托盘（除非用户自己关了这个行为，或者我们正在真退出）
            appWindow.Closing += OnAppWindowClosing;
            appWindow.Changed += OnAppWindowChanged;

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);
            }
        }
        catch
        {
            // 自定义标题栏失败不影响主流程
        }
    }

    /// <summary>把上次的窗口位置搬回来；位置明显不在任何屏幕上就退回默认居中。</summary>
    private static void TryRestorePlacement(AppWindow appWindow, QBank.Core.Models.WindowConfig cfg)
    {
        if (!cfg.RememberWindowPlacement) return;
        if (cfg.WindowX == int.MinValue || cfg.WindowY == int.MinValue) return;

        try
        {
            // 至少要有 200x120 落在可见区，否则用户就找不到了
            if (cfg.WindowX + 200 < 0 || cfg.WindowY + 120 < 0) return;

            var left = Math.Max(0, cfg.WindowX);
            var top = Math.Max(0, cfg.WindowY);

            appWindow.Move(new PointInt32(left, top));
            if (cfg.WindowMaximized && appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch (Exception ex)
        {
            App.Trace("恢复窗口位置失败：" + ex.Message);
        }
    }

    /// <summary>把当前窗口尺寸/位置写回配置（退出或收托盘时调用）。</summary>
    private void SavePlacement()
    {
        try
        {
            var cfg = AppServices.Current.Config.Config.Window;
            if (!cfg.RememberWindowPlacement || _appWindow is null) return;

            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            cfg.WindowX = pos.X;
            cfg.WindowY = pos.Y;
            cfg.WindowMaximized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

            if (!cfg.WindowMaximized)
            {
                cfg.WindowWidth = size.Width;
                cfg.WindowHeight = size.Height;
            }
        }
        catch (Exception ex)
        {
            App.Trace("保存窗口位置失败：" + ex.Message);
        }
    }

    // ─── 托盘 ────────────────────────────────────────────────────────────

    private void SetupTray()
    {
        try
        {
            var cfg = AppServices.Current.Config.Config.Window;
            if (!cfg.EnableTray) return;

            if (_appWindow is null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "QBank.ico");
            if (!File.Exists(icoPath)) icoPath = Path.Combine(AppContext.BaseDirectory, "QBank.ico");

            _tray = new TrayIcon(hwnd, "题库工坊 · 后台运行中", File.Exists(icoPath) ? icoPath : null);
            _tray.ShowRequested += RestoreFromTray;
            _tray.ExitRequested += ExitFromTray;
            _tray.OpenDataDirectoryRequested += OpenDataDirectory;

            if (!_tray.TryInstall())
            {
                TrayUnavailableReason = _tray.LastError;
                _tray.Dispose();
                _tray = null;
            }
            else
            {
                TrayUnavailableReason = null;
            }
        }
        catch (Exception ex)
        {
            App.Trace("托盘初始化失败：" + ex.Message);
            TrayUnavailableReason = "托盘初始化失败：" + ex.Message;
            _tray = null;
        }
    }

    /// <summary>托盘用不了时的原因；能用就是 null。设置页会读它显示给用户。</summary>
    public static string? TrayUnavailableReason { get; private set; }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit) return;

        var cfg = AppServices.Current.Config.Config.Window;
        if (cfg is not { EnableTray: true, CloseToTray: true })
        {
            SavePlacement();
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    /// <summary>最小化时也收进托盘（设置里可关）。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_allowExit || !args.DidPresenterChange) return;

        try
        {
            var cfg = AppServices.Current.Config.Config.Window;
            if (!cfg.EnableTray || !cfg.MinimizeToTray) return;
            if (_tray is null) return;
            if (sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

            SavePlacement();
            sender.Hide();
            App.Trace("窗口最小化后收进托盘");
        }
        catch (Exception ex)
        {
            App.Trace("最小化收托盘失败：" + ex.Message);
        }
    }

    /// <summary>把窗口藏进托盘。托盘图标没建起来就只能老老实实退出，否则进程会变幽灵。</summary>
    private void HideToTray()
    {
        if (_tray is null || !_tray.Installed)
        {
            if (!_trayWarningLogged)
            {
                _trayWarningLogged = true;
                App.Trace("托盘不可用，关闭窗口按退出处理（不会留在后台）");
            }

            ExitFromTray();
            return;
        }

        SavePlacement();

        var cfg = AppServices.Current.Config.Config.Window;
        _appWindow?.Hide();

        if (cfg.ShowTrayHint && !_balloonShown)
        {
            _balloonShown = true;
            _tray.ShowBalloon("题库工坊还在后台运行",
                "局域网共享和 OCS 接口服务没停。双击托盘图标可以叫回窗口，右键菜单里能退出。");
        }

        App.Trace("窗口已收进托盘");
    }

    /// <summary>从托盘把窗口叫回来。</summary>
    private void RestoreFromTray()
    {
        try
        {
            _appWindow?.Show();
            Activate();
            App.Trace("窗口已从托盘恢复");
        }
        catch (Exception ex)
        {
            App.Trace("恢复窗口失败：" + ex.Message);
        }
    }

    /// <summary>托盘菜单里的「退出」：放行关闭，走正常退出流程。</summary>
    private void ExitFromTray()
    {
        _allowExit = true;
        SavePlacement();

        try
        {
            AppServices.Current.SaveConfig();
        }
        catch (Exception ex)
        {
            App.Trace("退出前保存配置失败：" + ex.Message);
        }

        _tray?.Dispose();
        _tray = null;

        Close();
        Application.Current.Exit();
    }

    private void OpenDataDirectory()
    {
        try
        {
            var dir = AppServices.Current.Config.DataDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Trace("打开数据目录失败：" + ex.Message);
        }
    }

    /// <summary>设置页改了托盘开关后重新应用，不用重启程序。</summary>
    public void ApplyTraySettings()
    {
        var cfg = AppServices.Current.Config.Config.Window;

        if (!cfg.EnableTray)
        {
            _tray?.Dispose();
            _tray = null;
            return;
        }

        if (_tray is null) SetupTray();
    }

    /// <summary>程序真退出前调用，确保图标不会留在托盘里变僵尸。</summary>
    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>给窗口设置任务栏 / Alt-Tab 用的图标（exe 图标已由 csproj 嵌入，这里补窗口级）。</summary>
    private static void TrySetWindowIcon(AppWindow appWindow)
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "QBank.ico");
            if (!File.Exists(icoPath))
                icoPath = Path.Combine(AppContext.BaseDirectory, "QBank.ico");
            if (!File.Exists(icoPath)) return;
            appWindow.SetIcon(icoPath);
        }
        catch (Exception ex)
        {
            App.Trace("设置窗口图标失败：" + ex.Message);
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) return;
        var tag = item.Tag as string ?? "search";
        if (tag == _currentTag) return; // 初始同步造成的高亮变化不该重新导航
        _currentTag = tag;
        App.CrashLog($"Nav_SelectionChanged -> {tag}", null);

        try
        {
            switch (tag)
            {
                case "search":
                    ContentFrame.Navigate(typeof(SearchPage));
                    break;
                case "bank":
                    ContentFrame.Navigate(typeof(BankPage));
                    break;
                case "history":
                    ContentFrame.Navigate(typeof(HistoryPage));
                    break;
                case "lan":
                    ContentFrame.Navigate(typeof(LanPage));
                    break;
                case "settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
            }
        }
        catch (Exception ex)
        {
            App.CrashLog($"导航到 {tag} 失败", ex);
        }
    }

    /// <summary>点 NavigationView 底部的齿轮进设置页。</summary>
    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _currentTag = "settings";
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    public void ShowStatus(string text)
    {
        if (DispatcherQueue.HasThreadAccess)
            TitleBarStatus.Text = text;
        else
            DispatcherQueue.TryEnqueue(() => TitleBarStatus.Text = text);
    }

    /// <summary>切到搜题页并带上题干（历史记录「再搜一次」用）。</summary>
    public void NavigateToSearch(string title, string options)
    {
        ContentFrame.Navigate(typeof(SearchPage));

        if (ContentFrame.Content is SearchPage page)
        {
            page.Prefill(title, options);
            return;
        }

        // 页面还没准备好，等导航完成再填
        void OnNavigated(object s, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            ContentFrame.Navigated -= OnNavigated;
            if (ContentFrame.Content is SearchPage p) p.Prefill(title, options);
        }

        ContentFrame.Navigated += OnNavigated;
    }
}
