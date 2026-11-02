using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;
using QBank.Core.Ai;
using QBank.Core.Models;

namespace QBank.App.Controls;

/// <summary>一个 AI 服务商的编辑卡片：配置 + 拉取模型列表 + 测试连接。</summary>
public sealed partial class ProviderCard : UserControl
{
    private bool _loading;

    public ProviderCard(AiProviderConfig provider)
    {
        InitializeComponent();
        Provider = provider;
        LoadFrom(provider);
    }

    public AiProviderConfig Provider { get; }

    public event EventHandler? Deleted;

    /// <summary>服务商被修改（父页面据此决定是否标记未保存）。</summary>
    public event EventHandler? Edited;

    private void LoadFrom(AiProviderConfig p)
    {
        _loading = true;
        try
        {
            NameBox.Text = p.Name;
            SelectKind(p.ProviderKind);
            BaseUrlBox.Text = p.BaseUrl;
            ApiKeyBox.Password = p.ApiKey ?? string.Empty;
            ModelCombo.Text = p.Model;
            EnabledBox.IsChecked = p.Enabled;
            TemperatureBox.Value = p.Temperature;
            MaxTokensBox.Value = p.MaxTokens;
            TimeoutBox.Value = p.TimeoutSeconds;
            ExtraParamsBox.Text = p.ExtraParamsJson;
            SystemPromptBox.Text = p.SystemPrompt;
            JsonModeBox.IsChecked = p.UseJsonMode;
            VisionBox.IsChecked = p.VisionCapable;
            PriorityBox.Value = p.Priority;
            OllamaPullButton.Visibility = IsOllama ? Visibility.Visible : Visibility.Collapsed;
            FetchModelsButton.Content = IsOllama ? "获取模型" : "获取模型";
        }
        finally
        {
            _loading = false;
        }
    }

    private bool IsOllama => (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ollama";

    private void SelectKind(string? kind)
    {
        foreach (var item in KindCombo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, kind, StringComparison.OrdinalIgnoreCase))
            {
                KindCombo.SelectedItem = ci;
                return;
            }
        }
        KindCombo.SelectedIndex = KindCombo.Items.Count - 1;
    }

    private string CurrentKind() => (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "custom";

    /// <summary>把界面上的值写回配置对象。保存设置时调用。</summary>
    public void ApplyToProvider()
    {
        var p = Provider;
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名模型" : NameBox.Text.Trim();
        p.ProviderKind = CurrentKind();
        p.BaseUrl = (BaseUrlBox.Text ?? string.Empty).Trim();
        p.ApiKey = ApiKeyBox.Password ?? string.Empty;
        p.Model = (ModelCombo.Text ?? string.Empty).Trim();
        p.Enabled = EnabledBox.IsChecked == true;
        p.Temperature = double.IsNaN(TemperatureBox.Value) ? 0.2 : TemperatureBox.Value;
        p.MaxTokens = double.IsNaN(MaxTokensBox.Value) ? 1024 : (int)MaxTokensBox.Value;
        p.TimeoutSeconds = double.IsNaN(TimeoutBox.Value) ? 60 : (int)TimeoutBox.Value;
        p.ExtraParamsJson = (ExtraParamsBox.Text ?? string.Empty).Trim();
        p.SystemPrompt = (SystemPromptBox.Text ?? string.Empty).Trim();
        p.UseJsonMode = JsonModeBox.IsChecked == true;
        p.VisionCapable = VisionBox.IsChecked == true;
        p.Priority = double.IsNaN(PriorityBox.Value) ? 0 : (int)PriorityBox.Value;
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var kind = CurrentKind();
        OllamaPullButton.Visibility = kind == "ollama" ? Visibility.Visible : Visibility.Collapsed;

        // 换类型时如果地址还是空的/是别的预设默认值，就填上该类型的默认地址
        var current = (BaseUrlBox.Text ?? string.Empty).Trim();
        var isDefault = current.Length == 0 || AiProviderConfig.CreatePresets()
            .Any(p => string.Equals(p.BaseUrl, current, StringComparison.OrdinalIgnoreCase));
        if (!isDefault && current.Length > 0) return;

        var preset = AiProviderConfig.CreatePresets()
            .FirstOrDefault(p => string.Equals(p.ProviderKind, kind, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;

        BaseUrlBox.Text = preset.BaseUrl;
        if (string.IsNullOrWhiteSpace(ModelCombo.Text)) ModelCombo.Text = preset.Model;
        JsonModeBox.IsChecked = preset.UseJsonMode;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        ApplyToProvider();
        var provider = Provider;

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            ModelStatus.Text = "先填接口地址(BaseUrl)，例如 https://api.deepseek.com/v1";
            return;
        }

        SetBusy(true, "正在从接口拉取模型列表…");
        try
        {
            var result = await AppServices.Current.ModelCatalog.FetchAsync(provider);

            if (!result.Ok)
            {
                ModelStatus.Text = AiModelCatalog.DescribeResult(result) +
                                   (string.IsNullOrWhiteSpace(result.Endpoint) ? string.Empty : $"\n请求地址：{result.Endpoint}");
                return;
            }

            var previous = (ModelCombo.Text ?? string.Empty).Trim();
            ModelCombo.Items.Clear();
            foreach (var model in result.Models)
                ModelCombo.Items.Add(model.Id);

            if (previous.Length > 0)
                ModelCombo.Text = previous;
            else
                ModelCombo.Text = result.Models[0].Id;

            var vision = result.Models.Count(m => m.VisionCapable);
            ModelStatus.Text = AiModelCatalog.DescribeResult(result) +
                               (vision > 0 ? $" · 其中 {vision} 个看起来支持图片" : string.Empty);

            // 拉到的模型里若有视觉模型且当前没勾，给用户一个提示（不自动改，避免误改）
            if (vision > 0 && VisionBox.IsChecked != true)
                ModelStatus.Text += "\n提示：列表里有视觉模型，做识图题时请勾选「支持图片输入」。";

            Edited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "拉取失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        ApplyToProvider();
        if (string.IsNullOrWhiteSpace(Provider.BaseUrl) || string.IsNullOrWhiteSpace(Provider.Model))
        {
            TestResult.Text = "先填接口地址和模型名";
            return;
        }

        SetBusy(true, "正在测试连接…");
        try
        {
            var service = new AiAnswerService();
            var result = await service.TestAsync(Provider);

            if (result.Ok)
            {
                var text = (result.Content ?? string.Empty).Trim();
                if (text.Length > 120) text = text[..120] + "…";
                TestResult.Text = $"连接正常 · {result.ElapsedMs}ms · 模型 {result.Model ?? Provider.Model}" +
                                  (text.Length > 0 ? $" · 回显：{text}" : string.Empty);
            }
            else
            {
                TestResult.Text = "测试失败：" + result.Error;
            }
        }
        catch (Exception ex)
        {
            TestResult.Text = "测试异常：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        FetchModelsButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) ModelStatus.Text = status;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Deleted?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyEdited() => Edited?.Invoke(this, EventArgs.Empty);
}
