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
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class TutorialPetScanTests
{
    private static ScanEntry Entry(string key, string? id, bool apply = true, bool value = true)
    {
        var entry = new ScanEntry { Key = key, PetId = id, Apply = apply, Confidence = ScanConfidence.Recognised,
            ThumbnailDataUrl = "", PortraitPng = [], Seen = 2, MatchScore = 24 };
        if (value) entry.Votes.Add((1, 4), 2);
        return entry;
    }

    [Theory]
    [InlineData("de", "Pet-Scan im Setup", "Markierte übernehmen & weiter")]
    [InlineData("en", "Pet scan in setup", "Apply selected & continue")]
    public async Task OpeningTheEmbeddedScanKeepsTheSetupOpenAndTheCurrentTabUnchanged(string language, string title, string next)
    {
        await using var f = new Fixture(language, [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Export("overview");
        await f.Invoke("OpenPetScan");
        var html = await f.Html();
        Assert.Contains(title, html);
        Assert.Contains(next, html);
        Assert.Single(Regex.Matches(html, "class=\"page pet-scan-page\""));
        Assert.Contains("data-setup-step=\"petscan\"", html);
        Assert.DoesNotContain("tutorial-video", html);
        Assert.True(f.Ui.TutorialOpen);
        Assert.False(f.Settings.Current.TutorialDone);
        Assert.Equal("options", f.Ui.Tab);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        await f.Export("pet-scan");
    }

    [Theory]
    [InlineData("de", "Erkundung einrichten", "1 markierte Pets übernommen.")]
    [InlineData("en", "Set up exploration", "Applied 1 selected pets.")]
    public async Task ContinueAppliesOnlyEligibleSelectedResultsOnceAndShowsExplorationInsideSetup(string language, string title, string applied)
    {
        await using var f = new Fixture(language,
            [Entry("selected", "fixture-sprout"), Entry("unselected", "fixture-fox", apply: false),
             Entry("unknown", null), Entry("missing", "fixture-fox", value: false)]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        var writes = 0;
        f.Progress.Changed += () => writes++;
        await f.Invoke("ContinueFromPetScan");
        await f.Invoke("ContinueFromPetScan"); // a queued second click cannot apply after the stage changed
        var html = await f.Html();
        Assert.Contains(title, html);
        Assert.Contains(applied, html);
        Assert.Contains("data-setup-step=\"exploration\"", html);
        Assert.DoesNotContain("pet-scan-page", html);
        Assert.DoesNotContain("tutorial-video", html);
        Assert.Equal(9, f.Progress.Souls("fixture-sprout"));
        Assert.Equal(0, f.Progress.Souls("fixture-fox"));
        Assert.Equal(1, writes);
        Assert.Equal(4, f.Scan.Entries.Count);
        Assert.True(f.Ui.TutorialOpen);
        Assert.False(f.Settings.Current.TutorialDone);
        Assert.Equal("options", f.Ui.Tab);
        Assert.False(f.Scan.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        await f.Export("exploration");
        await f.Invoke("Close");
        Assert.False(f.Ui.TutorialOpen);
        Assert.True(f.Settings.Current.TutorialDone);
        Assert.Equal("options", f.Ui.Tab);
    }

    [Fact]
    public async Task ContinueWaitsForTheLastFrameBeforeApplyingItsFinalReading()
    {
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SetScanField("_loop", frame.Task);
        f.SetScan(nameof(PetScanService.Running), true);
        var next = await f.Begin("ContinueFromPetScan");
        Assert.False(next.IsCompleted);
        Assert.True(f.Scan.Stopping);
        Assert.False(f.Scan.Running);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
        await f.Invoke("ContinueFromPetScan");
        Assert.True(f.Scan.CorrectValue("selected", "8/75")); // last in-flight frame/correction becomes the final snapshot
        frame.SetResult();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(38, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"exploration\"", await f.Html());
        Assert.False(f.Scan.Stopping);
        Assert.False(f.Settings.Current.TutorialDone);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("file")]
    [InlineData("stopping")]
    public async Task BusyOperationsCannotBeSkippedSavedOrClosed(string operation)
    {
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        if (operation == "file") f.SetPetViewField("_readingFile", true);
        else f.SetScan(operation == "starting" ? nameof(PetScanService.Starting) : nameof(PetScanService.Stopping), true);
        await f.Rerender();
        var html = await f.Html();
        Assert.Contains("disabled", Button(html, "Apply selected & continue"));
        Assert.Contains("disabled", Button(html, "Skip pet scan"));
        await f.Invoke("ContinueFromPetScan");
        await f.Invoke("SkipPetScan");
        await f.Invoke("BackToOverview");
        await f.Invoke("Close");
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
        Assert.True(f.Ui.TutorialOpen);
        Assert.False(f.Settings.Current.TutorialDone);
        await f.Export(operation);
    }

    [Theory]
    [InlineData("de", "Der Pet-Scan meldet einen Fehler.")]
    [InlineData("en", "The pet scan reports an error.")]
    public async Task ScanErrorsRemainVisibleAndBlockNormalContinueButExplicitSkipPreservesResults(string language, string error)
    {
        await using var f = new Fixture(language, [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        f.SetScan(nameof(PetScanService.Status), "Synthetic capture failure");
        f.SetScan(nameof(PetScanService.StatusIsError), true);
        await f.Invoke("ContinueFromPetScan");
        Assert.Contains(error, await f.Html());
        Assert.Contains("Synthetic capture failure", await f.Html());
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
        Assert.True(f.Scan.StatusIsError);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        await f.Export("error");
        await f.Invoke("SkipPetScan");
        Assert.Contains("data-setup-step=\"exploration\"", await f.Html());
        Assert.Single(f.Scan.Entries);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.True(f.Scan.StatusIsError);
        Assert.True(f.Ui.TutorialOpen);
        Assert.False(f.Settings.Current.TutorialDone);
    }

    [Fact]
    public async Task AFailureDuringStopCannotBeOverwrittenByTheSuccessfulStopMessage()
    {
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SetScanField("_loop", frame.Task);
        f.SetScan(nameof(PetScanService.Running), true);
        var next = await f.Begin("ContinueFromPetScan");
        f.SetScan(nameof(PetScanService.StatusIsError), true);
        f.SetScan(nameof(PetScanService.Status), "Synthetic last-frame failure");
        frame.SetResult();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Scan.StatusIsError);
        Assert.Equal("Synthetic last-frame failure", f.Scan.Status);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
    }

    [Theory]
    [InlineData(false, "No pets read yet.")]
    [InlineData(true, "No eligible pets selected.")]
    public async Task EmptyOrUnselectedResultsNeedAnExplicitSkip(bool hasResults, string feedback)
    {
        await using var f = new Fixture("en", hasResults ? [Entry("unselected", "fixture-sprout", apply: false)] : []);
        await f.Render();
        await f.Invoke("OpenPetScan");
        await f.Invoke("ContinueFromPetScan");
        Assert.Contains(feedback, await f.Html());
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        await f.Export(hasResults ? "unselected" : "empty");
        await f.Invoke("SkipPetScan");
        Assert.Contains("data-setup-step=\"exploration\"", await f.Html());
        if (hasResults) Assert.Single(f.Scan.Entries);
        else Assert.Empty(f.Scan.Entries);
        Assert.False(f.Settings.Current.TutorialDone);
    }

    [Fact]
    public async Task FileReadFailureAlsoBlocksContinueWhenTheCaptureServiceHasNoError()
    {
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        await f.Render();
        await f.Invoke("OpenPetScan");
        f.SetPetViewField("_fileError", true);
        await f.Invoke("ContinueFromPetScan");
        Assert.Contains("The pet scan reports an error.", await f.Html());
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
    }

    [Fact]
    public async Task GoingBackPreservesMinimapFactionExplorationUndoAndScanResults()
    {
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        await f.Render();
        var region = f.Settings.Current.MapRegion;
        var faction = f.Settings.Current.Faction;
        await f.Invoke("MarkAllExplored");
        Assert.Equal(2, f.Exploration.Completed.Count);
        await f.Invoke("OpenPetScan");
        await f.Invoke("BackToOverview");
        Assert.Contains("data-setup-step=\"overview\"", await f.Html());
        Assert.Equal(region, f.Settings.Current.MapRegion);
        Assert.Equal(faction, f.Settings.Current.Faction);
        Assert.Single(f.Scan.Entries);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.False(f.Settings.Current.TutorialDone);
        await f.Invoke("UndoAllExplored");
        Assert.Empty(f.Exploration.Completed);
    }

    [Fact]
    public async Task ExplorationScanRemainsTheExplicitFinalNavigationAndCompletesSetup()
    {
        await using var f = new Fixture("en");
        await f.Render();
        await f.Invoke("OpenPetScan");
        await f.Invoke("SkipPetScan");
        await f.Invoke("OpenExplorationScan");
        Assert.Equal("exploration", f.Ui.Tab);
        Assert.False(f.Ui.TutorialOpen);
        Assert.True(f.Settings.Current.TutorialDone);
    }

    [Fact]
    public async Task StopAsyncIsIdempotentDrainsPreparationAndAllowsANewStart()
    {
        await using var f = new Fixture("en");
        var oldLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SetScanField("_loop", oldLoop.Task);
        var start = f.Scan.StartAsync(); // waits for the synthetic prior loop, before any OCR/capture
        Assert.True(f.Scan.Starting);
        var stop = f.Scan.StopAsync();
        Assert.Same(stop, f.Scan.StopAsync());
        Assert.False(stop.IsCompleted);
        Assert.True(f.Scan.Stopping);
        await f.Scan.StartAsync(); // ignored while draining
        oldLoop.SetResult();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Scan.Starting);
        Assert.False(f.Scan.Running);
        Assert.False(f.Scan.Stopping);
        var nextLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SetScanField("_loop", nextLoop.Task);
        var nextStart = f.Scan.StartAsync();
        Assert.True(f.Scan.Starting);
        var nextStop = f.Scan.StopAsync();
        nextLoop.SetResult();
        await Task.WhenAll(nextStart, nextStop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Scan.Starting);
        Assert.False(f.Scan.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task StopAsyncWaitsForAnUncancellableFileOperationAndRetainsItsError()
    {
        await using var f = new Fixture("en");
        var file = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SetScanField("_preparing", file.Task);
        f.SetScan(nameof(PetScanService.Starting), true);
        var stop = f.Scan.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.True(f.Scan.Stopping);
        await f.Scan.ScanFileAsync("must-not-open-during-stop.png");
        f.SetScan(nameof(PetScanService.StatusIsError), true);
        f.SetScan(nameof(PetScanService.Status), "Synthetic file failure");
        f.SetScan(nameof(PetScanService.Starting), false);
        file.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Scan.StatusIsError);
        Assert.Equal("Synthetic file failure", f.Scan.Status);
        Assert.False(f.Scan.Stopping);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task AProblematicImageKeepsOldResultsButCannotTurnIntoSuccessAndLaterValidImportRecovers()
    {
        if (!Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US")) return;
        await using var f = new Fixture("en", [Entry("selected", "fixture-sprout")]);
        f.Settings.Current.AutoOcrLanguage = false;
        f.Settings.Current.OcrLanguage = "en-US";
        await f.Render();
        await f.Invoke("OpenPetScan");
        var blank = f.SyntheticBlankImage();
        await f.Scan.ScanFileAsync(blank);
        Assert.True(f.Scan.StatusIsError);
        Assert.Contains("Kein Pet-Raster", f.Scan.Status);
        Assert.Single(f.Scan.Entries);
        await f.Invoke("ContinueFromPetScan");
        Assert.Contains("The pet scan reports an error.", await f.Html());
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Contains("data-setup-step=\"petscan\"", await f.Html());
        await f.Scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
        Assert.False(f.Scan.StatusIsError);
        Assert.StartsWith("Bild gelesen:", f.Scan.Status);
        Assert.NotNull(f.Scan.Page);
        Assert.True(f.Scan.Page.Cards > 0);
        Assert.Equal(0, f.Progress.Souls("fixture-sprout"));
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    private static string Button(string html, string label) => Regex.Matches(html, "<button\\b[^>]*>[\\s\\S]*?</button>")
        .Cast<Match>().Single(match => Regex.Replace(match.Value, "<[^>]+>", "").Contains(label, StringComparison.Ordinal)).Value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-setup-pet-scan-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _oldLanguage = UiText.Language;
        private readonly string _language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private bool _rendered;
        public SettingsService Settings { get; }
        public ProgressService Progress { get; }
        public PetScanService Scan { get; }
        public UiState Ui { get; }
        public ExplorationService Exploration { get; }
        public GameCaptureService Capture { get; }
        public TutorialView View { get; private set; } = default!;
        public List<ScanEntry> RawEntries => (List<ScanEntry>)typeof(PetScanService).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Scan)!;

        public Fixture(string language, IReadOnlyList<ScanEntry>? entries = null)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[]}""");
            PetDefinition[] pets = [new() { Id = "fixture-sprout", En = "Synthetic Amber Sprout", De = "Synthetischer Bernsteinspross", Genus = "fixture" },
                new() { Id = "fixture-fox", En = "Synthetic Frost Fox", De = "Synthetischer Frostfuchs", Genus = "fixture" }];
            File.WriteAllText(Path.Combine(_folder, "pets.json"), JsonSerializer.Serialize(pets));
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.ProgressFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile })
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            JsonFile.Save(AppPaths.LearnedPetsFile, new List<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            JsonFile.Save(AppPaths.ProgressFile, new ProgressProfile { Server = "Synthetic Test Realm" });
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<PetScanService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<OcrLanguageInstaller>();
            services.AddSingleton<IJSRuntime, NoJs>();
            Exploration = new ExplorationService(Path.Combine(_folder, "exploration.json"),
                [new("fixture-a", "fixture-map", "dungeon", 1, "Synthetic Cavern", null),
                 new("fixture-b", "fixture-map", "kibelisk", 2, "Synthetic Beacon", null)]);
            services.AddSingleton(Exploration);
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.TutorialDone = false;
            Settings.Current.MapRegion = new Rectangle(12, 16, 220, 220);
            Settings.Current.PetWindowRegion = new Rectangle(0, 0, 1280, 720);
            Settings.Current.Faction = Factions.Asmodian;
            Progress = _provider.GetRequiredService<ProgressService>();
            Capture = _provider.GetRequiredService<GameCaptureService>();
            Scan = _provider.GetRequiredService<PetScanService>();
            Ui = _provider.GetRequiredService<UiState>();
            Ui.Navigate("options");
            Ui.OpenTutorial();
            if (entries is not null) RawEntries.AddRange(entries);
            Set(_provider.GetRequiredService<OcrLanguageInstaller>(), nameof(OcrLanguageInstaller.CheckedAt), DateTime.UtcNow);
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
        public void SetScan(string property, object? value) => Set(Scan, property, value);
        public void SetScanField(string name, object? value) => typeof(PetScanService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Scan, value);
        public void SetPetViewField(string name, object? value)
        {
            var pet = (PetScanView)typeof(TutorialView).GetField("_petScan", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View)!;
            typeof(PetScanView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, value);
        }

        public string SyntheticBlankImage()
        {
            var path = Path.Combine(_folder, "blank.png");
            using var picture = new Bitmap(1280, 720);
            using (var graphics = Graphics.FromImage(picture)) graphics.Clear(Color.Black);
            picture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        public async Task Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () => await _renderer.RenderComponentAsync<CaptureSetup>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CaptureSetup.Capture)] = (Action<TutorialView>)(view => View = view) })));
            _rendered = true;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task Rerender() => _renderer.Dispatcher.InvokeAsync(() => { typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, null); });
        private Task Call(string method)
        {
            var handler = typeof(TutorialView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return handler.Invoke(View, null) as Task ?? Task.CompletedTask;
        }
        public Task Invoke(string method) => _renderer.Dispatcher.InvokeAsync(async () => { await Call(method); await Rerender(); });
        private async Task CallAndRender(string method) { await Call(method); await Rerender(); }
        public async Task<Task> Begin(string method)
        {
            Task? pending = null;
            await _renderer.Dispatcher.InvokeAsync(() => { pending = CallAndRender(method); });
            return pending!;
        }
        public async Task Export(string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            var body = await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
            await File.WriteAllTextAsync(Path.Combine(output, "setup-" + state + "-" + _language + ".html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><title>Soulcrest setup preview</title></head><body>" + body + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            SetScan(nameof(PetScanService.Running), false);
            SetScan(nameof(PetScanService.Starting), false);
            SetScan(nameof(PetScanService.Stopping), false);
            if (_rendered)
            {
                var pet = (PetScanView?)typeof(TutorialView).GetField("_petScan", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View);
                if (pet is not null) typeof(PetScanView).GetField("_readingFile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pet, false);
            }
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            UiText.Language = _oldLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _oldMapData);
            Directory.Delete(_folder, true);
        }
    }

    private sealed class CaptureSetup : ComponentBase
    {
        [Parameter] public Action<TutorialView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TutorialView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((TutorialView)value));
            builder.CloseComponent();
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
