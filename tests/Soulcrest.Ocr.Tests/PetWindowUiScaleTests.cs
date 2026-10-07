using System.Drawing;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// The same pet window at three UI scales (user screenshots 2026-10-03, 2000×1125). Values by eye: first row
/// 4/75 (selected, "Duduka Worker"), MAX, MAX; then MAX rows; the small scale also shows MAX, 42/75, 20/75 in
/// row 5. At the large scale the grid reaches ~29 % of the width (the search ended at 26 %) and a fourth
/// row is cut off by the list (it read as "locked 0/5"); at the medium scale the text beside the grid was
/// taken for grid columns.
/// </summary>
public sealed class PetWindowUiScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("en-2026-10-03-uiscale-small.png", 15)]
    [InlineData("en-2026-10-03-uiscale-medium.png", 12)]
    [InlineData("en-2026-10-03-uiscale-large.png", 9)]
    public async Task GridIsFoundAndNothingIsReadWrong(string file, int cards)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", file);
        if (!File.Exists(path) || !WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return;
        using var matcher = new PortraitMatcher();
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4), WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var image = new Bitmap(path);

        var scan = await scanner.ScanAsync(image);

        Assert.Equal(cards, scan.Cards.Count);
        Assert.Equal("Duduka Worker", scan.Panel?.Name);
        var rows = scan.Cards.Select(c => c.Bounds.Y).Distinct().Order().ToList();
        var read = 0;
        foreach (var card in scan.Cards)
        {
            var (row, column) = (rows.IndexOf(card.Bounds.Y), card.Column);
            PetCardProgress expected = (row, column) switch
            {
                (0, 0) => new(2, 4, 75, false),
                (4, 1) => new(2, 42, 75, false),
                (4, 2) => new(2, 20, 75, false),
                _ => new(3, 0, 0, true),
            };
            output.WriteLine($"r{row}c{column}: erwartet {expected}, gelesen {card.Progress} «{card.ProgressText}»");
            if (card.Progress is null)
                continue;
            Assert.Equal(expected, card.Progress);
            read++;
        }
        Assert.True(read >= cards - 3, $"nur {read} von {cards} gelesen");
    }
}
