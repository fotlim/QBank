using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QBank.App.Controls;
using QBank.App.Services;
using QBank.Core.Models;
using QBank.Core.Ocr;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace QBank.App.Pages;

/// <summary>结果列表里的一行。</summary>
public sealed class SearchResultItem
{
    public string Answer { get; init; } = string.Empty;
    public string QuestionText { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string ScoreText { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    public SearchHit? Hit { get; init; }
}

public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<SearchResultItem> _results = new();
    private CancellationTokenSource? _cts;
    private SearchResponse? _lastResponse;

    public SearchPage()
    {
        InitializeComponent();
        ResultList.ItemsSource = _results;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildSourceToggles();
        UpdateOcrStatus();
    }

    /// <summary>从历史记录跳进来时预填题干。</summary>
    public void Prefill(string title, string options)
    {
        TitleBox.Text = title ?? string.Empty;
        OptionsBox.Text = options ?? string.Empty;
        _ = RunSearchAsync();
    }

    private void BuildSourceToggles()
    {
        SourcePanel.Children.Clear();
        var cfg = AppServices.Current.Config.Config;

        AddToggle("本地题库 + 缓存", cfg.Search.UseLocalBank, v => cfg.Search.UseLocalBank = v);
        AddToggle("局域网共享", cfg.Search.UseLanShare, v => cfg.Search.UseLanShare = v);
        AddToggle("在线题库（OCS）", cfg.Search.UseOcs, v => cfg.Search.UseOcs = v);
        AddToggle("AI 模型", cfg.Search.UseAi, v => cfg.Search.UseAi = v);
        AddToggle("网页搜索（省 token）", cfg.Search.UseWeb, v => cfg.Search.UseWeb = v);

        void AddToggle(string text, bool value, Action<bool> apply)
        {
            var index = SourcePanel.Children.Count;
            var box = new CheckBox
            {
                Content = text,
                IsChecked = value,
                MinWidth = 0,
                Margin = new Thickness(0, 0, 0, 2)
            };
            Grid.SetRow(box, index / 2);
            Grid.SetColumn(box, index % 2);
            box.Checked += (_, _) => { apply(true); AppServices.Current.SaveConfig(); };
            box.Unchecked += (_, _) => { apply(false); AppServices.Current.SaveConfig(); };
            SourcePanel.Children.Add(box);
        }
    }

    private void UpdateOcrStatus()
    {
        try
        {
            OcrStatusText.Text = AppServices.Current.Ocr.DescribeCurrent();
        }
        catch (Exception ex)
        {
            OcrStatusText.Text = "识别引擎状态不可用：" + ex.Message;
        }
    }

    private QuestionType CurrentType() => TypeCombo.SelectedIndex switch
    {
        1 => QuestionType.Single,
        2 => QuestionType.Multiple,
        3 => QuestionType.Judgement,
        4 => QuestionType.Completion,
        _ => QuestionTypeInference.Infer(TitleBox.Text ?? string.Empty, OptionsBox.Text ?? string.Empty),
    };

    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        e.Handled = true;
        _ = RunSearchAsync();
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var title = (TitleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            await ShowDialogAsync("先填题干", "请先把题目内容粘贴到「题干」框里。");
            return;
        }

        var cfg = AppServices.Current.Config.Config;
        var request = new SearchRequest
        {
            Title = title,
            Options = (OptionsBox.Text ?? string.Empty).Trim(),
            Type = CurrentType(),
            Limit = Math.Max(cfg.Search.DefaultLimit, 5),
            UseLocalBank = cfg.Search.UseLocalBank,
            UseLanShare = cfg.Search.UseLanShare,
            UseOcs = cfg.Search.UseOcs,
            UseAi = cfg.Search.UseAi,
            UseWeb = cfg.Search.UseWeb,
            TimeoutSeconds = cfg.Search.TimeoutSeconds,
            RecordHistory = cfg.Search.RecordHistory,
        };

        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(cfg.Search.TimeoutSeconds, 10, 300)));

        SearchButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SearchProgress.Visibility = Visibility.Visible;
        ResultMeta.Text = "正在搜索…";
        EmptyHint.Visibility = Visibility.Collapsed;

        try
        {
            var response = await AppServices.Current.Search.SearchAsync(request, _cts.Token);
            _lastResponse = response;
            RenderResults(response);
        }
        catch (OperationCanceledException)
        {
            ResultMeta.Text = "已取消";
        }
        catch (Exception ex)
        {
            ResultMeta.Text = "搜索失败：" + ex.Message;
            AppServices.Current.Log("搜题异常：" + ex);
        }
        finally
        {
            SearchButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderResults(SearchResponse response)
    {
        _results.Clear();

        foreach (var hit in response.Ranked)
        {
            var outcome = response.Outcomes.FirstOrDefault(o => o.Hits.Contains(hit));
            _results.Add(new SearchResultItem
            {
                Hit = hit,
                Answer = string.IsNullOrWhiteSpace(hit.Answer) ? "（空）" : hit.Answer,
                QuestionText = string.IsNullOrWhiteSpace(hit.Question) ? response.Title : hit.Question,
                SourceText = outcome?.SourceName ?? "未知来源",
                ScoreText = hit.Score > 0 ? hit.Score.ToString("P0") : string.Empty,
                Note = hit.Note ?? string.Empty,
            });
        }

        var best = response.Ranked.FirstOrDefault();
        if (best is not null)
        {
            BestCard.Visibility = Visibility.Visible;
            BestAnswerText.Text = best.Answer;
            var source = response.Outcomes.FirstOrDefault(o => o.Hits.Contains(best));
            BestSource.Text = source is null
                ? string.Empty
                : $"来自 {source.SourceName} · {best.Score:P0}";
        }
        else
        {
            BestCard.Visibility = Visibility.Collapsed;
        }

        var failed = response.Outcomes.Where(o => !o.Ok).ToList();
        var okCount = response.Outcomes.Count(o => o.Ok);
        ResultMeta.Text = $"{response.Ranked.Count} 条候选 · {okCount} 个来源成功" +
                          (failed.Count > 0 ? $"，{failed.Count} 个失败" : string.Empty) +
                          $" · {response.ElapsedMs}ms" +
                          (response.FromCache ? " · 命中缓存" : string.Empty);

        EmptyHint.Visibility = response.Ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (response.Ranked.Count == 0)
        {
            EmptyHintDetail.Text = failed.Count > 0
                ? "没有找到结果，来源报错：" + string.Join("；", failed.Select(f => $"{f.SourceName}={f.Error}"))
                : "没有找到结果。可以换个说法，或到设置里换一个来源。";
        }

        var notices = response.Outcomes.SelectMany(o => o.Notices).Distinct().ToList();
        if (notices.Count > 0)
            AppServices.Current.Log("搜题提示：" + string.Join(" | ", notices));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        TitleBox.Text = string.Empty;
        OptionsBox.Text = string.Empty;
        _results.Clear();
        BestCard.Visibility = Visibility.Collapsed;
        ResultMeta.Text = string.Empty;
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHintDetail.Text = "还没有结果。左边粘上题干，然后点「开始搜题」。";
    }

    /// <summary>从引导进来时把焦点放到题干框上。</summary>
    public void FocusQuestionInput()
    {
        try
        {
            TitleBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            AppServices.Current.Log("聚焦题干框失败：" + ex.Message);
        }
    }

    private void OnReplayOnboarding(object sender, RoutedEventArgs e)
    {
        (App.MainAppWindow as MainWindow)?.ReplayOnboarding();
    }

    private void OnGoSettings(object sender, RoutedEventArgs e)
    {
        (App.MainAppWindow as MainWindow)?.NavigateTo("settings");
    }

    private void OnCopyBest(object sender, RoutedEventArgs e)
    {
        if (_lastResponse?.Best is null) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_lastResponse.Best.Answer);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OnSaveBest(object sender, RoutedEventArgs e)
    {
        if (_lastResponse?.Best is not { } best) return;
        var source = _lastResponse.Outcomes.FirstOrDefault(o => o.Hits.Contains(best));
        var question = new Question
        {
            Title = _lastResponse.Title,
            Type = _lastResponse.Type,
            Options = _lastResponse.Options,
            Answer = best.Answer,
            Source = source?.SourceName ?? "手动保存",
            Tags = "手动",
        };
        try
        {
            AppServices.Current.Bank.ImportMany(new[] { question });
            ResultMeta.Text = "已存入本地题库";
        }
        catch (Exception ex)
        {
            ResultMeta.Text = "保存失败：" + ex.Message;
        }
    }

    /// <summary>框选截图：先把整个屏幕拍下来，再让用户在覆盖窗口里拉框，最后只识别框里那块。</summary>
    private async void OnOcrRegion(object sender, RoutedEventArgs e)
    {
        var full = ScreenCapture.CaptureAllScreens();
        if (full is null)
        {
            await ShowDialogAsync("截图失败", "没能截到屏幕内容，可能是权限或显示环境问题。");
            return;
        }

        OcrStatusText.Text = "在屏幕上拉一个框，松手就开始识别…";
        var picker = new RegionPickerWindow(full);
        picker.Picked += async (_, bytes) =>
        {
            if (bytes is null || bytes.Length == 0)
            {
                OcrStatusText.Text = "框选已取消。";
                return;
            }
            await RecognizeIntoTitleAsync(bytes);
        };
        picker.Activate();
    }

    private async void OnOcrScreenshot(object sender, RoutedEventArgs e)
    {
        var bytes = ScreenCapture.CapturePrimaryScreen();
        if (bytes is null)
        {
            await ShowDialogAsync("截图失败", "没能截到屏幕内容，可能是权限或显示环境问题。");
            return;
        }
        await RecognizeIntoTitleAsync(bytes);
    }

    /// <summary>Ctrl+V：剪贴板里有图片就直接识别（Win+Shift+S 截完图就能用）。</summary>
    private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PasteImageFromClipboardAsync();
    }

    private async Task PasteImageFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var bytes = await ReadStreamAsync(stream);
                if (bytes.Length > 0)
                {
                    await RecognizeIntoTitleAsync(bytes);
                    return;
                }
            }

            // 从资源管理器复制的图片文件
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                foreach (var item in items.OfType<StorageFile>())
                {
                    var ext = Path.GetExtension(item.Name).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")) continue;
                    using var fileStream = await item.OpenStreamForReadAsync();
                    using var ms = new MemoryStream();
                    await fileStream.CopyToAsync(ms);
                    await RecognizeIntoTitleAsync(ms.ToArray());
                    return;
                }
            }

            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    TitleBox.Text = text.Trim();
                    OcrStatusText.Text = "剪贴板里是文字，已经贴到题干框了。";
                    return;
                }
            }

            OcrStatusText.Text = "剪贴板里没有图片。可以用 Win+Shift+S 截一张，再按 Ctrl+V。";
        }
        catch (Exception ex)
        {
            OcrStatusText.Text = "粘贴失败：" + ex.Message;
        }
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStreamWithContentType stream)
    {
        using var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }

    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Bitmap) || e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松手就开始识别题目";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnPageDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await e.DataView.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var bytes = await ReadStreamAsync(stream);
                if (bytes.Length > 0)
                {
                    OcrStatusText.Text = "已接住拖进来的图片，正在识别…";
                    await RecognizeIntoTitleAsync(bytes);
                    return;
                }
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items.OfType<StorageFile>())
                {
                    var ext = Path.GetExtension(item.Name).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")) continue;
                    using var fileStream = await item.OpenStreamForReadAsync();
                    using var ms = new MemoryStream();
                    await fileStream.CopyToAsync(ms);
                    OcrStatusText.Text = "已接住拖进来的图片，正在识别…";
                    await RecognizeIntoTitleAsync(ms.ToArray());
                    return;
                }
            }

            OcrStatusText.Text = "只认识图片文件（png / jpg / bmp / webp）。";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("拖拽识别失败", ex.Message);
        }
    }

    private async void OnOcrFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            using var stream = await file.OpenStreamForReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            await RecognizeIntoTitleAsync(ms.ToArray());
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("打开图片失败", ex.Message);
        }
    }

    private async Task RecognizeIntoTitleAsync(byte[] imageBytes)
    {
        OcrStatusText.Text = "正在识别图片…";
        try
        {
            var (ocr, parsed) = await AppServices.Current.Ocr.RecognizeQuestionAsync(imageBytes);

            if (!ocr.Ok)
            {
                OcrStatusText.Text = "识别失败：" + ocr.Error;
                await ShowDialogAsync("识别失败", ocr.Error ?? "未知错误");
                return;
            }

            if (parsed.Title.Length > 0)
                TitleBox.Text = parsed.Title;
            else if (ocr.Text.Length > 0)
                TitleBox.Text = ocr.Text;

            if (parsed.Options.Length > 0)
                OptionsBox.Text = parsed.Options;

            OcrStatusText.Text = $"识别完成（{ocr.Engine}，{ocr.Lines.Count} 行）。可手动修正后搜题。";
        }
        catch (Exception ex)
        {
            OcrStatusText.Text = "识别异常：" + ex.Message;
            await ShowDialogAsync("识别异常", ex.Message);
        }
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
