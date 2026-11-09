using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class GenusInsightOverlayTests : IDisposable
{
    private readonly string _language = UiText.Language;
    private static GenusInsightSnapshot Example => GenusInsightOverlayExamples.Create(new(2026, 11, 9, 12, 34, 56, TimeSpan.Zero), "anonymous-overlay");

    [Theory]
    [InlineData("de")] [InlineData("en")]
    public async Task NativeWidgetRendersNineOrderedSlotsRankColorsFavoritesAndExactMaxRoll(string language)
    {
        var html = await Render(Example, language);
        Assert.Equal(Enumerable.Range(1, 9).Select(number => number.ToString()),
            Regex.Matches(html, "data-genus-slot=\"([0-9]+)\"").Select(match => match.Groups[1].Value));
        foreach (byte rank in new byte[] { 1, 2, 3, 4, 5 }) Assert.Contains("--genus-rank:" + GenusInsightPresentation.RankColor(rank), html);
        Assert.Equal(2, Regex.Matches(html, "data-genus-favorite=\"true\"").Count);
        Assert.Contains("MAX ROLL!", html); Assert.Contains("data-overlay-genus=\"cogni\"", html);
        Assert.Contains("icons/combat/genus-insight/cogni.png", html);
        Assert.Contains(language == "de" ? "Netzwerkstand:" : "Network snapshot:", html);
        Assert.DoesNotContain("anonymous-overlay", html);
    }

    [Fact]
    public async Task ViewedPresetUsesItsOwnBoardAndDoesNotBorrowActivePresetValues()
    {
        var example = Example;
        var viewed = example.Boards.Single(board => board.Genus == "fera");
        viewed = viewed with { Slots = viewed.Slots.Select(slot => slot with { LabelEn = "Viewed preview stat", Value = 731 }).ToArray() };
        var html = await Render(example with { SelectedGenus = "fera", ViewedPreset = 2, ViewedBoard = viewed }, "en");
        Assert.Equal(9, Regex.Matches(html, "Viewed preview stat</span>").Count);
        Assert.Equal(9, Regex.Matches(html, "data-genus-value=\"731\"").Count);
        Assert.Contains("data-overlay-genus=\"fera\"", html);
        Assert.DoesNotContain("icons/combat/genus-insight/cogni.png", html);
    }

    [Theory]
    [InlineData("old")] [InlineData("menu")] [InlineData("category")] [InlineData("preset")] [InlineData("board")] [InlineData("identity")]
    public async Task UnconfirmedAuthorityNeverShowsPreviousRolls(string boundary)
    {
        var state = boundary switch
        {
            "old" => Example with { IsCurrent = false }, "menu" => Example with { MenuVisible = false },
            "category" => Example with { SelectedGenus = "fera" }, "preset" => Example with { ViewedPreset = null },
            "board" => Example with { ViewedBoard = null }, _ => Example with { ProfileId = null },
        };
        var html = await Render(state, "en");
        Assert.DoesNotContain("data-genus-slot=", html); Assert.DoesNotContain("MAX ROLL!", html);
        Assert.False(GenusInsightOverlayContent.Available(state));
    }

    [Theory]
    [InlineData("de", "3,2 %", "0,25 %", "3,25 %")]
    [InlineData("en", "3.2 %", "0.25 %", "3.25 %")]
    public async Task GameRoundedPercentDoesNotRoundRangeEndpointsOrInventAMaximum(string language, string current, string min, string max)
    {
        var state = Example; var board = state.ViewedBoard!;
        var slot = board.Slots[0] with { Unit = "Percent", Value = 3.249m, Minimum = .25m, Maximum = 3.25m, IsMaxRoll = false, RollFraction = .999666666 };
        state = state with { ViewedBoard = board with { Slots = [slot] } };
        var html = await Render(state, language);
        Assert.Contains(current, html); Assert.Contains(min, html); Assert.Contains(max, html);
        Assert.Contains("data-genus-value=\"3.249\"", html); Assert.DoesNotContain("MAX ROLL!", html);
    }

    [Fact]
    public void DedicatedMenuWindowHidesWithoutQualifiedDataWhileMixedWindowsRetainOtherModules()
    {
        var widget = OverlayCatalog.CreateWidget("genus-insight");
        var settings = new OverlaySettings { Enabled = true, Widgets = [widget] };
        var context = new OverlayEditorLiveContext(true, true, false, null, new(null, null, null));
        Assert.False(OverlayEditorLivePolicy.IsVisible(settings, new(), context));
        Assert.True(OverlayEditorLivePolicy.IsVisible(settings, new() { GenusInsight = Example }, context));
        Assert.True(OverlayEditorLivePolicy.IsVisible(settings with { Widgets = [widget, OverlayCatalog.CreateWidget("clock")] }, new(), context));
        Assert.True(OverlayEditorLiveDiagnostics.Inputs(settings, new(), context, false).FailedGates.HasFlag(OverlayLiveFailedGates.GenusUnavailable));
    }

    [Fact]
    public void OnlyLiveConsumersWithEnabledGenusLayoutsAcquireTheSharedMenuLease()
    {
        var enabled = false; var acquired = 0; var released = 0;
        using var source = new OverlayEditorPresentationSource(() => new(null, false, new(null, null, null, 0), null, false, DateTimeOffset.UtcNow),
            () => null, (_, _) => new(), () => new Lease(() => { }), genus: () => Example,
            acquireGenus: () => { acquired++; return new Lease(() => released++); }, hasGenusLayout: () => enabled);
        using var library = source.Acquire(examples: true);
        enabled = true; using (source.Acquire(examples: true)) Assert.Equal(0, acquired);
        var first = source.Acquire(); var second = source.Acquire(); Assert.Equal(1, acquired);
        Assert.Equal("anonymous-overlay", source.Capture().GenusInsight.ProfileId);
        Assert.NotEqual("anonymous-overlay", source.Capture(examples: true).GenusInsight.ProfileId);
        first.Dispose(); Assert.Equal(0, released);
        second.Dispose(); Assert.Equal(1, released);
        enabled = false; using var unrelated = source.Acquire(); Assert.Equal(1, acquired);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")] [InlineData("en")]
    public async Task ExportProductionGenusWidgetForVisualReview(string language)
    {
        var output = TestPrerequisites.RequiredValue("SOULCREST_GENUS_OVERLAY_EXPORT");
        var repo = FindRepository(); var webroot = Path.Combine(repo, "src", "Soulcrest.App", "wwwroot");
        var widget = OverlayCatalog.CreateWidget("genus-insight") with { X = 12, Y = 10 };
        var model = new OverlayPlacementRenderState();
        model.Publish(new OverlayInstance { Id = "genus-visual", Name = "", Settings = new() { Enabled = true, Width = 464, Height = 410, Widgets = [widget] } },
            OverlaySnapshot.DemoFor(language) with { GenusInsight = Example });
        var markup = await RenderComponent<OverlayPlacementRenderSurface>(new() { [nameof(OverlayPlacementRenderSurface.Model)] = model }, language);
        var css = string.Join("\n", new[] { "css/app.css", "overlay-kit/base.css", "overlay-kit/overlay-editor.css", "overlay-kit/overlay-widgets.css", "overlay-kit/browser-preview.css", "overlay-kit/placement-render.css" }
            .Select(file => File.ReadAllText(Path.Combine(webroot, file))));
        var scripts = string.Join("\n", new[] { "overlay-kit/overlay-alignment.js", "overlay-kit/overlay-editor.js", "overlay-kit/placement-render.js" }
            .Select(file => File.ReadAllText(Path.Combine(webroot, file))));
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "genus-overlay-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\"" + new Uri(webroot + Path.DirectorySeparatorChar).AbsoluteUri + "\"><style>" + css + "</style></head><body><div id=\"app\">" + markup + "</div><script>" + scripts + "\nsoulcrestPlacementRender.begin('1');</script></body></html>");
    }

    [Fact]
    public async Task ReleasingAnOldMenuLeaseCannotTakeOverAnotherThreadsPendingAcquisition()
    {
        var enabled = 1; var acquired = 0; var released = 0;
        using var releaseEntered = new ManualResetEventSlim(); using var allowRelease = new ManualResetEventSlim();
        using var acquireEntered = new ManualResetEventSlim(); using var allowAcquire = new ManualResetEventSlim();
        using var source = new OverlayEditorPresentationSource(() => new(null, false, new(null, null, null, 0), null, false, DateTimeOffset.UtcNow),
            () => null, (_, _) => new(), () => new Lease(() => { }), acquireGenus: () =>
            {
                var id = Interlocked.Increment(ref acquired);
                if (id == 2) { acquireEntered.Set(); Assert.True(allowAcquire.Wait(TimeSpan.FromSeconds(5))); }
                return new Lease(() =>
                {
                    Interlocked.Increment(ref released);
                    if (id == 1) { releaseEntered.Set(); Assert.True(allowRelease.Wait(TimeSpan.FromSeconds(5))); }
                });
            }, hasGenusLayout: () => Volatile.Read(ref enabled) != 0);
        var first = source.Acquire(); Volatile.Write(ref enabled, 0);
        var releasing = Task.Run(first.Dispose); Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(5)));
        Volatile.Write(ref enabled, 1); var acquiring = Task.Run(() => source.Acquire());
        try
        {
            Assert.True(acquireEntered.Wait(TimeSpan.FromSeconds(5))); allowRelease.Set();
            await releasing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, Volatile.Read(ref acquired));
        }
        finally { allowRelease.Set(); allowAcquire.Set(); }
        var second = await acquiring.WaitAsync(TimeSpan.FromSeconds(5)); second.Dispose();
        Assert.Equal(2, Volatile.Read(ref released));
    }

    private static Task<string> Render(GenusInsightSnapshot snapshot, string language) => RenderComponent<OverlayWidgetPreview>(new()
    {
        [nameof(OverlayWidgetPreview.Widget)] = OverlayCatalog.CreateWidget("genus-insight"),
        [nameof(OverlayWidgetPreview.Snapshot)] = new OverlaySnapshot { GenusInsight = snapshot, UiLanguage = language },
    }, language);

    private static async Task<string> RenderComponent<T>(Dictionary<string, object?> parameters, string language) where T : IComponent
    {
        UiText.Language = language; var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString()));
    }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Soulcrest repository was not found.");
    }
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    public void Dispose() => UiText.Language = _language;
}
