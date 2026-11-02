using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;
using QBank.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace QBank.App.Pages;

public sealed class BankRow
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
    public Question Source { get; init; } = new();
}

public sealed partial class BankPage : Page
{
    private readonly ObservableCollection<BankRow> _rows = new();
    private const int PageSize = 200;

    public BankPage()
    {
        InitializeComponent();
        QuestionList.ItemsSource = _rows;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        try
        {
            var keyword = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
            var type = FilterType.SelectedIndex switch
            {
                1 => QuestionType.Single,
                2 => QuestionType.Multiple,
                3 => QuestionType.Judgement,
                4 => QuestionType.Completion,
                _ => (QuestionType?)null,
            };
            var order = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "updated";

            var items = AppServices.Current.Bank.Query(keyword, type, 0, PageSize, order);
            _rows.Clear();
            foreach (var q in items)
            {
                _rows.Add(new BankRow
                {
                    Id = q.Id,
                    Title = q.Title,
                    Answer = string.IsNullOrWhiteSpace(q.Answer) ? "—" : q.Answer,
                    MetaText = $"{q.Type.ToChinese()} · 命中 {q.HitCount} 次 · 更新 {q.UpdatedAt.ToLocalTime():MM-dd HH:mm}" +
                               (string.IsNullOrWhiteSpace(q.Source) ? string.Empty : $" · {q.Source}"),
                    Source = q,
                });
            }

            var stats = AppServices.Current.Bank.Stats();
            BankStatsText.Text = $"共 {stats.Total} 道题 · 今日新增 {stats.TodayAdded} · 累计命中 {stats.TotalHits}" +
                                 (items.Count >= PageSize ? $" · 当前显示前 {PageSize} 条，可用搜索缩小范围" : string.Empty);
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "读取题库失败：" + ex.Message;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh();
    }

    private void OnDeleteOne(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankRow row }) return;
        try
        {
            AppServices.Current.Bank.Delete(row.Id);
            Refresh();
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "删除失败：" + ex.Message;
        }
    }

    private async void OnAddQuestion(object sender, RoutedEventArgs e)
    {
        var titleBox = new TextBox { Header = "题干", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        var optionsBox = new TextBox { Header = "选项（一行一个，可选）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
        var answerBox = new TextBox { Header = "答案" };
        var typeBox = new ComboBox { Header = "题型", SelectedIndex = 0 };
        foreach (var text in new[] { "自动判断", "单选题", "多选题", "判断题", "填空题" }) typeBox.Items.Add(text);

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(typeBox);
        panel.Children.Add(titleBox);
        panel.Children.Add(optionsBox);
        panel.Children.Add(answerBox);

        var dialog = new ContentDialog
        {
            Title = "新增题目",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            BankStatsText.Text = "题干不能为空";
            return;
        }

        var type = typeBox.SelectedIndex switch
        {
            1 => QuestionType.Single,
            2 => QuestionType.Multiple,
            3 => QuestionType.Judgement,
            4 => QuestionType.Completion,
            _ => QuestionTypeInference.Infer(title, optionsBox.Text ?? string.Empty),
        };

        try
        {
            AppServices.Current.Bank.Upsert(new Question
            {
                Title = title,
                Options = (optionsBox.Text ?? string.Empty).Trim(),
                Answer = (answerBox.Text ?? string.Empty).Trim(),
                Type = type,
                Source = "手动添加",
                Tags = "手动",
            });
            Refresh();
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "保存失败：" + ex.Message;
        }
    }

    private async void OnImportClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                await ShowDialogAsync("剪贴板里没有文字", "请先复制要导入的内容（JSON / CSV / 每行「题目#答案」）。");
                return;
            }
            var text = await content.GetTextAsync();
            DoImport(text);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导入失败", ex.Message);
        }
    }

    private async void OnImportFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".txt");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            DoImport(await File.ReadAllTextAsync(file.Path));
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导入失败", ex.Message);
        }
    }

    private void DoImport(string text)
    {
        var result = AppServices.Current.Bank.Import(text);
        Refresh();
        BankStatsText.Text = $"导入完成：新增 {result.Added}，更新 {result.Updated}，跳过 {result.Skipped}" +
                             (result.Errors.Count > 0 ? $"，{result.Errors.Count} 条有问题" : string.Empty);
    }

    private async void OnExportJson(object sender, RoutedEventArgs e) => await ExportAsync("JSON", ".json", AppServices.Current.Bank.ExportJson);
    private async void OnExportCsv(object sender, RoutedEventArgs e) => await ExportAsync("CSV", ".csv", AppServices.Current.Bank.ExportCsv);
    private async void OnExportTxt(object sender, RoutedEventArgs e) => await ExportAsync("TXT", ".txt", AppServices.Current.Bank.ExportTxt);

    private async Task ExportAsync(string label, string ext, Func<string> exporter)
    {
        try
        {
            var content = exporter();
            var package = new DataPackage();
            package.SetText(content);
            Clipboard.SetContent(package);

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"qbank-{DateTime.Now:yyyyMMdd-HHmm}",
            };
            picker.FileTypeChoices.Add(label, new List<string> { ext });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                BankStatsText.Text = $"{label} 已复制到剪贴板";
                return;
            }

            await File.WriteAllTextAsync(file.Path, content);
            BankStatsText.Text = $"已导出 {label} 到 {file.Path}（同时已复制到剪贴板）";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("导出失败", ex.Message);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        // MenuFlyout 会自动展开
        await Task.CompletedTask;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        await Task.CompletedTask;
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
