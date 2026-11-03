using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;
using QBank.Core.Ai;
using QBank.Core.Models;
using QBank.Core.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace QBank.App.Pages;

public sealed class BankRow
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
    public string TagText { get; init; } = string.Empty;
    public Question Source { get; init; } = new();
}

public sealed partial class BankPage : Page
{
    private readonly ObservableCollection<BankRow> _rows = new();
    private int _page;
    private int _pageSize = 50;
    private int _total;
    private bool _suppressSelection;

    public BankPage()
    {
        InitializeComponent();
        QuestionList.ItemsSource = _rows;
        PageSizeBox.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReloadTagFilter();
        Refresh();
    }

    /// <summary>把题库里出现过的标签灌进筛选下拉（保留开头两项「全部标签」「★收藏」）。</summary>
    private void ReloadTagFilter()
    {
        var previous = (FilterTag.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        _suppressSelection = true;
        try
        {
            FilterTag.Items.Clear();
            FilterTag.Items.Add(new ComboBoxItem { Content = "全部标签", Tag = "" });
            FilterTag.Items.Add(new ComboBoxItem { Content = "★ 收藏", Tag = LocalBank.FavoriteTag });

            var favoriteCount = AppServices.Current.Bank.Count(null, null, LocalBank.FavoriteTag);
            if (FilterTag.Items[1] is ComboBoxItem favItem) favItem.Content = $"★ 收藏（{favoriteCount}）";

            foreach (var (tag, count) in AppServices.Current.Bank.TagStats(60))
            {
                if (tag == LocalBank.FavoriteTag) continue;
                FilterTag.Items.Add(new ComboBoxItem { Content = $"{tag}（{count}）", Tag = tag });
            }

            var restored = false;
            for (var i = 0; i < FilterTag.Items.Count; i++)
            {
                if (FilterTag.Items[i] is ComboBoxItem item && (item.Tag as string ?? string.Empty) == previous)
                {
                    FilterTag.SelectedIndex = i;
                    restored = true;
                    break;
                }
            }
            if (!restored) FilterTag.SelectedIndex = 0;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private string? CurrentKeyword() => string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();

    private QuestionType? CurrentType() => FilterType.SelectedIndex switch
    {
        1 => QuestionType.Single,
        2 => QuestionType.Multiple,
        3 => QuestionType.Judgement,
        4 => QuestionType.Completion,
        _ => null,
    };

    private string? CurrentTag()
    {
        var tag = (FilterTag.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private string CurrentOrder() => (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "updated";

    private void Refresh(bool resetPage = false)
    {
        try
        {
            if (resetPage) _page = 0;

            var keyword = CurrentKeyword();
            var type = CurrentType();
            var tag = CurrentTag();
            var pageSize = _pageSize;

            _total = AppServices.Current.Bank.Count(keyword, type, tag);
            var pageCount = Math.Max(1, (_total + pageSize - 1) / pageSize);
            if (_page > pageCount - 1) _page = pageCount - 1;
            if (_page < 0) _page = 0;

            var items = AppServices.Current.Bank.Query(keyword, type, _page * pageSize, pageSize, CurrentOrder(), tag);

            _suppressSelection = true;
            try
            {
                QuestionList.SelectedItems.Clear();
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
                        TagText = string.IsNullOrWhiteSpace(q.Tags) ? string.Empty : "# " + q.Tags,
                        Source = q,
                    });
                }
            }
            finally
            {
                _suppressSelection = false;
            }

            var stats = AppServices.Current.Bank.Stats();
            BankStatsText.Text = $"共 {stats.Total} 道题 · 今日新增 {stats.TodayAdded} · 累计命中 {stats.TotalHits}";
            PageText.Text = _total == 0
                ? "没有符合条件的题目"
                : $"筛选出 {_total} 条 · 第 {_page + 1} / {pageCount} 页";
            FirstPageButton.IsEnabled = _page > 0;
            PrevPageButton.IsEnabled = _page > 0;
            NextPageButton.IsEnabled = _page < pageCount - 1;

            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionUi();
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "读取题库失败：" + ex.Message;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh(resetPage: true);

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSelection) return;
        Refresh(resetPage: true);
    }

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PageSizeBox.SelectedItem is not ComboBoxItem item) return;
        if (int.TryParse(item.Tag as string, out var size) && size > 0) _pageSize = size;
        Refresh(resetPage: true);
    }

    private void OnFirstPage(object sender, RoutedEventArgs e)
    {
        _page = 0;
        Refresh();
    }

    private void OnPrevPage(object sender, RoutedEventArgs e)
    {
        if (_page > 0) _page--;
        Refresh();
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        _page++;
        Refresh();
    }

    private List<BankRow> SelectedRows() => QuestionList.SelectedItems.OfType<BankRow>().ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        var count = QuestionList.SelectedItems.Count;
        SelectionText.Text = count == 0 ? "没有选中题目" : $"已选中 {count} 道";
        BulkButton.IsEnabled = count > 0;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        QuestionList.SelectAll();
        UpdateSelectionUi();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        QuestionList.SelectedItems.Clear();
        UpdateSelectionUi();
    }

    private async void OnDeleteOne(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankRow row }) return;
        try
        {
            AppServices.Current.Bank.Delete(row.Id);
            ReloadTagFilter();
            Refresh();
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "删除失败：" + ex.Message;
        }
        await Task.CompletedTask;
    }

    private async void OnEditOne(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankRow row }) return;
        await EditQuestionAsync(row.Source, isNew: false);
    }

    private async void OnAddQuestion(object sender, RoutedEventArgs e) => await EditQuestionAsync(new Question(), isNew: true);

    /// <summary>新增与编辑共用同一个对话框。</summary>
    private async Task EditQuestionAsync(Question question, bool isNew)
    {
        var typeBox = new ComboBox { Header = "题型", SelectedIndex = 0 };
        foreach (var text in new[] { "自动判断", "单选题", "多选题", "判断题", "填空题" }) typeBox.Items.Add(text);
        typeBox.SelectedIndex = isNew
            ? 0
            : question.Type switch
            {
                QuestionType.Single => 1,
                QuestionType.Multiple => 2,
                QuestionType.Judgement => 3,
                QuestionType.Completion => 4,
                _ => 0,
            };

        var titleBox = new TextBox
        {
            Header = "题干",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            Text = question.Title,
        };
        var optionsBox = new TextBox
        {
            Header = "选项（一行一个，可选）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 70,
            Text = question.Options,
        };
        var answerBox = new TextBox { Header = "答案", Text = question.Answer };
        var tagsBox = new TextBox { Header = "标签（空格分隔，可留空）", Text = question.Tags };

        var panel = new StackPanel { Spacing = 10, Width = 440 };
        panel.Children.Add(typeBox);
        panel.Children.Add(titleBox);
        panel.Children.Add(optionsBox);
        panel.Children.Add(answerBox);
        panel.Children.Add(tagsBox);

        var dialog = new ContentDialog
        {
            Title = isNew ? "新增题目" : "编辑题目",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
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

        var options = (optionsBox.Text ?? string.Empty).Trim();
        var type = typeBox.SelectedIndex switch
        {
            1 => QuestionType.Single,
            2 => QuestionType.Multiple,
            3 => QuestionType.Judgement,
            4 => QuestionType.Completion,
            _ => QuestionTypeInference.Infer(title, options),
        };

        try
        {
            var target = isNew
                ? new Question()
                : AppServices.Current.Bank.GetById(question.Id) ?? new Question();

            target.Title = title;
            target.Options = options;
            target.Answer = (answerBox.Text ?? string.Empty).Trim();
            target.Tags = TagList.Join(TagList.Parse(tagsBox.Text));
            target.Type = type;
            if (string.IsNullOrWhiteSpace(target.Source)) target.Source = "手动添加";

            if (isNew)
            {
                AppServices.Current.Bank.Upsert(target);
                BankStatsText.Text = "已新增 1 道题";
            }
            else if (!AppServices.Current.Bank.Update(target))
            {
                BankStatsText.Text = "保存失败：题库里已经有题干相同的题目了";
                return;
            }
            else
            {
                BankStatsText.Text = "已保存修改";
            }

            ReloadTagFilter();
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("保存失败", ex.Message);
        }
    }

    private async Task<string?> AskTextAsync(string title, string placeholder, string initial = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial, Width = 360 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var text = (box.Text ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>批量改标签：mode 为 add/remove；tag 为空时按删除全部标签处理。</summary>
    private void ApplyBulkTag(IEnumerable<BankRow> rows, string? tag, bool remove)
    {
        var touched = new List<Question>();
        foreach (var row in rows)
        {
            var question = AppServices.Current.Bank.GetById(row.Id);
            if (question is null) continue;

            var tags = TagList.Parse(question.Tags);
            if (tag is null && remove)
            {
                tags.Clear();
            }
            else if (tag is not null)
            {
                if (remove) tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                else if (!tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) tags.Add(tag);
            }

            question.Tags = TagList.Join(tags);
            touched.Add(question);
        }

        var changed = AppServices.Current.Bank.SaveTags(touched);
        ReloadTagFilter();
        Refresh();
        BankStatsText.Text = $"已更新 {changed} 道题的标签";
    }

    private async void OnBulkAddTag(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var panel = new StackPanel { Spacing = 8 };
        var box = new TextBox { PlaceholderText = "例如：错题 / 第3章 / 期末", Width = 360 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = $"会加到选中的 {rows.Count} 道题上（空格分隔可一次写多个）",
            FontSize = 12,
        });
        var dialog = new ContentDialog
        {
            Title = "加标签",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var tags = TagList.Parse(box.Text).Select(TagList.Sanitize).Where(t => t.Length > 0).ToList();
        if (tags.Count == 0) return;

        foreach (var tag in tags) ApplyBulkTag(rows, tag, remove: false);
    }

    private async void OnBulkRemoveTag(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var known = AppServices.Current.Bank.AllTags();
        var combo = new ComboBox { Width = 360, PlaceholderText = "选一个标签" };
        foreach (var tag in known) combo.Items.Add(tag);
        combo.Items.Add("（清空所有标签）");
        combo.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "去掉标签",
            Content = combo,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (combo.SelectedIndex == known.Count) ApplyBulkTag(rows, null, remove: true);
        else if (combo.SelectedItem is string tag) ApplyBulkTag(rows, tag, remove: true);
    }

    private void OnBulkFavorite(object sender, RoutedEventArgs e) => ApplyBulkTag(SelectedRows(), LocalBank.FavoriteTag, remove: false);

    private void OnBulkUnfavorite(object sender, RoutedEventArgs e) => ApplyBulkTag(SelectedRows(), LocalBank.FavoriteTag, remove: true);

    private async void OnBulkAutoTag(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var cfg = AppServices.Current.Config.Config;
        var questions = rows
            .Select(r => AppServices.Current.Bank.GetById(r.Id))
            .Where(q => q is not null)
            .Select(q => q!)
            .ToList();

        BankStatsText.Text = $"正在给 {questions.Count} 道题打标签…";
        try
        {
            var (tagged, skipped, failed, errors) = await AppServices.Current.Tagger.TagManyAsync(
                questions, cfg.AutoTag, cfg, AppServices.Current.Bank.AllTags());

            var changed = AppServices.Current.Bank.SaveTags(questions.Where(q => !string.IsNullOrWhiteSpace(q.Tags)));
            ReloadTagFilter();
            Refresh();
            BankStatsText.Text = $"自动打标签完成：用了 {cfg.AutoTag.Describe()}，改动 {changed} 道（新打 {tagged} / 跳过 {skipped} / 失败 {failed}）" +
                                 (errors.Count > 0 ? $" · {errors[0]}" : string.Empty);
        }
        catch (Exception ex)
        {
            BankStatsText.Text = "自动打标签失败：" + ex.Message;
        }
    }

    private async void OnBulkDelete(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = $"删除 {rows.Count} 道题？",
            Content = new TextBlock { Text = "删掉之后没法撤销（除非你有备份）。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var deleted = 0;
        foreach (var row in rows)
        {
            if (AppServices.Current.Bank.Delete(row.Id)) deleted++;
        }
        ReloadTagFilter();
        Refresh();
        BankStatsText.Text = $"已删除 {deleted} 道题";
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
        ReloadTagFilter();
        Refresh(resetPage: true);
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