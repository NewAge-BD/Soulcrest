using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>Offline synthetic pixels only. No HWND, desktop/game capture, input or app instance.</summary>
public sealed class DaevanionAnimationPerformanceExploration(ITestOutputHelper output)
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void MeasureHighlightRasterAndNativeBufferPreparation()
    {
        var results = new List<object>();
        foreach (var family in new[] { "ariel", "azphel" })
        {
            var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
            const int nodeSize = 72, spacing = 84, frames = 90;
            var anchors = board.Nodes.Where(node => node.Kind != DaevanionNodeKind.Start).Select(node =>
                new DaevanionNodeAnchor(node.Row, node.Column,
                    new(20 + (node.Column - 1) * spacing, 20 + (node.Row - 1) * spacing, nodeSize, nodeSize), 1)).ToArray();
            var selected = DaevanionStatRailLayout.StatsFor(board).Take(4).Select(stat => stat.Id).ToHashSet(StringComparer.Ordinal);
            var size = new Size(14 * spacing + nodeSize + 40, 14 * spacing + nodeSize + 40);
            using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var renderer = new DaevanionHighlightRenderer();
            var raster = new double[frames]; var native = new double[frames];
            var reused = new double[frames];
            using var buffer = new LayeredOverlaySurface();
            for (var iteration = -5; iteration < frames; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Transparent);
                    renderer.Paint(graphics, board, anchors, selected, spacing: spacing, phase: (iteration + 5) / (double)frames);
                }
                var rendered = Stopwatch.GetTimestamp();
                PrepareLegacyBuffer(image);
                var prepared = Stopwatch.GetTimestamp();
                Assert.True(buffer.TryCopyFrom(image));
                if (iteration < 0) continue;
                raster[iteration] = Stopwatch.GetElapsedTime(started, rendered).TotalMilliseconds;
                native[iteration] = Stopwatch.GetElapsedTime(rendered, prepared).TotalMilliseconds;
                reused[iteration] = Stopwatch.GetElapsedTime(prepared).TotalMilliseconds;
            }
            var row = new { family, size.Width, size.Height, Nodes = anchors.Length, Selected = selected.Count,
                Frames = frames, RasterMeanMs = raster.Average(), RasterP95Ms = Percentile(raster),
                LegacyNativeMeanMs = native.Average(), LegacyNativeP95Ms = Percentile(native),
                ReusedNativeMeanMs = reused.Average(), ReusedNativeP95Ms = Percentile(reused),
                ReusedNativeAllocations = buffer.AllocationCount };
            output.WriteLine(JsonSerializer.Serialize(row)); results.Add(row);
        }
        if (Environment.GetEnvironmentVariable("SOULCREST_ANIMATION_BENCH_OUTPUT") is { Length: > 0 } destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new
            {
                Mode = "Offline synthetic raster plus native DIB/DC preparation; no UpdateLayeredWindow or window.",
                GameCapture = false, DesktopCapture = false, Inputs = false, Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static double Percentile(double[] values) => values.Order().ElementAt((int)Math.Floor((values.Length - 1) * .95));

    private static void PrepareLegacyBuffer(Bitmap image)
    {
        var header = new NativeMethods.BitmapInfoHeader
            { Size = 40, Width = image.Width, Height = -image.Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        Assert.NotEqual(0, dib);
        try
        {
            using (var surface = new Bitmap(image.Width, image.Height, image.Width * 4, PixelFormat.Format32bppPArgb, bits))
            using (var graphics = Graphics.FromImage(surface))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(image, 0, 0);
            }
            var screen = NativeMethods.GetDC(0);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            Assert.NotEqual(0, memory);
            var old = NativeMethods.SelectObject(memory, dib);
            NativeMethods.SelectObject(memory, old);
            NativeMethods.DeleteDC(memory); NativeMethods.ReleaseDC(0, screen);
        }
        finally { NativeMethods.DeleteObject(dib); }
    }
}
