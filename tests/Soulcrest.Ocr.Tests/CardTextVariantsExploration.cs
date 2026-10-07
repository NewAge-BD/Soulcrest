using System.Drawing;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>Exploration: which preprocessing makes Windows OCR read short card texts like "4/5".</summary>
public sealed class CardTextVariantsExploration(ITestOutputHelper output)
{
    [Fact]
    public async Task CompareVariants()
    {
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-locked.png"));
        var cards = new List<OpenCvSharp.Rect>();
        foreach (var (y, h) in new[] { (264, 155), (433, 155), (604, 155), (775, 154) })
            foreach (var (x, w) in new[] { (97, 107), (217, 115), (341, 114) })
                cards.Add(new OpenCvSharp.Rect(x, y, w, h));
        foreach (var scale in new[] { 3, 4, 6 })
        {
            var reader = WindowsOcrLineReader.Create("en-US", scale);
            foreach (var variant in new[] { "max", "mask", "maskwide" })
            {
                var results = new List<string>();
                foreach (var card in cards)
                {
                    var left = variant == "maskwide" ? 0.10 : 0.35;
                    var rect = new OpenCvSharp.Rect(card.X + (int)(card.Width * left), card.Y + (int)(card.Height * 0.835), (int)(card.Width * (1 - left)), (int)(card.Height * 0.09));
                    using var band = new Mat(image, rect);
                    using var gray = new Mat();
                    if (variant == "max")
                    {
                        var ch = Cv2.Split(band);
                        Cv2.Max(ch[0], ch[1], gray); Cv2.Max(gray, ch[2], gray);
                        Cv2.Normalize(gray, gray, 0, 255, NormTypes.MinMax);
                        Cv2.BitwiseNot(gray, gray);
                        foreach (var c in ch) c.Dispose();
                    }
                    else
                    {
                        using var hsv = new Mat();
                        Cv2.CvtColor(band, hsv, ColorConversionCodes.BGR2HSV);
                        Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 70, 255), gray);
                        Cv2.BitwiseNot(gray, gray);
                    }
                    using var padded = new Mat();
                    Cv2.CopyMakeBorder(gray, padded, 12, 12, 40, 40, BorderTypes.Constant, Scalar.White);
                    using var color = new Mat();
                    Cv2.CvtColor(padded, color, ColorConversionCodes.GRAY2BGR);
                    using var bitmap = BitmapMat.ToBitmap(color);
                    var lines = await reader.ReadAsync(bitmap);
                    results.Add(string.Join(" ", lines.Select(l => l.Text)));
                }
                output.WriteLine($"x{scale} {variant,-9}: " + string.Join(" | ", results));
            }
        }
    }
}
