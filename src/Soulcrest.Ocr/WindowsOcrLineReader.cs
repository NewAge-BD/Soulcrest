using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.Core.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using OcrLine = Soulcrest.Core.Text.OcrLine;
using OcrWord = Soulcrest.Core.Text.OcrWord;

namespace Soulcrest.Ocr;

/// <summary>
/// Windows OCR over a captured region (technique from Grindcrest CompanionWindowsOcrRecognizer:
/// copy into a Gray8 SoftwareBitmap). The region is upscaled first because the loot feed text is
/// only ~16 px high at 1440p.
/// </summary>
public sealed class WindowsOcrLineReader
{
    private OcrEngine _engine;
    private IReadOnlyList<OcrEngine> _alternatives = [];
    private bool _automatic;
    private DateTime _nextProbe;
    public string? DetectedLanguage { get; private set; }
    public bool Automatic => _automatic;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WindowsOcrLineReader(OcrEngine engine) => _engine = engine;

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    public int Scale { get; init; } = 2;

    public static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <param name="scale">Upscaling before OCR; small text (pet cards) needs 3–4.</param>
    public static WindowsOcrLineReader Create(string languageTag, int scale = 2)
    {
        if (languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var available = OcrEngine.AvailableRecognizerLanguages
                .Where(l => l.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                    || l.LanguageTag.StartsWith("de-", StringComparison.OrdinalIgnoreCase)
                    || l.LanguageTag is "en" or "de")
                .OrderBy(l => l.LanguageTag == "en-US" ? 0 : l.LanguageTag == "de-DE" ? 1 : 2)
                .GroupBy(l => l.LanguageTag.Split('-')[0]).Select(g => g.First())
                .Select(OcrEngine.TryCreateFromLanguage).OfType<OcrEngine>().ToArray();
            if (available.Length == 0)
                throw new InvalidOperationException("Windows OCR: Englisches oder deutsches OCR-Sprachpaket installieren.");
            return new WindowsOcrLineReader(available[0]) { Scale = scale, _automatic = true, _alternatives = available };
        }
        var language = new Language(languageTag);
        if (!OcrEngine.IsLanguageSupported(language))
            throw new InvalidOperationException(
                $"Windows-OCR-Sprache {languageTag} ist nicht installiert (Einstellungen → Zeit und Sprache → Sprache).");
        return new WindowsOcrLineReader(OcrEngine.TryCreateFromLanguage(language)
            ?? throw new InvalidOperationException($"Windows OCR für {languageTag} konnte nicht gestartet werden."))
        {
            Scale = scale,
        };
    }

    public async Task<IReadOnlyList<OcrLine>> ReadAsync(Bitmap source, CancellationToken cancellationToken = default)
    {
        var scale = Math.Max(1, Scale);
        var width = Math.Min(source.Width * scale, (int)OcrEngine.MaxImageDimension);
        var height = Math.Min(source.Height * scale, (int)OcrEngine.MaxImageDimension);
        var factorX = (double)width / source.Width;
        var factorY = (double)height / source.Height;

        var gray = ToGray(source, width, height);
        var buffer = CryptographicBuffer.CreateFromByteArray(gray);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Gray8, width, height);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OcrResult result;
        try
        {
            result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            if (_automatic)
            {
                var detected = GameLanguageDetector.Detect(result.Lines.Select(l => l.Text));
                if (detected is not null)
                {
                    DetectedLanguage = detected;
                    var preferred = _alternatives.FirstOrDefault(e => e.RecognizerLanguage.LanguageTag.StartsWith(detected, StringComparison.OrdinalIgnoreCase));
                    if (preferred is not null && !ReferenceEquals(preferred, _engine))
                    {
                        _engine = preferred;
                        result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (DateTime.UtcNow >= _nextProbe)
                {
                    _nextProbe = DateTime.UtcNow.AddSeconds(3);
                    var other = _alternatives.FirstOrDefault(e => !ReferenceEquals(e, _engine));
                    if (other is not null)
                    {
                        var candidate = await other.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
                        var hint = GameLanguageDetector.Detect(candidate.Lines.Select(l => l.Text));
                        if (hint is not null && other.RecognizerLanguage.LanguageTag.StartsWith(hint, StringComparison.OrdinalIgnoreCase))
                        {
                            _engine = other;
                            DetectedLanguage = hint;
                            result = candidate;
                        }
                        else if (result.Lines.Count == 0 && candidate.Lines.Count > 0)
                            result = candidate;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
                continue;
            var left = line.Words.Min(w => w.BoundingRect.X);
            var top = line.Words.Min(w => w.BoundingRect.Y);
            var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            var words = line.Words.Select(w => new OcrWord(w.Text, w.BoundingRect.X / factorX, w.BoundingRect.Y / factorY,
                w.BoundingRect.Width / factorX, w.BoundingRect.Height / factorY)).ToList();
            lines.Add(new OcrLine(line.Text, left / factorX, top / factorY, (right - left) / factorX, (bottom - top) / factorY) { Words = words });
        }
        return lines;
    }

    private static byte[] ToGray(Bitmap source, int width, int height)
    {
        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        var data = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bgra = new byte[stride * height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var gray = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    // Max channel keeps white and coloured text bright on the dark translucent feed band.
                    gray[y * width + x] = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
                }
            }
            return gray;
        }
        finally
        {
            scaled.UnlockBits(data);
        }
    }
}
