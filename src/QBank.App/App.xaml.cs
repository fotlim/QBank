using Microsoft.UI.Xaml;
using QBank.App.Services;

namespace QBank.App;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    /// <summary>启动/崩溃日志固定写到 exe 同目录的 startup.log，排查启动失败用。</summary>
    public static readonly string StartupLogPath =
        Path.Combine(AppContext.BaseDirectory, "startup.log");

    public App()
    {
        Trace("App ctor 开始");

        try
        {
            InitializeComponent();
            Trace("InitializeComponent 完成");
        }
        catch (Exception ex)
        {
            CrashLog("InitializeComponent 失败", ex);
            throw;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("AppDomain 未处理异常", e.ExceptionObject as Exception);

        UnhandledException += (_, e) =>
        {
            CrashLog("XAML 未处理异常", e.Exception);
            e.Handled = true;
        };

        try
        {
            AppServices.Current.Initialize();
            Trace("服务初始化完成，数据目录 " + AppServices.Current.Config.DataDir);
        }
        catch (Exception ex)
        {
            CrashLog("服务初始化失败", ex);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainAppWindow = new MainWindow();
            MainAppWindow.Activate();
            Trace("主窗口已激活");
        }
        catch (Exception ex)
        {
            CrashLog("主窗口创建失败", ex);
            throw;
        }
    }

    /// <summary>写一行启动轨迹。</summary>
    public static void Trace(string message)
    {
        Append($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    }

    /// <summary>记录一次异常（含堆栈）。</summary>
    public static void CrashLog(string scene, Exception? ex)
    {
        Append($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {scene}\n{ex}\n");
    }

    private static void Append(string text)
    {
        try
        {
            File.AppendAllText(StartupLogPath, text + "\n");
        }
        catch
        {
            // 日志都写不了就放弃
        }
    }
}
