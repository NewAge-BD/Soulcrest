using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;
using Xunit.Abstractions;
using BrowserView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Soulcrest.App.Tests;

/// <summary>Opt-in own WebView rasterization only: no MainForm, tracker, game or desktop capture.</summary>
[Collection("Settings file")]
public sealed class OverlayEditorRankNativeRenderDiagnostic(ITestOutputHelper output)
{
    private static readonly string[] Kinds =
        ["rank-net", "rank-gained", "rank-lost", "rank-net-hour", "rank-gained-hour", "rank-lost-hour"];

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public Task RenderChangingRankOnlyFrames() => RunSta(async () =>
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_OVERLAY_RANK_NATIVE_PNG"));
        var permitted = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(permitted + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".png", Path.GetExtension(target), ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DATA")));
        Assert.False(string.Equals(Path.GetFullPath(AppPaths.DataDirectory), Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest")), StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        EnsureTestHostFrameworkAssets();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWindowsFormsBlazorWebView();
        await using var provider = services.BuildServiceProvider();
        var window = new OverlayInstance
        {
            Id = "synthetic-rank-native", Name = "Synthetic rank renderer",
            Settings = new OverlaySettings
            {
                Width = 720, Height = 216, Scale = 1, BackgroundOpacity = .92, ShowBorder = true,
                Widgets = Kinds.Select((kind, index) => OverlayCatalog.CreateWidget(kind,
                    index % 3 * 240, index / 3 * 108) with
                    { Width = 240, Height = 108, FontScale = 1.15, ShowLabel = true }).ToArray(),
            },
        };
        var originalLanguage = UiText.Language;
        var originalDebugPort = Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT");
        Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", null);
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                using var renderer = new OverlayEditorBitmapRenderer(provider, pauseAnimations: false, drawLockWidget: false);
                Bitmap? previous = null;
                try
                {
                    var states = new (string Name, long? Gained, long? Lost)[]
                    {
                        ("unknown", null, null), ("positive", 1_733, 0), ("negative", 12, 50),
                        ("updated", 2_733, 56), ("repeated", 2_733, 56),
                    };
                    for (var index = 0; index < states.Length; index++)
                    {
                        var state = states[index];
                        var snapshot = Snapshot(language, state.Gained, state.Lost, index);
                        Assert.Empty(snapshot.Drops);
                        using var image = await renderer.RenderAsync(window, snapshot);
                        Assert.Equal(new Size(720, 216), image.Size);
                        await AssertBrowserValues(renderer, snapshot);
                        if (previous is not null)
                            for (var cell = 0; cell < Kinds.Length; cell++)
                                Assert.Equal(state.Name != "repeated", DifferentPixels(previous, image,
                                    new Rectangle(cell % 3 * 240, cell / 3 * 108, 240, 108)));
                        var file = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target)
                            + "-" + language + "-" + state.Name + ".png");
                        image.Save(file, ImageFormat.Png);
                        output.WriteLine("{0}/{1}: DOM values verified; production PNG saved to {2}", language, state.Name, file);
                        previous?.Dispose();
                        previous = new Bitmap(image);
                    }
                }
                finally { previous?.Dispose(); }
                await RenderOverlappingLayout(provider, target, language);
            }
        }
        finally
        {
            UiText.Language = originalLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT", originalDebugPort);
        }
    });

    private static void EnsureTestHostFrameworkAssets()
    {
        // testhost is the entry assembly, so WebView's entry-assembly manifest lookup cannot
        // find Soulcrest's framework assets. Materialize only these existing local build inputs.
        var manifestPath = Path.Combine(AppContext.BaseDirectory,
            typeof(OverlayEditorBitmapRenderer).Assembly.GetName().Name + ".staticwebassets.runtime.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var roots = manifest.RootElement.GetProperty("ContentRoots");
        var framework = manifest.RootElement.GetProperty("Root").GetProperty("Children")
            .GetProperty("_framework").GetProperty("Children");
        foreach (var name in new[] { "blazor.webview.js", "blazor.modules.json" })
        {
            var destination = Path.Combine(AppContext.BaseDirectory, "wwwroot", "_framework", name);
            if (File.Exists(destination)) continue;
            var asset = framework.GetProperty(name).GetProperty("Asset");
            var source = Path.Combine(roots[asset.GetProperty("ContentRootIndex").GetInt32()].GetString()!,
                asset.GetProperty("SubPath").GetString()!);
            Assert.True(File.Exists(source), "The local build's WebView framework asset is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static OverlaySnapshot Snapshot(string language, long? gained, long? lost, int revision)
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision);
        var session = new LootTrackerSession
        {
            Id = "synthetic-rank-session", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1,
            Status = LootTrackerSessionStatus.Live, StartedAt = now.AddMinutes(-30), LastLootAt = now,
            Elapsed = TimeSpan.FromMinutes(30), Character = new(new string('a', 32), "Synthetic"),
            RankPointsGained = gained, RankPointsLost = lost, ItemCount = 0, SoulCount = 0,
        };
        return OverlayEditorPresentation.Project(new(session, true, new(null, null, null, 0), null, false, now),
            null, null, now, TimeZoneInfo.Utc, language);
    }

    private async Task RenderOverlappingLayout(IServiceProvider services, string target, string language)
    {
        var window = new OverlayInstance
        {
            Id = "synthetic-overlapping-rank", Name = "Synthetic overlapping rank layout",
            Settings = new OverlaySettings
            {
                Width = 360, Height = 400, Scale = 1, BackgroundOpacity = .92, ShowBorder = true,
                Widgets =
                [
                    OverlayCatalog.CreateWidget("rank-net", 0, 80) with { Width = 184, Height = 72, ShowLabel = true },
                    OverlayCatalog.CreateWidget("rank-net-hour", 184, 80) with { Width = 184, Height = 72, ShowLabel = true },
                    OverlayCatalog.CreateWidget("abyss-rank-progress", 0, 0) with
                        { Width = 368, Height = 112, ContentWidth = 360, ContentHeight = 112, ShowLabel = false },
                ],
            },
        };
        using var renderer = new OverlayEditorBitmapRenderer(services, pauseAnimations: false, drawLockWidget: false);
        foreach (var known in new[] { false, true })
        {
            var snapshot = Snapshot(language, known ? 1_733 : null, known ? 0 : null, known ? 1 : 0) with
            {
                Abyss = new(999_572, true, true)
                { WorldContextVisible = true, ProfileId = new string('a', 32), BindingGeneration = 1 },
            };
            using var image = await renderer.RenderAsync(window, snapshot);
            await AssertBrowserValues(renderer, snapshot, ["rank-net", "rank-net-hour"]);
            var file = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target)
                + "-" + language + "-overlap-" + (known ? "positive" : "unknown") + ".png");
            image.Save(file, ImageFormat.Png);
            output.WriteLine("{0}/overlap-{1}: DOM values verified; production PNG saved to {2}", language, known, file);
        }
    }

    private static async Task AssertBrowserValues(OverlayEditorBitmapRenderer renderer, OverlaySnapshot snapshot,
        string[]? kinds = null)
    {
        kinds ??= Kinds;
        var field = typeof(OverlayEditorBitmapRenderer).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var initialized = Assert.IsType<TaskCompletionSource<BrowserView>>(field.GetValue(renderer));
        var browser = await initialized.Task;
        var script = JsonSerializer.Serialize(kinds) + """
            .map(kind => {
                const node = document.querySelector('.overlay-widget-' + kind + ' .overlay-value-text');
                const bounds = node?.getBoundingClientRect();
                const style = node && getComputedStyle(node);
                return { kind, value: node?.textContent.trim(), width: bounds?.width, height: bounds?.height,
                    font: style && parseFloat(style.fontSize), color: style?.color, opacity: style?.opacity };
            })
            """;
        using var values = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync(script));
        var entries = values.RootElement.EnumerateArray().ToArray();
        Assert.Equal(kinds.Length, entries.Length);
        foreach (var entry in entries)
        {
            var kind = entry.GetProperty("kind").GetString()!;
            Assert.Equal(snapshot.Metrics[kind].Value, entry.GetProperty("value").GetString());
            Assert.True(entry.GetProperty("width").GetDouble() > 0);
            Assert.True(entry.GetProperty("height").GetDouble() > 0);
            Assert.True(entry.GetProperty("font").GetDouble() >= 8);
            Assert.NotEqual("rgba(0, 0, 0, 0)", entry.GetProperty("color").GetString());
            Assert.NotEqual("0", entry.GetProperty("opacity").GetString());
        }
    }

    private static bool DifferentPixels(Bitmap first, Bitmap second, Rectangle area)
    {
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return true;
        return false;
    }

    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                Exception? failure = null;
                dispatcher.BeginInvoke(async () =>
                {
                    try { await action(); }
                    catch (Exception error) { failure = error; }
                    finally { context.ExitThread(); }
                });
                Application.Run(context);
                if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
