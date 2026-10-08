using OpenCvSharp;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class PetCardClippingTests
{
    [Fact]
    public async Task ClippingOriginalCountersRemovesTheRowBeforeIdentityAndVoting()
    {
        using var matcher = new PortraitMatcher();
        var scanner = new PetWindowScanner(WindowsOcrLineReader.Create("en-US", 4),
            WindowsOcrLineReader.Create("en-US"), matcher, WindowsOcrLineReader.Create("en-US", 6));
        using var original = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "pet-window", "en-2026-10-03-all.png"));
        var before = await scanner.ScanAsync(original);
        Assert.Equal(15, before.Cards.Count);
        var bottom = before.Cards.Max(c => c.Bounds.Y);
        var row = before.Cards.Where(c => c.Bounds.Y == bottom).ToList();
        using var clipped = (System.Drawing.Bitmap)original.Clone();
        using (var graphics = System.Drawing.Graphics.FromImage(clipped))
            foreach (var card in row)
                graphics.FillRectangle(System.Drawing.Brushes.Black, card.Bounds.X - 8,
                    card.Bounds.Y + card.Bounds.Height * 92 / 100, card.Bounds.Width + 16,
                    card.Bounds.Height * 8 / 100 + 15);
        var after = await scanner.ScanAsync(clipped);
        Assert.Equal(12, after.Cards.Count);
        Assert.DoesNotContain(after.Cards, c => c.Bounds.Y >= bottom);
        // Existing complete rows and their readings survive; no clipped portrait can get an ID/vote.
        foreach (var card in after.Cards)
        {
            var previous = before.Cards.Single(c => c.Column == card.Column && Math.Abs(c.Bounds.Y - card.Bounds.Y) <= 3);
            Assert.Equal(previous.Progress, card.Progress);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AlmostCompleteEdgeRowCannotCreateZeroVotes(bool bottom)
    {
        // Synthetic brightness geometry: 210px cards, clipped to 193px (92 %).
        // The old 90 % median rule accepted the clipped row without its counter/bar.
        using var image = new Mat(1000, 1600, MatType.CV_8UC1, Scalar.All(0));
        List<(int Left, int Right)> columns = [(80, 230), (245, 395), (410, 560)];
        var rows = bottom ? new[] { (180, 210), (410, 210), (640, 193) }
            : new[] { (120, 193), (333, 210), (563, 210) };
        foreach (var (top, height) in rows)
            foreach (var (left, right) in columns)
                Cv2.Rectangle(image, new Rect(left, top, right - left, height), Scalar.All(60), -1);
        var cards = PetWindowScanner.FindCards(image, columns);
        Assert.Equal(6, cards.Count);
        Assert.All(cards, c => Assert.Equal(210, c.Card.Height));
        Assert.DoesNotContain(cards, c => bottom ? c.Card.Y >= 640 : c.Card.Y < 333);
    }

    [Fact]
    public void CompleteDarkZeroCardsAndSmallSelectionGlowStillRemainInTheGrid()
    {
        using var image = new Mat(1000, 1600, MatType.CV_8UC1, Scalar.All(0));
        List<(int Left, int Right)> columns = [(80, 230), (245, 395), (410, 560)];
        foreach (var (top, height) in new[] { (180, 210), (410, 214), (644, 210) })
            foreach (var (left, right) in columns)
                Cv2.Rectangle(image, new Rect(left, top, right - left, height), Scalar.All(10), -1);
        Assert.Equal(9, PetWindowScanner.FindCards(image, columns).Count);
    }
}
