using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QBank.App.Services;
using QBank.Core.Storage;

namespace QBank.App.Pages;

public sealed class HistoryRow
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string MetaText { get; init; } = string.Empty;
    public HistoryRecord Source { get; init; } = new();
}

public sealed partial class HistoryPage : Page
{
    private readonly ObservableCollection<HistoryRow> _rows = new();
    private int _page;
    private int _pageSize = 50;
    private int _total;

    public HistoryPage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _rows;
        PageSizeBox.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh(bool resetPage = false)
    {
        try
        {
            if (resetPage) _page = 0;

            var keyword = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
            var kind = (KindFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(kind)) kind = null;
            var onlySuccess = OnlySuccess.IsChecked == true;

            _total = AppServices.Current.History.Count(keyword, kind, onlySuccess);
            var pageCount = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            if (_page > pageCount - 1) _page = pageCount - 1;
            if (_page < 0) _page = 0;

            var items = AppServices.Current.History.Query(
                keyword: keyword,
                sourceKind: kind,
                onlySuccess: onlySuccess,
                from: null,
                offset: _page * _pageSize,
                limit: _pageSize);

            _rows.Clear();
            foreach (var record in items)
            {
                _rows.Add(new HistoryRow
                {
                    Id = record.Id,
                    Title = record.Title,
                    Answer = string.IsNullOrWhiteSpace(record.Answer) ? "—" : record.Answer,
                    MetaText = $"{record.TimeText} · {record.KindText}" +
                               (string.IsNullOrWhiteSpace(record.SourceName) ? string.Empty : $" · {record.SourceName}") +
                               $" · {(record.Success ? "成功" : "未命中")} · {record.ElapsedMs}ms",
                    Source = record,
                });
            }

            var stats = AppServices.Current.History.Stats();
            HistoryStatsText.Text = $"共 {stats.Total} 条 · 今日 {stats.Today} · 近七天 {stats.Week} · " +
                                    $"成功率 {stats.SuccessRate:F1}% · 平均 {stats.AvgElapsedMs}ms";
            PageText.Text = _total == 0
                ? "没有符合条件的记录"
                : $"筛选出 {_total} 条 · 第 {_page + 1} / {pageCount} 页";
            FirstPageButton.IsEnabled = _page > 0;
            PrevPageButton.IsEnabled = _page > 0;
            NextPageButton.IsEnabled = _page < pageCount - 1;
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            HistoryStatsText.Text = "读取历史失败：" + ex.Message;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh(resetPage: true);

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

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh(resetPage: true);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh(resetPage: true);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private async void OnPrune(object sender, RoutedEventArgs e)
    {
        var box = new NumberBox
        {
            Header = "保留最近多少天的记录",
            Value = 30,
            Minimum = 1,
            Maximum = 3650,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var dialog = new ContentDialog
        {
            Title = "清理旧记录",
            Content = box,
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var removed = AppServices.Current.History.Prune((int)box.Value);
        Refresh();
        HistoryStatsText.Text = $"已清理 {removed} 条记录";
    }

    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空全部历史？",
            Content = new TextBlock { Text = "这个操作不可撤销。本地题库里的题目不受影响。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var removed = AppServices.Current.History.Clear();
        Refresh();
        HistoryStatsText.Text = $"已清空 {removed} 条记录";
    }

    private void OnSearchAgain(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryRow row }) return;
        if (Frame.CanGoBack) Frame.GoBack();

        // 切到搜题页并填入题干
        var main = App.MainAppWindow as MainWindow;
        main?.NavigateToSearch(row.Source.Title, row.Source.Options ?? string.Empty);
    }
}
