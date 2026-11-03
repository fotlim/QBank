using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using QBank.Core.Ocr;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace QBank.App.Controls;

/// <summary>框选截图：把整屏截图铺满一个无边框窗口，让用户拖一个框，松手后返回裁好的 PNG。</summary>
public sealed partial class RegionPickerWindow : Window
{
    private readonly byte[] _fullShot;
    private readonly (int X, int Y, int Width, int Height) _bounds;

    private bool _dragging;
    private Point _start;
    private Point _current;
    private bool _finished;

    /// <summary>裁好的 PNG；用户取消就是 null。</summary>
    public byte[]? Result { get; private set; }

    public event EventHandler<byte[]?>? Picked;

    public RegionPickerWindow(byte[] fullShot)
    {
        InitializeComponent();
        _fullShot = fullShot;
        _bounds = ScreenCapture.VirtualScreenBounds();

        Title = "框选要识别的区域";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        appWindow.MoveAndResize(new RectInt32(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));

        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerCaptureLost += (_, _) => _dragging = false;
        RootGrid.KeyDown += OnKeyDown;
        Closed += (_, _) => { if (!_finished) Picked?.Invoke(this, null); };

        _ = LoadShotAsync();
    }

    private async Task LoadShotAsync()
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(_fullShot.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            ShotImage.Source = bitmap;
            HintText.Text = $"按住鼠标左键拖一个框（{_bounds.Width}×{_bounds.Height} 像素），松手就开始识别";
            Activate();
            RootGrid.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            App.Trace("RegionPickerWindow.LoadShotAsync 失败：" + ex.Message);
            Close();
        }
    }

    private double Scale() => RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _start = e.GetCurrentPoint(RootGrid).Position;
        _current = _start;
        Overlay.Visibility = Visibility.Visible;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateRect();
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _current = e.GetCurrentPoint(RootGrid).Position;
        UpdateRect();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _current = e.GetCurrentPoint(RootGrid).Position;
        RootGrid.ReleasePointerCapture(e.Pointer);
        Finish();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _finished = true;
            Result = null;
            Close();
            Picked?.Invoke(this, null);
        }
    }

    private void UpdateRect()
    {
        var left = Math.Min(_start.X, _current.X);
        var top = Math.Min(_start.Y, _current.Y);
        var width = Math.Abs(_current.X - _start.X);
        var height = Math.Abs(_current.Y - _start.Y);

        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        var scale = Scale();
        SizeText.Text = $"{(int)Math.Round(width * scale)} × {(int)Math.Round(height * scale)} 像素";
    }

    private void Finish()
    {
        var left = Math.Min(_start.X, _current.X);
        var top = Math.Min(_start.Y, _current.Y);
        var width = Math.Abs(_current.X - _start.X);
        var height = Math.Abs(_current.Y - _start.Y);

        var scale = Scale();
        var x = (int)Math.Round(left * scale);
        var y = (int)Math.Round(top * scale);
        var w = (int)Math.Round(width * scale);
        var h = (int)Math.Round(height * scale);

        _finished = true;

        if (w < 8 || h < 8)
        {
            // 太小的框当误点处理，让用户再拖一次
            _finished = false;
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeText.Text = "框太小了，再拖一次";
            return;
        }

        Result = ScreenCapture.CropRegion(_fullShot, x, y, w, h);
        Close();
        Picked?.Invoke(this, Result);
    }
}