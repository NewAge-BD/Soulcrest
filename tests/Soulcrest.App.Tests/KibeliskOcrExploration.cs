using System.Drawing;
using Soulcrest.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Prints the OCR lines of the Kibelisk list fixtures (SOULCREST_KIBELISK_OCR=1).</summary>
public sealed class KibeliskOcrExploration(ITestOutputHelper output)
{
    [Fact]
    public async Task PrintLines()
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_KIBELISK_OCR") != "1")
            return;
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "kibelisk-list");
        foreach (var file in Directory.GetFiles(folder, "*.png").Order())
        {
            using var image = new Bitmap(file);
            var lines = await WindowsOcrLineReader.Create("auto", 1).ReadAsync(image);
            output.WriteLine("== " + Path.GetFileName(file));
            foreach (var line in lines.OrderBy(l => l.Y))
                output.WriteLine($"{line.X,5:0} {line.Y,5:0} {line.Height,3:0} {line.Text}");
        }
    }
}
