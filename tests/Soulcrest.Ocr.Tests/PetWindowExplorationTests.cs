using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>Dumps what Windows OCR reads in the in-game pet window (exploration for the parser).</summary>
public sealed class PetWindowExplorationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DumpOcrOfPetWindow()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png");
        using var image = new Bitmap(file);
        var reader = WindowsOcrLineReader.Create("en-US");
        foreach (var (name, rect) in new[] { ("links", new Rectangle(0, 0, image.Width / 4, image.Height)), ("rechts", new Rectangle(image.Width * 3 / 4, 0, image.Width / 4, image.Height / 2)) })
        {
            using var part = image.Clone(rect, image.PixelFormat);
            var lines = await reader.ReadAsync(part);
            output.WriteLine($"== {name}");
            foreach (var line in lines)
                output.WriteLine($"[{line.X + rect.X:0},{line.Y + rect.Y:0} h{line.Height:0}] {line.Text}   | " +
                    string.Join(" ", line.Words.Select(w => $"{w.Text}@{w.X + rect.X:0},{w.Y + rect.Y:0}")));
        }
    }
}
