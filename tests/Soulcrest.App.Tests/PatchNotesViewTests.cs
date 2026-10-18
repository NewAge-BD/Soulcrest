using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Real offline Razor rendering: embedded notes, isolated UI language, no host, capture, settings or updater.</summary>
[Collection("Settings file")]
public sealed class PatchNotesViewTests
{
    [Theory]
    [InlineData("de", "Neueste Version", "Installierte Version")]
    [InlineData("en", "Latest version", "Installed version")]
    public async Task LatestSelectionAndInstalledVersionAreIndependentAndOldOrInvalidVersionsStayOutsideTheHistory(
        string language, string latestLabel, string installedLabel)
    {
        await using var fixture = new Fixture(language);
        var source = Releases(language);
        var original = source.Select(release => (release, release.Entries.ToArray())).ToArray();
        var html = await fixture.View(source, installed: "1.0.0+fixture.build");
        var buttons = VersionButtons(html);
        Assert.Equal(new[] { "2.0.0", "1.0.0", "0.1.28" }, buttons.Select(button => button.Version));
        var selected = Assert.Single(buttons, button => button.Active);
        Assert.Equal("2.0.0", selected.Version);
        Assert.Equal("true", selected.Current);
        Assert.Contains(latestLabel, Text(html)); // latest belongs to the selected article, independently of installation
        Assert.Contains(installedLabel, Text(html)); // the page summary reports the installed version
        var installed = Assert.Single(buttons, button => button.Version == "1.0.0");
        Assert.Contains("pn-nav-installed", installed.Html);
        Assert.Contains(language == "de" ? "installiert" : "installed", installed.Html);
        Assert.False(installed.Active);
        Assert.DoesNotContain("pn-nav-installed", selected.Html);
        Assert.DoesNotContain("below-floor-marker", Text(html));
        Assert.DoesNotContain("invalid-version-marker", Text(html));
        Assert.Contains("latest-fixture-marker", Text(html));
        Assert.Contains("pn-group", html);
        Assert.All(original, row => Assert.Equal(row.Item2, row.release.Entries.ToArray()));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AnExplicitArchiveVersionIsSelectedAndItsContentIsRendered(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.View(Releases(language), selected: "1.0.0");
        var active = Assert.Single(VersionButtons(html), button => button.Active);
        Assert.Equal("1.0.0", active.Version);
        Assert.Equal("true", active.Current);
        var select = Regex.Match(html, @"<select\b[^>]*id=""pn-version-select""[^>]*>(?<options>[\s\S]*?)</select>");
        Assert.True(select.Success);
        var selectedOption = Assert.Single(Regex.Matches(select.Groups["options"].Value, @"<option\b(?<attributes>[^>]*)>"),
            option => Regex.IsMatch(option.Groups["attributes"].Value, @"(?:^|\s)selected(?:\s|=|$)"));
        Assert.Equal("1.0.0", Attribute(selectedOption.Groups["attributes"].Value, "value"));
        Assert.Contains(language == "de" ? "Archivtreffer fixture" : "Archive match fixture", Text(html));
        Assert.DoesNotContain("latest-fixture-marker", Text(html));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("0.1.27")]
    public async Task AnUnavailableInitialVersionFallsBackToTheNewestShownRelease(string requested)
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.View(Releases("en"), selected: requested);
        Assert.Equal("2.0.0", Assert.Single(VersionButtons(html), button => button.Active).Version);
        Assert.Contains("latest-fixture-marker", Text(html));
    }

    [Theory]
    [InlineData("de", "  ARCHIVTREFFER  ")]
    [InlineData("en", "  ARCHIVE MATCH  ")]
    public async Task SearchMatchesLocalizedEntryTextIgnoringCaseAndOuterWhitespace(string language, string query)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.View(Releases(language), query: query);
        var match = Assert.Single(VersionButtons(html));
        Assert.Equal("1.0.0", match.Version);
        Assert.True(match.Active);
        Assert.Contains(language == "de" ? "Archivtreffer fixture" : "Archive match fixture", Text(html));
        Assert.DoesNotContain("latest-fixture-marker", Text(html));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SearchCanSelectAnExactVersionWithoutChangingTheSource(string language)
    {
        await using var fixture = new Fixture(language);
        var source = Releases(language);
        var html = await fixture.View(source, query: "1.0.0");
        Assert.Equal("1.0.0", Assert.Single(VersionButtons(html)).Version);
        Assert.Equal(5, source.Count);
        Assert.Equal(3, source[0].Entries.Count);
    }

    [Theory]
    [InlineData("de", "Keine passenden Versionen")]
    [InlineData("en", "No matching versions")]
    public async Task NoMatchShowsAnEmptyStateInsteadOfAnUnrelatedRelease(string language, string emptyLabel)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.View(Releases(language), query: "no-match-fixture-xyz");
        Assert.Empty(VersionButtons(html));
        Assert.Contains("pn-empty", html);
        Assert.Contains(emptyLabel, Text(html));
        Assert.DoesNotContain("latest-fixture-marker", Text(html));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AnEmptySourceShowsAnEmptyStateWithoutInventedNotes(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.View([]);
        Assert.Empty(VersionButtons(html));
        Assert.Contains("status error", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains(language == "de" ? "Patchnotes nicht gefunden." : "Patch notes not found.", Text(html));
        Assert.DoesNotContain("pn-group", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task UnknownKindsAndUnlabelledEntriesKeepTheirTextAndEscapeUserProvidedMarkup(string language)
    {
        await using var fixture = new Fixture(language);
        var entries = new PatchNotes.Entry[] { new("Maintenance <unsafe>", "unknown-kind-marker <script>alert('x')</script>"),
            new("", "unlabelled-marker & untouched"), new(language == "de" ? "Neu" : "New", "known-kind-marker") };
        var html = await fixture.View([new("2.0.0", "2026-10-17", entries)]);
        Assert.Contains("unknown-kind-marker", Text(html));
        Assert.Contains("unlabelled-marker & untouched", Text(html));
        Assert.Contains("known-kind-marker", Text(html));
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<unsafe>", html);
        Assert.Equal(3, entries.Length);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SharedEntriesKeepDefaultUpdateBadgesAndSafeBoldCodeMarkupWhenGroupedBadgesAreDisabled(string language)
    {
        await using var fixture = new Fixture(language);
        PatchNotes.Entry[] entries = [new(language == "de" ? "Neu" : "New",
            "**Bold <value>** and `code<unsafe>` <script>alert('x')</script> & tail")];
        var update = await fixture.Entries(entries);
        var grouped = await fixture.Entries(entries, showKind: false);
        Assert.Contains("pn-kind new", update);
        Assert.DoesNotContain("pn-kind", grouped);
        foreach (var html in new[] { update, grouped })
        {
            Assert.Contains("<b>Bold &lt;value&gt;</b>", html);
            Assert.Contains("<code>code&lt;unsafe&gt;</code>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&amp; tail", html);
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("<value>", html);
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task DefaultLanguageUsesEmbeddedNotesAndOptionallyExportsRealOfflineStates(string language)
    {
        await using var fixture = new Fixture(language);
        var shown = PatchNotes.For(language).Where(release => Version.TryParse(release.Version, out var version) && version >= PatchNotes.FirstShown).ToArray();
        Assert.NotEmpty(shown);
        var latest = shown[0];
        var html = await fixture.View(installed: latest.Version);
        Assert.Equal(shown.Length, VersionButtons(html).Count);
        Assert.Equal(latest.Version, Assert.Single(VersionButtons(html), button => button.Active).Version);
        var firstText = string.Concat(PatchNotes.Spans(latest.Entries[0].Text).Select(span => span.Text));
        Assert.Contains(Normalize(firstText), Text(html));
        await fixture.Export(html, "current");
        var archive = shown.Where(release => release.Version != latest.Version).MaxBy(release => release.Entries.Count) ?? latest;
        await fixture.Export(await fixture.View(installed: latest.Version, selected: archive.Version), "archive");
        await fixture.Export(await fixture.View(installed: latest.Version, query: "Overlay"), "search");
        await fixture.Export(await fixture.View(installed: latest.Version, query: "no-match-fixture-xyz"), "empty");
        var update = await fixture.Entries(latest.Entries);
        await fixture.Export("<div class=\"tutorial-backdrop\"><section class=\"update-notice card\"><h2>Soulcrest "
            + WebUtility.HtmlEncode(latest.Version) + "</h2><div class=\"update-notes\"><section class=\"update-version\"><h3>"
            + WebUtility.HtmlEncode(latest.Version) + "</h3>" + update + "</section></div></section></div>", "update-entries");
    }

    private static IReadOnlyList<PatchNotes.Release> Releases(string language) => [
        new("2.0.0", "2026-10-17", [new(language == "de" ? "Neu" : "New", "latest-fixture-marker with **bold** and `code-token`"),
            new(language == "de" ? "Behoben" : "Fixed", "second-latest-marker"), new("Maintenance", "unknown-latest-marker")]),
        new("1.0.0", "2026-10-16", [new(language == "de" ? "Verbessert" : "Improved", language == "de" ? "Archivtreffer fixture" : "Archive match fixture")]),
        new("0.1.28", "2026-10-07", [new("", "first-shown-marker")]),
        new("0.1.27", "2026-10-06", [new("", "below-floor-marker")]),
        new("invalid", "2026-10-05", [new("", "invalid-version-marker")])];

    private sealed record VersionButton(string Version, bool Active, string? Current, string Html);
    private static List<VersionButton> VersionButtons(string html) => Regex.Matches(html,
            "<(?:button|a)\\b(?<attributes>[^>]*)>(?<content>[\\s\\S]*?)</(?:button|a)>")
        .Where(match => Classes(Attribute(match.Groups["attributes"].Value, "class")).Contains("pn-version", StringComparer.Ordinal))
        .Select(match => new VersionButton(Regex.Match(Text(match.Groups["content"].Value), @"\d+\.\d+\.\d+").Value,
            Classes(Attribute(match.Groups["attributes"].Value, "class")).Contains("active", StringComparer.Ordinal),
            Attribute(match.Groups["attributes"].Value, "aria-current"), WebUtility.HtmlDecode(match.Value))).ToList();
    private static string[] Classes(string? value) => (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static string? Attribute(string html, string name)
    {
        var match = Regex.Match(html, "\\b" + Regex.Escape(name) + "=\"(?<value>[^\"]*)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }
    private static string Text(string html) => Normalize(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private sealed class RejectJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static Razor rendering must not invoke JavaScript: " + identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        internal Fixture(string language)
        {
            _language = language;
            UiText.Language = language;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IJSRuntime>(new RejectJs());
            _provider = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> View(IReadOnlyList<PatchNotes.Release>? releases = null, string installed = "1.0.0", string? selected = null, string? query = null)
        {
            var parameters = new Dictionary<string, object?> { [nameof(PatchNotesView.InstalledVersion)] = installed };
            if (releases is not null) parameters[nameof(PatchNotesView.Releases)] = releases;
            if (selected is not null) parameters[nameof(PatchNotesView.InitialVersion)] = selected;
            if (query is not null) parameters[nameof(PatchNotesView.InitialQuery)] = query;
            return Render<PatchNotesView>(parameters); // Language is intentionally the component's UiText.Language default.
        }
        internal Task<string> Entries(IReadOnlyList<PatchNotes.Entry> entries, bool? showKind = null)
        {
            var parameters = new Dictionary<string, object?> { [nameof(PatchNotesEntries.Entries)] = entries };
            if (showKind is not null) parameters[nameof(PatchNotesEntries.ShowKind)] = showKind.Value;
            return Render<PatchNotesEntries>(parameters);
        }
        private Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => _renderer.Dispatcher.InvokeAsync(async () =>
            (await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        internal async Task Export(string html, string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_PATCHNOTES_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var css = string.Join("\n", new[] { "app.css", "patchnotes.css" }.Select(sheet =>
                File.ReadAllText(Path.Combine(repository, "src", "Soulcrest.App", "wwwroot", "css", sheet))));
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"patchnotes-{_language}-{state}.html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>"
                + css + "</style></head><body><div class=\"shell\"><header class=\"topbar\"><div class=\"brand\">Soulcrest</div><nav class=\"tabs\"><button type=\"button\" class=\"active\">"
                + WebUtility.HtmlEncode(UiText.T("Patchnotes")) + "</button></nav></header><main class=\"content\">" + html + "</main></div></body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _previousLanguage;
        }
    }
}
