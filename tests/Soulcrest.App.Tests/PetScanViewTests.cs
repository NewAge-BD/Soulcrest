using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetScanViewTests
{
    // Synthetic portrait/preview, not a game capture.
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jKf0AAAAASUVORK5CYII=";

    private static ScanEntry Entry(string key, string? id, ScanConfidence confidence, (int Level, int InLevel)? value = null, bool apply = true)
    {
        var entry = new ScanEntry { Key = key, PetId = id, Confidence = confidence, Apply = apply,
            ThumbnailDataUrl = Pixel, PortraitPng = [], Seen = 3, MatchScore = 24, LastText = "Synthetic scan evidence" };
        if (value is { } reading) entry.Votes.Add(reading, 2);
        return entry;
    }

    private static ScanEntry[] Results()
    {
        var conflict = Entry("conflict", "fixture-ember", ScanConfidence.Recognised, (2, 8));
        conflict.Votes.Add((2, 9), 40);
        return
        [
            Entry("ready", "fixture-sprout", ScanConfidence.Recognised, (1, 4)),
            Entry("confirmed", "fixture-fox", ScanConfidence.Confirmed, (3, 0), apply: false),
            Entry("uncertain", "fixture-bloom", ScanConfidence.Uncertain, (2, 8)),
            Entry("missing", "fixture-fox", ScanConfidence.Confirmed),
            conflict,
            Entry("unknown", null, ScanConfidence.Unknown, (0, 2)),
        ];
    }

    [Theory]
    [InlineData("de", "Serverweiter Fortschritt", "In Soulcrest gespeichert · für alle Charaktere dieses Servers", "Noch keine Pets gelesen. Scan starten oder ein Bild einlesen.")]
    [InlineData("en", "Server-wide progress", "Saved in Soulcrest · shared by all characters on this server", "No pets read yet. Start scanning or open an image.")]
    public async Task EmptyScanExplainsTheServerScopeAndKeepsCaptureHelpSecondary(string language, string scope, string saved, string empty)
    {
        await using var fixture = new Fixture(language);
        var profile = fixture.SavedProgress;
        var html = await fixture.Render();
        Assert.Contains("class=\"page pet-scan-page\"", html);
        Assert.Contains(scope, html);
        Assert.Contains(saved, html);
        Assert.Contains(empty, html);
        Assert.Contains("class=\"pet-scan-summary\"", html);
        Assert.Contains("class=\"pet-scan-filters\"", html);
        Assert.Matches("<details class=\"pet-scan-settings\">", html);
        Assert.Matches("<details class=\"pet-scan-preview\">", html);
        Assert.DoesNotContain("data-entry=", html);
        Assert.Contains("disabled", Button(html, language == "de" ? "Markierte übernehmen (0)" : "Apply selected (0)"));
        Assert.Equal(9, fixture.Progress.Souls("fixture-sprout"));
        Assert.Equal(profile, fixture.SavedProgress);
        AssertPassive(fixture);
        await fixture.Export("empty");
    }

    [Theory]
    [InlineData("de", "Synthetischer Bernsteinspross", "Ergebnisse bereit zum Prüfen", "Unbekanntes Pet", "Markierte übernehmen (2)")]
    [InlineData("en", "Synthetic Amber Sprout", "Results ready to review", "Unknown pet", "Apply selected (2)")]
    public async Task ResultsDistinguishReviewReasonsAndSelectionFromTheSavedServerProgress(string language, string name, string status, string unknown, string apply)
    {
        await using var fixture = new Fixture(language, Results());
        var profile = fixture.SavedProgress;
        var html = await fixture.Render();
        Assert.Contains(name, html);
        Assert.Contains(status, html);
        Assert.Contains(unknown, html);
        Assert.Contains("data-status=\"conflictingvalue\"", Row(html, "conflict"));
        Assert.Contains("data-status=\"unreadablevalue\"", Row(html, "missing"));
        Assert.Contains("data-status=\"unknownpet\"", Row(html, "unknown"));
        Assert.Contains("data-status=\"uncertainpet\"", Row(html, "uncertain"));
        foreach (var key in new[] { "conflict", "missing", "unknown" }) Assert.Contains("disabled", Checkbox(Row(html, key)));
        Assert.DoesNotContain("disabled", Checkbox(Row(html, "uncertain")));
        Assert.Contains("checked", Checkbox(Row(html, "uncertain")));
        Assert.DoesNotContain("checked", Checkbox(Row(html, "confirmed")));
        Assert.DoesNotContain("disabled", Button(html, apply));
        Assert.DoesNotContain("class=\"pet-scan-correction\"", html);
        Assert.Equal(4, Regex.Matches(html, "data-status=\"(?:conflictingvalue|unreadablevalue|unknownpet|uncertainpet)\"").Count);
        Assert.Equal(6, Regex.Matches(html, "data-entry=\"").Count);
        Assert.True(html.IndexOf("data-entry=\"conflict\"", StringComparison.Ordinal) < html.IndexOf("data-entry=\"ready\"", StringComparison.Ordinal));
        Assert.Equal(profile, fixture.SavedProgress);
        AssertPassive(fixture);
        await fixture.Export("results");
    }

    [Theory]
    [InlineData("de", "Scan wird vorbereitet", "Scan startet …", "Abbrechen", "Bild einlesen …")]
    [InlineData("en", "Preparing scan", "Starting scan…", "Cancel", "Read image…")]
    public async Task PreparingTheScanShowsWaitingAndCancellationAndLocksFileAndCaptureChanges(string language, string page, string starting, string cancel, string image)
    {
        await using var fixture = new Fixture(language, Results());
        fixture.SetScan(nameof(PetScanService.Starting), true);
        var html = await fixture.Render();
        Assert.Contains(page, html);
        Assert.Contains("disabled", Button(html, starting));
        Assert.DoesNotContain("disabled", Button(html, cancel));
        Assert.Contains("disabled", Button(html, image));
        Assert.Contains("disabled", Button(html, language == "de" ? "Bereich wählen" : "Select region"));
        Assert.Matches("<select[^>]*disabled[^>]*aria-label=\"|<select[^>]*aria-label=\"[^\"]*\"[^>]*disabled", html);
        AssertPassive(fixture);
        await fixture.Export("starting");
    }

    [Theory]
    [InlineData("de", true, "Aktuelle Seite vollständig gelesen", "Langsam zur nächsten Seite scrollen.")]
    [InlineData("en", true, "Current page fully read", "Scroll slowly to the next page.")]
    [InlineData("de", false, "Noch nicht alle Werte lesbar", "Liste stillhalten. Unklare Pets im Spiel einmal anklicken oder unten korrigieren.")]
    [InlineData("en", false, "Some values are not readable yet", "Pause scrolling. Click unclear pets once in the game or correct them below.")]
    public async Task CurrentPageSeparatesVisibleReadinessFromAllScannedPetsAndAllowsApplyingWhileRunning(string language, bool ready, string title, string action)
    {
        await using var fixture = new Fixture(language, Results());
        var read = ready ? 4 : 3;
        fixture.SetScan(nameof(PetScanService.Running), true);
        fixture.SetScan(nameof(PetScanService.Frames), 12);
        fixture.SetScan(nameof(PetScanService.Collection), (2, 6));
        fixture.SetScan(nameof(PetScanService.PanelText), "Synthetic Frost Fox");
        fixture.SetScan(nameof(PetScanService.Page), new ScanPageStatus(4, read,
            ready ? [] : ["Synthetic Frost Fox"], true, 6, (2, 6), []));
        fixture.SetScan(nameof(PetScanService.PreviewDataUrl), Pixel);
        var profile = fixture.SavedProgress;
        var html = await fixture.Render();
        Assert.Contains(title, html);
        Assert.Contains(action, html);
        Assert.Contains($"<progress value=\"{read}\" max=\"4\"", html);
        if (ready) Assert.Contains("class=\"pet-scan-current-page ready\"", html);
        else Assert.DoesNotContain("class=\"pet-scan-current-page ready\"", html);
        Assert.Equal(6, Regex.Matches(html, "data-entry=\"").Count);
        Assert.DoesNotContain("disabled", Button(html, language == "de" ? "Markierte übernehmen (2)" : "Apply selected (2)"));
        Assert.Contains("disabled", Button(html, language == "de" ? "Liste leeren" : "Clear list"));
        Assert.Equal(profile, fixture.SavedProgress);
        Assert.Equal(9, fixture.Progress.Souls("fixture-sprout"));
        AssertPassive(fixture, running: true);
        await fixture.Export(ready ? "page-ready" : "page-review");
    }

    [Fact]
    public async Task MovingPageNeverClaimsReadinessEvenWhenEveryVisibleValueWasRead()
    {
        await using var fixture = new Fixture("en", Results());
        fixture.SetScan(nameof(PetScanService.Running), true);
        fixture.SetScan(nameof(PetScanService.Page), new ScanPageStatus(4, 4, [], false, 6, (2, 6), []));
        var html = await fixture.Render();
        Assert.Contains("List is moving", html);
        Assert.DoesNotContain("class=\"pet-scan-current-page ready\"", html);
        Assert.DoesNotContain("Current page fully read", html);
        AssertPassive(fixture, running: true);
        await fixture.Export("moving");
    }

    [Theory]
    [InlineData("de", "Scan konnte nicht abgeschlossen werden", "Fehler beim Scan")]
    [InlineData("en", "Scan could not be completed", "Scan error")]
    public async Task ScanFailureShowsRecoveryInsteadOfAnOldSuccessfulPageAndPreservesResults(string language, string recovery, string error)
    {
        await using var fixture = new Fixture(language, Results());
        fixture.SetScan(nameof(PetScanService.Page), new ScanPageStatus(4, 4, [], true, 6, (2, 6), []));
        fixture.SetScan(nameof(PetScanService.StatusIsError), true);
        fixture.SetScan(nameof(PetScanService.Status), "Synthetic scan fixture error");
        var profile = fixture.SavedProgress;
        var html = await fixture.Render();
        Assert.Contains(recovery, html);
        Assert.Contains(error, html);
        Assert.Contains("Synthetic scan fixture error", html);
        Assert.DoesNotContain("class=\"pet-scan-current-page ready\"", html);
        Assert.Equal(6, fixture.Scan.Entries.Count);
        Assert.Equal(profile, fixture.SavedProgress);
        AssertPassive(fixture);
        await fixture.Export("error");
    }

    [Fact]
    public async Task SearchAndVisibleSelectionPreserveHiddenChoicesAndNeverWriteServerProgress()
    {
        var entries = Results();
        foreach (var entry in entries.Where(entry => entry.Key is "missing" or "conflict" or "unknown")) entry.Apply = false;
        await using var fixture = new Fixture("en", entries);
        var profile = fixture.SavedProgress;
        await fixture.Render();
        await fixture.Invoke("Search", new ChangeEventArgs { Value = "BERNSTEINSPROSS" });
        var one = await fixture.Html();
        Assert.Single(Regex.Matches(one, "data-entry=\""));
        Assert.Contains("data-entry=\"ready\"", one);
        Assert.Contains("Apply selected (2)", one); // selected pet hidden by the filter still counts
        await fixture.Invoke("SelectVisible", false);
        Assert.False(fixture.Scan.Entries.Single(entry => entry.Key == "ready").Apply);
        Assert.True(fixture.Scan.Entries.Single(entry => entry.Key == "uncertain").Apply);
        await fixture.Invoke("Search", new ChangeEventArgs { Value = "" });
        await fixture.Invoke("SetStatus", "selected");
        var selected = await fixture.Html();
        Assert.Single(Regex.Matches(selected, "data-entry=\""));
        Assert.Contains("data-entry=\"uncertain\"", selected);
        await fixture.Invoke("SelectVisible", false);
        await fixture.Invoke("SetStatus", "review");
        await fixture.Invoke("SelectVisible", true);
        var after = fixture.Scan.Entries;
        Assert.True(after.Single(entry => entry.Key == "uncertain").Apply);
        Assert.All(after.Where(entry => entry.Key != "uncertain"), entry => Assert.False(entry.Apply));
        Assert.Contains("Apply selected (1)", await fixture.Html());
        Assert.Equal(profile, fixture.SavedProgress);
        AssertPassive(fixture);
        await fixture.Export("filtered");
    }

    [Theory]
    [InlineData("de", "Wert nicht übernommen.", "Wert korrigiert. Zum Speichern im Fortschritt anschließend übernehmen.")]
    [InlineData("en", "Value not accepted.", "Value corrected. Apply the results to save it to your progress.")]
    public async Task InlineCorrectionRejectsBadValuesAndFixesConflictsBeforeAnyProgressIsSaved(string language, string rejected, string corrected)
    {
        await using var fixture = new Fixture(language, Results());
        var profile = fixture.SavedProgress;
        await fixture.Render();
        await fixture.Invoke("ToggleCorrection", "conflict");
        await fixture.Invoke("SetCorrection", "conflict", "999/25");
        await fixture.Invoke("CorrectValue", "conflict");
        var invalid = await fixture.Html();
        Assert.Contains(rejected, invalid);
        Assert.Null(fixture.Scan.Entries.Single(entry => entry.Key == "conflict").Reading);
        await fixture.Invoke("SetCorrection", "conflict", "8/75");
        await fixture.Invoke("CorrectValue", "conflict");
        var html = await fixture.Html();
        Assert.Contains(corrected, html);
        Assert.Contains("data-status=\"ready\"", Row(html, "conflict"));
        var conflict = fixture.Scan.Entries.Single(entry => entry.Key == "conflict");
        Assert.Equal((2, 8), conflict.Reading);
        Assert.NotNull(conflict.ManualReading);
        Assert.Equal(profile, fixture.SavedProgress);
        Assert.Equal(0, fixture.Progress.Souls("fixture-ember"));
        AssertPassive(fixture);
        await fixture.Export("correction");
    }

    [Fact]
    public async Task AQueuedClearConfirmationCannotDiscardResultsAfterScanningStarts()
    {
        await using var fixture = new Fixture("en", Results());
        await fixture.Render();
        var profile = fixture.SavedProgress;
        typeof(PetScanView).GetField("_confirmClear", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.View, true);
        fixture.SetScan(nameof(PetScanService.Running), true);
        await fixture.Invoke("ClearConfirmed");
        Assert.Equal(6, fixture.Scan.Entries.Count);
        Assert.Equal(profile, fixture.SavedProgress);
        AssertPassive(fixture, running: true);
    }

    private static string Row(string html, string key)
    {
        var match = Regex.Match(html, "<article\\b[^>]*data-entry=\"" + Regex.Escape(key) + "\"[^>]*>[\\s\\S]*?</article>");
        Assert.True(match.Success, $"Missing scan row {key}");
        return match.Value;
    }
    private static string Checkbox(string row)
    {
        var match = Regex.Match(row, "<input\\b[^>]*type=\"checkbox\"[^>]*>");
        Assert.True(match.Success, "Missing row selection checkbox");
        return match.Value;
    }
    private static string Button(string html, string label)
    {
        var match = Regex.Matches(html, "<button\\b[^>]*>[\\s\\S]*?</button>").Cast<Match>()
            .FirstOrDefault(candidate => Regex.Replace(candidate.Value, "<[^>]+>", "").Contains(label, StringComparison.Ordinal));
        Assert.NotNull(match);
        return match.Value;
    }
    private static void AssertPassive(Fixture fixture, bool running = false)
    {
        Assert.Equal(running, fixture.Scan.Running);
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-scan-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private readonly string _language;
        public SettingsService Settings { get; }
        public ProgressService Progress { get; }
        public PetScanService Scan { get; }
        public GameCaptureService Capture { get; }
        public PetScanView View { get; private set; } = default!;
        public byte[] SavedProgress => File.ReadAllBytes(AppPaths.ProgressFile);

        public Fixture(string language, IReadOnlyList<ScanEntry>? entries = null)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[]}""");
            PetDefinition[] pets =
            [
                new() { Id = "fixture-sprout", En = "Synthetic Amber Sprout", De = "Synthetischer Bernsteinspross", Genus = "fixture" },
                new() { Id = "fixture-fox", En = "Synthetic Frost Fox", De = "Synthetischer Frostfuchs", Genus = "fixture" },
                new() { Id = "fixture-bloom", En = "Synthetic Moon Bloom", De = "Synthetische Mondblüte", Genus = "fixture" },
                new() { Id = "fixture-ember", En = "Synthetic Ember", De = "Synthetischer Funke", Genus = "fixture" },
            ];
            File.WriteAllText(Path.Combine(_folder, "pets.json"), JsonSerializer.Serialize(pets));
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.ProgressFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile })
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            JsonFile.Save(AppPaths.LearnedPetsFile, new List<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            JsonFile.Save(AppPaths.ProgressFile, new ProgressProfile { Server = "Synthetic Test Realm",
                Pets = new Dictionary<string, PetProgressEntry> { ["fixture-sprout"] = new() { Souls = 9 } } });
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<PetScanService>();
            services.AddSingleton<OcrLanguageInstaller>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.PetWindowRegion = new Rectangle(0, 0, 1280, 720);
            Progress = _provider.GetRequiredService<ProgressService>();
            Capture = _provider.GetRequiredService<GameCaptureService>();
            Scan = _provider.GetRequiredService<PetScanService>();
            if (entries is not null)
                ((List<ScanEntry>)typeof(PetScanService).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Scan)!).AddRange(entries);
            Set(_provider.GetRequiredService<OcrLanguageInstaller>(), nameof(OcrLanguageInstaller.CheckedAt), DateTime.UtcNow);
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public void SetScan(string property, object? value) => Set(Scan, property, value);
        private static void Set(object target, string property, object? value) =>
            target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () =>
                await _renderer.RenderComponentAsync<CaptureView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(CaptureView.Capture)] = (Action<PetScanView>)(view => View = view) })));
            return await Html();
        }

        public async Task<string> Html() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task Invoke(string method, params object?[] arguments) => await _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var member = typeof(PetScanView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(member);
            if (member.Invoke(View, arguments) is Task pending) await pending;
            // Blazor rerenders automatically after UI events; these direct handler calls emulate it.
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, null);
        });

        public async Task Export(string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_PET_SCAN_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            var body = await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
            await File.WriteAllTextAsync(Path.Combine(output, "pet-scan-" + state + "-" + _language + ".html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><title>Pet scan preview</title></head><body>" + body + "</body></html>");
        }

        public async ValueTask DisposeAsync()
        {
            SetScan(nameof(PetScanService.Running), false);
            SetScan(nameof(PetScanService.Starting), false);
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            UiText.Language = _previousLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            Directory.Delete(_folder, true);
        }
    }

    private sealed class CaptureView : ComponentBase
    {
        [Parameter] public Action<PetScanView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PetScanView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((PetScanView)value));
            builder.CloseComponent();
        }
    }
}
