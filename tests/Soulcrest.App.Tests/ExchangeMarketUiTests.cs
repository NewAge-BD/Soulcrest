using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ExchangeMarketUiTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AutomaticSelectionIsVisibleAndManualOverridePreservesFeesAndOtherProfiles(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.SetExchangeMarketProfile(FirstProfile, new()
        {
            Region = "eu", Faction = "elyos", ServerId = 1, AutoDetectServer = true, SalesTaxPercent = 7.5m,
        });
        fixture.Settings.Update(value => value.LootTrackingEnabled = false);
        ExchangeMarketSettings? component = null;
        var loads = 0;
        var html = await fixture.Render<CaptureSettings>(new()
        {
            [nameof(CaptureSettings.Captured)] = (Action<ExchangeMarketSettings>)(value => component = value),
            [nameof(ExchangeMarketSettings.Profiles)] = new (string Id, string Name)[] { (FirstProfile, "Synthetic profile A"), (SecondProfile, "Synthetic profile B") },
            [nameof(ExchangeMarketSettings.ActiveProfileId)] = FirstProfile,
            [nameof(ExchangeMarketSettings.DetectedProfileId)] = FirstProfile,
            [nameof(ExchangeMarketSettings.AutomaticStatus)] = "Marktserver automatisch zugeordnet.",
            [nameof(ExchangeMarketSettings.Servers)] = Servers,
            [nameof(ExchangeMarketSettings.LoadServers)] = EventCallback.Factory.Create(this, () => loads++),
        });
        Assert.Contains(UiText.T("Marktserver automatisch erkennen"), WebUtility.HtmlDecode(html));
        Assert.Contains("data-exchange-auto-status=\"true\"", html);
        Assert.Equal(0, loads);
        if (Environment.GetEnvironmentVariable("SOULCREST_AUTO_MARKET_PREVIEW") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            var css = ReadCss("app.css") + ReadCss("loot-tracker.css");
            await File.WriteAllTextAsync(Path.Combine(output, "auto-market-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><style>" + css + "</style></head><body><main class=\"page\">" + html + "</main></body></html>");
        }
        await fixture.Event(component!, "exchange-market-auto", "onchange", new ChangeEventArgs { Value = false });
        var manual = fixture.Settings.GetExchangeMarketProfile(FirstProfile);
        Assert.False(manual.AutomaticServerSelection);
        Assert.Equal(1, manual.ServerId);
        Assert.Equal(7.5m, manual.SalesTaxPercent);
        await fixture.Event(component!, "exchange-market-auto", "onchange", new ChangeEventArgs { Value = true });
        var automatic = fixture.Settings.GetExchangeMarketProfile(FirstProfile);
        Assert.True(automatic.AutomaticServerSelection);
        Assert.Null(automatic.ServerId); Assert.Null(automatic.Region); Assert.Null(automatic.Faction);
        Assert.Equal(7.5m, automatic.SalesTaxPercent);
        Assert.Equal(new ExchangeMarketProfileSettings(), fixture.Settings.GetExchangeMarketProfile(SecondProfile));
        Assert.True(new SettingsService().GetExchangeMarketProfile(FirstProfile).AutomaticServerSelection);
        Assert.False(fixture.Settings.Current.LootTrackingEnabled);
        Assert.Equal(0, loads);
    }

    private const string FirstProfile = "b5037bf47d70426084c0e464b4c85621", SecondProfile = "75adb901b0ee4f56800db99e78cf3276";
    private static readonly DateTimeOffset At = new(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);
    private static readonly ExchangeMarketServer[] Servers =
    [new(1, "Synthetic EU", "eu", "Europe", "elyos", true), new(2, "Synthetic NA", "nae", "North America", "asmodian", true)];

    [Fact]
    public void OverlayOnlyProjectsFrozenPricesForItsQualifiedLiveSession()
    {
        var session = new LootTrackerSession { Id = "synthetic-live", Status = LootTrackerSessionStatus.Live,
            Kind = LootTrackerSessionKind.Mixed, KinaUnbound = 12_345, Character = new(FirstProfile, "Synthetic profile A") };
        var state = new UnifiedLootOverlayState(session, true, new(null, null, null, 0), null, false, At);
        var projected = UnifiedLootOverlaySource.Project(state, AbyssProgressSnapshot.Empty, new(), Summary());
        Assert.True(projected.ExchangeMarketDisplay); Assert.Equal(135_520, projected.ExchangeNetKina);
        Assert.Equal(132_000, projected.ExchangeReferenceNetKina); Assert.Equal(12_345, projected.SessionKina);
        Assert.Equal(2, projected.ExchangePricedRows); Assert.Equal(4, projected.ExchangeEligibleRows);
        var stale = UnifiedLootOverlaySource.Project(state with { TrackingAvailable = false }, AbyssProgressSnapshot.Empty, new(), Summary());
        Assert.False(stale.ExchangeMarketDisplay); Assert.Null(stale.ExchangeNetKina);
        var ended = UnifiedLootOverlaySource.Project(state with { Session = session with { Status = LootTrackerSessionStatus.Completed } }, AbyssProgressSnapshot.Empty, new(), Summary());
        Assert.False(ended.ExchangeMarketDisplay); Assert.Null(ended.ExchangeReferenceNetKina);
    }

    [Fact]
    public async Task SettingsAreProfileScopedAtomicAndIndependentOfNetworkTracking()
    {
        await using var fixture = new Fixture("en");
        var settings = fixture.Settings;
        Assert.True(new AppSettings().OnlineMarketPricesEnabled);
        settings.Update(value => value.LootTrackingEnabled = false);
        var first = new ExchangeMarketProfileSettings { Region = "eu", Faction = "elyos", ServerId = 1 };
        settings.SetExchangeMarketProfile(FirstProfile, first);
        settings.SetExchangeMarketProfile(SecondProfile, first with { Region = "nae", Faction = "asmodian", ServerId = 2, SalesTaxPercent = 7.5m });
        settings.SetOnlineMarketPricesEnabled(false);
        var restarted = new SettingsService();
        Assert.True(restarted.Current.OnlineMarketPricesEnabled);
        Assert.False(restarted.Current.LootTrackingEnabled);
        Assert.Equal(first, restarted.GetExchangeMarketProfile(FirstProfile));
        Assert.Equal(7.5m, restarted.GetExchangeMarketProfile(SecondProfile).SalesTaxPercent);
        Assert.Equal(2m, restarted.GetExchangeMarketProfile(SecondProfile).RegistrationFeePercent);
        Assert.Throws<ArgumentException>(() => settings.SetExchangeMarketProfile("not-a-local-profile", first));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetExchangeMarketProfile(FirstProfile, first with { SalesTaxPercent = 99m }));
        var before = JsonSerializer.Serialize(settings.Current);
        var changes = 0; settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            Assert.True(Record.Exception(() => settings.SetExchangeMarketProfile(FirstProfile, first with { ServerId = 3 })) is IOException or UnauthorizedAccessException);
            Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
            Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SettingsRenderWithoutNetworkAndOnlyExplicitLoadInvokesCallback(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.SetExchangeMarketProfile(FirstProfile, new() { Region = "eu", Faction = "elyos", ServerId = 1, AutoDetectServer = false });
        var calls = 0;
        ExchangeMarketSettings? component = null;
        var html = await fixture.Render<CaptureSettings>(new()
        {
            [nameof(CaptureSettings.Captured)] = (Action<ExchangeMarketSettings>)(value => component = value),
            [nameof(ExchangeMarketSettings.Profiles)] = new (string Id, string Name)[] { (FirstProfile, "Synthetic profile A"), (SecondProfile, "Synthetic profile B") },
            [nameof(ExchangeMarketSettings.ActiveProfileId)] = FirstProfile,
            [nameof(ExchangeMarketSettings.Servers)] = Servers,
            [nameof(ExchangeMarketSettings.LoadServers)] = EventCallback.Factory.Create(this, () => calls++),
        });
        Assert.Equal(0, calls);
        Assert.DoesNotContain("exchange-market-enabled", html);
        Assert.Contains("data-exchange-market-always-on", html);
        Assert.Contains("Synthetic EU", html);
        Assert.DoesNotContain("<option value=\"2\">", html);
        Assert.Contains(UiText.T("Verkaufsgebühr (%)"), WebUtility.HtmlDecode(html));
        Assert.Contains(ExchangeMarketPresentation.TradeabilityHint, WebUtility.HtmlDecode(html));
        await fixture.Event(component!, "exchange-market-load-servers", "onclick", new MouseEventArgs());
        Assert.Equal(1, calls);
        await fixture.Event(component!, "exchange-market-region", "onchange", new ChangeEventArgs { Value = "nae" });
        var selected = fixture.Settings.GetExchangeMarketProfile(FirstProfile);
        Assert.Equal("nae", selected.Region); Assert.Null(selected.ServerId); Assert.Null(selected.Faction);
        Assert.Equal(new ExchangeMarketProfileSettings(), fixture.Settings.GetExchangeMarketProfile(SecondProfile));
        Assert.True(fixture.Settings.Current.OnlineMarketPricesEnabled);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ServerLoadErrorsAndInternalRevocationDuringALoadRemainVisibleAndHandled()
    {
        await using var fixture = new Fixture("en");
        ExchangeMarketSettings? component = null;
        var parameters = new Dictionary<string, object?>
        {
            [nameof(CaptureSettings.Captured)] = (Action<ExchangeMarketSettings>)(value => component = value),
            [nameof(ExchangeMarketSettings.LoadServers)] = EventCallback.Factory.Create(this,
                (Func<Task>)(() => Task.FromException(new HttpRequestException("synthetic public source failure")))),
        };
        await fixture.Render<CaptureSettings>(parameters);
        await fixture.Event(component!, "exchange-market-load-servers", "onclick", new MouseEventArgs());
        Assert.Contains(UiText.T("Serverliste konnte nicht geladen werden."), WebUtility.HtmlDecode(await fixture.Markup()));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.Changed += () => { if (!fixture.Settings.Current.OnlineMarketPricesEnabled) load.TrySetCanceled(); };
        parameters[nameof(ExchangeMarketSettings.LoadServers)] = EventCallback.Factory.Create(this,
            (Func<Task>)(async () => { started.TrySetResult(); await load.Task; }));
        await fixture.Render<CaptureSettings>(parameters);
        var waiting = fixture.Event(component!, "exchange-market-load-servers", "onclick", new MouseEventArgs());
        await started.Task;
        fixture.Settings.SetOnlineMarketPricesEnabled(false); // Simulated internal source revocation; no user-facing off switch.
        await waiting;
        Assert.False(fixture.Settings.Current.OnlineMarketPricesEnabled);
        Assert.Contains(UiText.T("Ausgeschaltet"), WebUtility.HtmlDecode(await fixture.Markup()));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FrozenMarketValuesShowCoverageAndEquipmentReferenceSeparatelyFromWalletKina(string language)
    {
        await using var fixture = new Fixture(language);
        var summary = Summary();
        var session = new LootTrackerSession
        {
            Id = "synthetic-exchange-session", Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Live,
            Character = new(FirstProfile, "Synthetic profile A"), Location = "Synthetic field", StartedAt = At.AddMinutes(-40),
            Elapsed = TimeSpan.FromMinutes(40), KinaUnbound = 12_345, ItemCount = 43, SoulCount = 0,
            Loot = [new("Item:999001", "Synthetic material", LootTrackerLootKind.Item, 40),
                new("Item:210230006", "Synthetic equipment", LootTrackerLootKind.Item, 1),
                new("Item:999002", "Synthetic unknown", LootTrackerLootKind.Item, 2)]
        };
        ExchangeLootValuationSummary Row(LootTrackerSession _, LootTrackerLoot row) => row.Id switch
        {
            "Item:999001" => summary with { NetKina = 3_520, GrossKina = 4_000, ReferenceRows = 0, ReferenceGrossKina = null, ReferenceNetKina = null, ReferenceOnly = false },
            "Item:210230006" => summary with { NetKina = 132_000, GrossKina = 150_000, EligibleRows = 1, PricedRows = 1, PendingRows = 0 },
            _ => new(null, null, 0, 1, 1, false, 1, false, null, null),
        };
        var html = await fixture.Render<LootTrackerView>(new()
        {
            [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session], At),
            [nameof(LootTrackerView.SourceAvailable)] = true,
            [nameof(LootTrackerView.ViewMode)] = LootTrackerViewMode.Pve,
            [nameof(LootTrackerView.ExchangeSessionSummary)] = (Func<LootTrackerSession, ExchangeLootValuationSummary>)(_ => summary),
            [nameof(LootTrackerView.ExchangeRowSummary)] = (Func<LootTrackerSession, LootTrackerLoot, ExchangeLootValuationSummary>)Row,
            [nameof(LootTrackerView.ExchangeDaySummary)] = (Func<IEnumerable<LootTrackerSession>, ExchangeLootValuationSummary>)(_ => summary),
            [nameof(LootTrackerView.MarketSettingsContent)] = (RenderFragment)(builder =>
            {
                builder.OpenComponent<ExchangeMarketSettings>(0);
                builder.AddAttribute(1, nameof(ExchangeMarketSettings.Profiles), new (string Id, string Name)[] { (FirstProfile, "Synthetic profile A") });
                builder.AddAttribute(2, nameof(ExchangeMarketSettings.ActiveProfileId), FirstProfile);
                builder.AddAttribute(3, nameof(ExchangeMarketSettings.Servers), Servers);
                builder.CloseComponent();
            }),
        });
        Assert.Contains("data-testid=\"exchange-net-kina\">" + ExchangeMarketPresentation.Amount(135_520), html);
        Assert.Contains("data-testid=\"kina-total\">" + ExchangeMarketPresentation.Amount(12_345), html);
        Assert.Contains("data-exchange-loot-id=\"Item:999002\"", html);
        Assert.Contains("data-testid=\"exchange-reference-value\"", html);
        Assert.Contains("data-testid=\"exchange-tradeability-note\"", html);
        Assert.Contains(ExchangeMarketPresentation.TradeabilityHint, WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("data-testid=\"market-summary\"", html);
        Assert.Contains(ExchangeMarketPresentation.Amount(132_000), html);
        Assert.Contains(ExchangeMarketPresentation.Coverage(summary), WebUtility.HtmlDecode(html));
        Assert.Contains(ExchangeMarketPresentation.QuantityCoverage(summary), WebUtility.HtmlDecode(html));
        Assert.Contains(ExchangeMarketPresentation.Basis(summary.Bases[0]), WebUtility.HtmlDecode(html));
        Assert.Contains("https://aion2.exchange", html);
        Assert.Equal(12_345, session.KinaUnbound);
        var overlay = Overlay(summary);
        Assert.Equal(96, LootOverlayRenderer.DropsTopFor(overlay) - LootOverlayRenderer.DropsTopFor(overlay with { ExchangeMarketDisplay = false }));
        using var png = LootOverlayRenderer.RenderBitmap(overlay, OverlayThemeDefinition.DefaultId);
        Assert.Equal(LootOverlayRenderer.Measure(overlay), png.Size);
        if (Environment.GetEnvironmentVariable("SOULCREST_EXCHANGE_PREVIEW") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            var css = ReadCss("app.css") + ReadCss("loot-tracker.css") + ReadCss("loot-session-timeline.css");
            await File.WriteAllTextAsync(Path.Combine(output, "loot-exchange-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><style>" + css + "</style></head><body>" + html + "</body></html>");
            png.Save(Path.Combine(output, "loot-overlay-exchange-" + language + ".png"), ImageFormat.Png);
        }
    }

    private static ExchangeLootValuationSummary Summary() => new(135_520, 154_000, 2, 4, 2, false, 1, true, At.AddMinutes(-12), At.AddMinutes(-3))
        { ReferenceRows = 1, ReferenceGrossKina = 150_000, ReferenceNetKina = 132_000,
            PricedQuantity = 41, EligibleQuantity = 43, UnknownQuantity = 2, PendingQuantity = 1,
            Bases = [new(1, "Synthetic EU", "eu", "elyos", 10m, 2m)] };
    private static LootOverlaySnapshot Overlay(ExchangeLootValuationSummary summary) => new("synthetic-session", true, 12_345, 350_000,
        [new("Item:999001", "Synthetic material", 40), new("Item:210230006", "Synthetic equipment", 1), new("Item:999002", "Synthetic unknown", 2)])
    {
        HasPveActivity = true, SessionKind = LootTrackerSessionKind.Mixed, SessionKinaPerHour = 18_517,
        SessionElapsed = TimeSpan.FromMinutes(40), AnimationsEnabled = false,
        ExchangeMarketDisplay = true, ExchangeNetKina = summary.NetKina, ExchangeReferenceGrossKina = summary.ReferenceGrossKina, ExchangeReferenceNetKina = summary.ReferenceNetKina,
        ExchangePricedRows = summary.PricedRows, ExchangeEligibleRows = summary.EligibleRows, ExchangePendingRows = summary.PendingRows,
        ExchangeOldestObservedAt = summary.OldestObservedAt, ExchangeNewestObservedAt = summary.NewestObservedAt,
        ExchangePricedQuantity = summary.PricedQuantity, ExchangeEligibleQuantity = summary.EligibleQuantity, ExchangeBases = summary.Bases,
        ExchangeUnknownQuantity = summary.UnknownQuantity,
    };
    private static string ReadCss(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "Soulcrest.App", "wwwroot", "css", name));
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Source root required for optional preview.");
    }

    public sealed class CaptureSettings : ExchangeMarketSettings
    {
        [Parameter] public Action<ExchangeMarketSettings>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        public Task RefreshNow() => InvokeAsync(StateHasChanged);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        public SettingsService Settings => _services.GetRequiredService<SettingsService>();
        public Fixture(string language)
        {
            File.Delete(AppPaths.SettingsFile);
            _services = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton<UiState>().BuildServiceProvider();
            Settings.Current.UiLanguage = language; Settings.Current.LootTrackerHideSmallSessions = false; UiText.Language = language;
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render<T>(Dictionary<string, object?> values) where T : IComponent => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(values));
            return _root.ToHtmlString();
        });
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
#pragma warning disable BL0006
        public Task Event<T>(ExchangeMarketSettings component, string id, string name, T args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var builder = new RenderTreeBuilder();
            typeof(ExchangeMarketSettings).GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]);
            var frames = builder.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "id" && Equals(frames.Array[i].AttributeValue, id));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            for (var i = index + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                if (frames.Array[i].AttributeName == name)
                {
                    if (frames.Array[i].AttributeValue is EventCallback<T> typed) await typed.InvokeAsync(args);
                    else if (frames.Array[i].AttributeValue is EventCallback callback) await callback.InvokeAsync(args);
                    else if (frames.Array[i].AttributeValue is MulticastDelegate handler)
                    {
                        if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task;
                    }
                    else throw new InvalidOperationException("Unexpected callback.");
                    if (component is CaptureSettings captured) await captured.RefreshNow();
                    return;
                }
            throw new InvalidOperationException("Event callback missing.");
        });
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _services.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }
}
