using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QBank.Core.Ocr;

/// <summary>屏幕截图（整屏 / 指定区域 / 当前前台窗口），供「图片识题」直接截图使用。</summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>截取全部屏幕（多显示器时是虚拟屏幕整体）。</summary>
    public static byte[]? CaptureAllScreens()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            x = 0;
            y = 0;
            width = GetSystemMetrics(SM_CXSCREEN);
            height = GetSystemMetrics(SM_CYSCREEN);
        }
        return CaptureRegion(x, y, width, height);
    }

    /// <summary>截取主显示器。</summary>
    public static byte[]? CapturePrimaryScreen()
    {
        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        return CaptureRegion(0, 0, width, height);
    }

    /// <summary>截取当前前台窗口（不含阴影）。</summary>
    public static byte[]? CaptureForegroundWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return CaptureAllScreens();
        if (!GetWindowRect(handle, out var rect)) return CaptureAllScreens();

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return CaptureAllScreens();

        return CaptureRegion(rect.Left, rect.Top, width, height);
    }

    /// <summary>虚拟屏幕（全部显示器合并）的像素范围。多显示器时 X/Y 可能是负数。</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreenBounds()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            width = GetSystemMetrics(SM_CXSCREEN);
            height = GetSystemMetrics(SM_CYSCREEN);
            x = 0;
            y = 0;
        }
        return (x, y, width, height);
    }

    /// <summary>从已有图片里裁一块（坐标是图片内部像素坐标，越界自动夹到边界）。用于框选截图。</summary>
    public static byte[]? CropRegion(byte[] imageBytes, int x, int y, int width, int height)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;
        if (width <= 0 || height <= 0) return null;

        try
        {
            using var source = new MemoryStream(imageBytes);
            using var bitmap = new Bitmap(source);
            x = Math.Clamp(x, 0, Math.Max(0, bitmap.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, bitmap.Height - 1));
            width = Math.Min(width, bitmap.Width - x);
            height = Math.Min(height, bitmap.Height - y);
            if (width <= 0 || height <= 0) return null;

            using var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(cropped))
            {
                graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
            }

            using var memory = new MemoryStream();
            cropped.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>截取指定矩形区域并编码成 PNG。返回 null 表示失败。</summary>
    public static byte[]? CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        width = Math.Min(width, 20000);
        height = Math.Min(height, 20000);

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>前台窗口标题，用于截图后给用户一点提示。</summary>
    public static string ForegroundWindowTitle()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return string.Empty;
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var buffer = new char[length + 1];
        GetWindowText(handle, buffer, buffer.Length);
        return new string(buffer).TrimEnd('\0');
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>图片宽高（不加载整图到内存，用于展示与裁剪提示）。</summary>
    public static (int Width, int Height)? Measure(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return (image.Width, image.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按最长边缩放图片，返回 PNG 字节（OCR 前压缩用）。</summary>
    public static byte[]? Resize(byte[] imageBytes, int maxDimension)
    {
        if (maxDimension <= 0) return imageBytes;
        try
        {
            using var source = new MemoryStream(imageBytes);
            using var image = Image.FromStream(source);
            if (image.Width <= maxDimension && image.Height <= maxDimension) return imageBytes;

            var scale = Math.Min((double)maxDimension / image.Width, (double)maxDimension / image.Height);
            var width = Math.Max(1, (int)(image.Width * scale));
            var height = Math.Max(1, (int)(image.Height * scale));

            using var resized = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            using var memory = new MemoryStream();
            resized.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return imageBytes;
        }
    }

    /// <summary>猜 MIME（给 AI 视觉接口用）。</summary>
    public static string DetectMime(byte[] imageBytes)
    {
        if (imageBytes.Length >= 8 &&
            imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            return "image/png";
        if (imageBytes.Length >= 3 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8 && imageBytes[2] == 0xFF)
            return "image/jpeg";
        if (imageBytes.Length >= 12 && imageBytes[0] == (byte)'R' && imageBytes[1] == (byte)'I' &&
            imageBytes[2] == (byte)'F' && imageBytes[3] == (byte)'F' &&
            imageBytes[8] == (byte)'W' && imageBytes[9] == (byte)'E')
            return "image/webp";
        if (imageBytes.Length >= 6 && imageBytes[0] == (byte)'G' && imageBytes[1] == (byte)'I' && imageBytes[2] == (byte)'F')
            return "image/gif";
        return "image/png";
    }

    /// <summary>粗略测量一张图片的处理耗时，便于日志。</summary>
    public static long MeasureDecodeMs(byte[] imageBytes)
    {
        var sw = Stopwatch.StartNew();
        _ = Measure(imageBytes);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}