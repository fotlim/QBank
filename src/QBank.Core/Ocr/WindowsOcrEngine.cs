using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace QBank.Core.Ocr;

/// <summary>
/// Windows 系统内置 OCR（Windows.Media.Ocr）：不用下载模型、完全离线。
/// 需要系统已安装对应语言的 OCR 语言包；中文语言包在中文版 Windows 上默认就有。
/// </summary>
public static class WindowsOcrEngine
{
    public const string EngineName = "windows";

    /// <summary>系统里装了哪些 OCR 语言。</summary>
    public static List<string> AvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>挑一个可用的识别语言：优先指定语言，其次中文，最后随系统。</summary>
    public static OcrEngine? CreateEngine(string? preferredLanguage)
    {
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        if (available.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var wanted = preferredLanguage.Trim();
            var exact = available.FirstOrDefault(l =>
                string.Equals(l.LanguageTag, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.NativeName, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.DisplayName, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(exact);
                if (engine is not null) return engine;
            }

            // zh-Hans-CN / zh-CN 这类前缀匹配
            var prefix = wanted.Split('-')[0];
            var partial = available.FirstOrDefault(l =>
                l.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(partial);
                if (engine is not null) return engine;
            }
        }

        var chinese = available.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (chinese is not null)
        {
            var engine = OcrEngine.TryCreateFromLanguage(chinese);
            if (engine is not null) return engine;
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>识别一张图片（字节数组）。</summary>
    public static async Task<OcrResult> RecognizeAsync(byte[] imageBytes, string? language = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OcrResult { Engine = EngineName };

        if (imageBytes is null || imageBytes.Length == 0)
            return OcrResult.Fail(EngineName, "图片内容为空");

        try
        {
            var engine = CreateEngine(language);
            if (engine is null)
                return OcrResult.Fail(EngineName, "系统没有可用的 OCR 语言包（可在「设置 → 时间和语言 → 语言」里添加中文语言包的 OCR 组件）");

            SoftwareBitmap bitmap;
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(imageBytes);
                    await writer.StoreAsync().AsTask(ct);
                    await writer.FlushAsync().AsTask(ct);
                    writer.DetachStream();
                }

                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

                var transform = BuildTransform(decoder, out var note);
                if (note is not null) result.Warnings.Add(note);

                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(ct);
            }

            using (bitmap)
            {
                var ocr = await engine.RecognizeAsync(bitmap).AsTask(ct);
                result.Text = string.Join("\n", ocr.Lines.Select(l => l.Text));

                foreach (var line in ocr.Lines)
                {
                    var item = new OcrTextLine { Text = line.Text };
                    var first = line.Words.FirstOrDefault();
                    if (first is not null)
                    {
                        item.Left = first.BoundingRect.X;
                        item.Top = first.BoundingRect.Y;
                    }

                    if (line.Words.Count > 0)
                    {
                        var minX = line.Words.Min(w => w.BoundingRect.X);
                        var minY = line.Words.Min(w => w.BoundingRect.Y);
                        var maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                        var maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                        item.Left = minX;
                        item.Top = minY;
                        item.Width = maxX - minX;
                        item.Height = maxY - minY;
                    }

                    result.Lines.Add(item);
                }
            }

            result.Confidence = result.Text.Length > 0 ? 0.85 : 0;
            if (result.Text.Length == 0)
                result.Warnings.Add("没有识别出文字，可能图片里没有文本或者太模糊");
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Fail(EngineName, "已取消");
        }
        catch (Exception ex)
        {
            return OcrResult.Fail(EngineName, "Windows OCR 失败：" + ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    private static BitmapTransform BuildTransform(BitmapDecoder decoder, out string? note)
    {
        note = null;
        var transform = new BitmapTransform();
        var maxDimension = (uint)OcrEngine.MaxImageDimension;

        if (decoder.PixelWidth <= maxDimension && decoder.PixelHeight <= maxDimension)
            return transform;

        var scale = Math.Min((double)maxDimension / decoder.PixelWidth, (double)maxDimension / decoder.PixelHeight);
        note = $"图片过大，已按 {scale:P0} 缩放后再识别";
        transform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
        transform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
        transform.InterpolationMode = BitmapInterpolationMode.Linear;
        return transform;
    }
}