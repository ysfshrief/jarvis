using Jarvis.Core.Files;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Jarvis.Platform.Windows;

/// <summary>
/// Reads text in images with the OCR engine built into Windows (offline, free). Uses every installed
/// recognizer among the user's languages plus English and Arabic, and keeps the richest result.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public bool IsAvailable
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
            catch { return false; }
        }
    }

    public string Name => "Windows OCR";

    public async Task<string?> RecognizeAsync(string imagePath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath)).AsTask(ct).ConfigureAwait(false);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        return await RecognizeAsync(decoder, ct).ConfigureAwait(false);
    }

    public async Task<string?> RecognizeAsync(BitmapDecoder decoder, CancellationToken ct)
    {
        var max = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform();
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            var scale = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            transform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

        string? best = null;
        var bestWords = -1;
        foreach (var engine in Engines())
        {
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            var words = result.Lines.Sum(l => l.Words.Count);
            if (words > bestWords)
            {
                bestWords = words;
                best = string.Join("\n", result.Lines.Select(l => l.Text));
            }
        }
        return best;
    }

    private static IEnumerable<OcrEngine> Engines()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } profile && seen.Add(profile.RecognizerLanguage.LanguageTag)) yield return profile;
        foreach (var tag in new[] { "en-US", "ar-EG", "ar-SA", "ar" })
        {
            var lang = new Language(tag);
            if (!OcrEngine.IsLanguageSupported(lang) || seen.Contains(lang.LanguageTag)) continue;
            if (OcrEngine.TryCreateFromLanguage(lang) is { } e && seen.Add(e.RecognizerLanguage.LanguageTag)) yield return e;
        }
    }
}
