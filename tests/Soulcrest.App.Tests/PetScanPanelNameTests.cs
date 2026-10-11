using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetScanPanelNameTests
{
    [Theory]
    [InlineData("Pet Info", false)]
    [InlineData("Gatekeeper", false)]
    [InlineData("Unknown Pet...", false)]
    [InlineData("Pet Info", true)]
    [InlineData("Gatekeeper", true)]
    [InlineData("Unknown Pet...", true)]
    public void InvalidPanelNamesAreNotLearnedEvenFromRepeatedOrSinglePictureReads(string name, bool singlePicture)
    {
        var progress = new ProgressService();
        using var scan = new PetScanService(new SettingsService(), progress);
        var before = File.Exists(AppPaths.LearnedPetsFile) ? File.ReadAllText(AppPaths.LearnedPetsFile) : null;
        var card = new PetCardScan(0, new Rectangle(10, 10, 150, 180), new PetCardProgress(1, 5, 25, false),
            "5/25", new PortraitMatch(null, 0, null, 0), true, [], 0);
        var entry = scan.Merge(card);
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(card, entry)];
        var panel = new PetWindowScan([card], new PetPanelScan(name, 1, name,
            new PetCardProgress(1, 5, 25, false)), null, null);

        scan.LearnFromPanel(panel, frame, singlePicture);
        scan.LearnFromPanel(panel, frame, singlePicture);

        Assert.Null(Assert.Single(scan.Entries).PetId);
        Assert.False(Assert.Single(scan.Entries).Apply);
        Assert.False(scan.StatusIsError);
        Assert.Null(progress.FindByName(name));
        Assert.Equal(before, File.Exists(AppPaths.LearnedPetsFile) ? File.ReadAllText(AppPaths.LearnedPetsFile) : null);
    }
}
