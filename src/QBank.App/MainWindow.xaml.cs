using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QBank.App.Pages;
using Windows.Graphics;

namespace QBank.App;

public sealed partial class MainWindow : Window
{
    private string _currentTag = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
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

            appWindow.Resize(new SizeInt32(1280, 840));
            TrySetWindowIcon(appWindow);

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
