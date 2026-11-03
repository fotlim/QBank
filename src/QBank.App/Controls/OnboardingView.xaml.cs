using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;

namespace QBank.App.Controls;

/// <summary>
/// 首次使用引导。四步走：这是什么 → 配模型 → 图片识题 → 怎么搜。
/// 放在主窗口的覆盖层里，走完或跳过就藏起来。
/// </summary>
public sealed partial class OnboardingView : UserControl
{
    private const int TotalSteps = 4;

    private int _step = 1;

    /// <summary>用户走完引导，或者直接跳过了。</summary>
    public event EventHandler? Finished;

    /// <summary>用户想去设置页的某个分类（模型与网络 / 图片识题）。</summary>
    public event EventHandler<string>? GoToSettings;

    /// <summary>用户点了「开始搜题」，希望直接去搜题页并聚焦题干框。</summary>
    public event EventHandler? StartSearching;

    public OnboardingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshOcrStatus();

        // 调试用：QBANK_ONBOARDING_STEP=3 直接停在某一步，方便截图核验
        var forced = Environment.GetEnvironmentVariable("QBANK_ONBOARDING_STEP");
        if (int.TryParse(forced, out var n) && n >= 1 && n <= TotalSteps) SetStep(n);
        else SetStep(1);
    }

    private void SetStep(int step)
    {
        _step = step;

        Pane1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pane2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Pane3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Pane4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        StepText.Text = $"第 {step} 步 / 共 {TotalSteps} 步";

        // 走到过的步骤点亮
        Dot1.Opacity = step >= 1 ? 1 : 0.25;
        Dot2.Opacity = step >= 2 ? 1 : 0.25;
        Dot3.Opacity = step >= 3 ? 1 : 0.25;
        Dot4.Opacity = step >= 4 ? 1 : 0.25;

        BackButton.IsEnabled = step > 1;
        SkipButton.Content = step >= TotalSteps ? "关闭" : "跳过引导";

        // 最后一步页面里已经有「开始搜题」按钮，底部就不再重复一个
        NextButton.Visibility = step >= TotalSteps ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshOcrStatus()
    {
        string text;
        try
        {
            text = AppServices.Current.Ocr?.DescribeCurrent() ?? string.Empty;
        }
        catch (Exception ex)
        {
            text = "读取识别引擎状态失败：" + ex.Message;
        }

        OcrStatusText.Text = string.IsNullOrWhiteSpace(text)
            ? "还没检测到识别引擎。搜题页的「截图识题」需要先在设置里挑一个引擎。"
            : text;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step >= TotalSteps)
        {
            Finish();
            return;
        }

        SetStep(_step + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 1) SetStep(_step - 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private void OnFinishSearch(object sender, RoutedEventArgs e) => StartSearching?.Invoke(this, EventArgs.Empty);

    private void OnGoSettingsModels(object sender, RoutedEventArgs e) => GoToSettings?.Invoke(this, "models");

    private void OnGoSettingsOcr(object sender, RoutedEventArgs e) => GoToSettings?.Invoke(this, "ocr");

    private void Finish() => Finished?.Invoke(this, EventArgs.Empty);
}