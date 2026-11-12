using System.Collections.Immutable;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Explicit offline renders of the production native HUD. No game, window or user data is opened.</summary>
public sealed class CraftingProgressRenderExploration
{
    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de", 1)]
    [InlineData("en", 1)]
    [InlineData("de", 1.5)]
    [InlineData("en", 1.5)]
    [InlineData("de", 2)]
    [InlineData("en", 2)]
    public void RenderProductionOverlay(string language, double scale)
    {
        var directory = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_CRAFTING_PROGRESS_RENDER_DIRECTORY"));
        Directory.CreateDirectory(directory);
        var example = OverlayPreviewRenderer.CraftingProgressExample();
        var suffix = language + "-" + (int)Math.Round(scale * 100);
        Save("crafting-progress-" + suffix, example);
        Save("crafting-progress-partial-" + suffix, example with
        {
            Rows = example.Rows.Select(row => row with { InventoryComplete = false }).ToImmutableArray()
        });
        Save("crafting-progress-unknown-" + suffix, example with
        {
            IsFresh = false,
            Rows = example.Rows.Select(row => row with { KnownQuantity = null, RequiredQuantity = null,
                InventoryFresh = false, InventoryComplete = false }).ToImmutableArray()
        });
        Save("crafting-progress-complete-" + suffix, example with
        {
            Pinned = example.Pinned! with { ConfirmedQuantity = example.Pinned.TargetQuantity },
            IsCompletionVisible = true, CompletionAgeSeconds = 2
        });

        void Save(string file, Soulcrest.App.Services.CraftingProgressSnapshot snapshot)
        {
            using var bitmap = CraftingProgressOverlayRenderer.Render(snapshot, scale: scale, language: language, animationsEnabled: false);
            Assert.Equal((int)Math.Round(360 * scale), bitmap.Width);
            bitmap.Save(Path.Combine(directory, file + ".png"), ImageFormat.Png);
        }
    }
}
