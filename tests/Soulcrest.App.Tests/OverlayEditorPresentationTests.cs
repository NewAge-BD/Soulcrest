using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void MissingWorldContextMetadataDoesNotInventSessionValuesAndExamplesStayIndependent()
    {
        var waiting = new UnifiedLootOverlayState(null, false, new(null, null, null, 0), null, false, Now)
            { WaitingForWorldContext = true };
        var snapshot = Project(waiting);
        Assert.True(snapshot.WaitingForWorldContext);
        Assert.False(snapshot.SessionActive);
        Assert.False(snapshot.TrackingAvailable);
        Assert.Empty(snapshot.Drops);
        Assert.Equal("—", snapshot.Metrics["duration"].Value);
        Assert.False(Project(waiting with { WaitingForWorldContext = false }).WaitingForWorldContext);
        Assert.False(Project(waiting with { At = default }).WaitingForWorldContext);
        Assert.False(Project(waiting with { At = Now.AddSeconds(1) }).WaitingForWorldContext);
        Assert.False(Project(waiting with { TrackingAvailable = true }).WaitingForWorldContext);
        Assert.False(OverlaySnapshot.DemoFor("de").WaitingForWorldContext);
        Assert.False(OverlaySnapshot.DemoFor("en").WaitingForWorldContext);
    }

    [Fact]
    public void SharedSessionFactsKeepTotalsRatesAndNegativeNetSeparate()
    {
        var session = Session() with { KinaUnbound = 600, KinaBound = 1_200, AbyssPoints = 20, RankPointsGained = 10, RankPointsLost = 50 };
        var snapshot = Project(State(session));
        Assert.Equal("600", snapshot.Metrics["dropped-kina"].Value);
        Assert.Equal("1,200", snapshot.Metrics["dropped-kina-hour"].Value);
        Assert.Equal("1,200", snapshot.Metrics["bound-kina"].Value);
        Assert.Equal("2,400", snapshot.Metrics["bound-kina-hour"].Value);
        Assert.Equal("20", snapshot.Metrics["abyss-points"].Value);
        Assert.Equal("40", snapshot.Metrics["abyss-points-hour"].Value);
        Assert.Equal("-40", snapshot.Metrics["rank-net"].Value);
        Assert.Equal("-80", snapshot.Metrics["rank-net-hour"].Value);
        Assert.Equal("10", snapshot.Metrics["rank-gained"].Value);
        Assert.Equal("20", snapshot.Metrics["rank-gained-hour"].Value);
        Assert.Equal("50", snapshot.Metrics["rank-lost"].Value);
        Assert.Equal("100", snapshot.Metrics["rank-lost-hour"].Value);
        Assert.True(snapshot.Metrics["rank-net"].IsWarning);
        Assert.True(snapshot.Metrics["rank-net-hour"].IsWarning);
        Assert.True(snapshot.Metrics["rank-lost"].IsWarning);
        Assert.Equal("00:30:00", snapshot.Metrics["duration"].Value);
        Assert.Equal(session.Elapsed, snapshot.SessionElapsed);
        Assert.Equal(Now, snapshot.ClockUtcNow);
    }

    [Fact]
    public void UnknownChannelsNeverBecomeZeroAndAZeroDurationHasNoRate()
    {
        var unknown = Project(State(Session()));
        Assert.Equal("—", unknown.Metrics["dropped-kina"].Value);
        Assert.Equal("—", unknown.Metrics["bound-kina"].Value);
        Assert.Equal("—", unknown.Metrics["bound-kina-hour"].Value);
        Assert.Equal("—", unknown.Metrics["abyss-points"].Value);
        Assert.Equal("—", unknown.Metrics["rank-net"].Value);
        var zero = Project(State(Session() with
        {
            Elapsed = TimeSpan.Zero, KinaUnbound = 0, KinaBound = 0, AbyssPoints = 0, RankPointsGained = 0, RankPointsLost = 0,
        }));
        foreach (var key in new[] { "dropped-kina", "bound-kina", "abyss-points", "rank-net", "rank-gained", "rank-lost" })
            Assert.Equal("0", zero.Metrics[key].Value);
        foreach (var key in new[] { "dropped-kina-hour", "bound-kina-hour", "abyss-points-hour", "rank-net-hour", "rank-gained-hour", "rank-lost-hour" })
            Assert.Equal("—", zero.Metrics[key].Value);
        var oneChannel = Project(State(Session() with { RankPointsGained = 12 }));
        Assert.Equal("12", oneChannel.Metrics["rank-gained"].Value);
        Assert.Equal("—", oneChannel.Metrics["rank-lost"].Value);
        Assert.Equal("—", oneChannel.Metrics["rank-net"].Value);
    }

    [Theory]
    [InlineData("de", "1.200", "2.400", "Gebundene Kina", "Gebundene Kina / Stunde")]
    [InlineData("en", "1,200", "2,400", "Bound Kina", "Bound Kina / hour")]
    public void BoundKinaUsesItsOwnCurrentSessionChannelAndLanguage(string language, string total, string rate,
        string label, string rateLabel)
    {
        var session = Session() with { KinaBound = 1_200, KinaUnbound = null };
        var snapshot = OverlayEditorPresentation.Project(State(session), null, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal(total, snapshot.Metrics["bound-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["bound-kina-hour"].Value);
        Assert.Equal(label, snapshot.Metrics["bound-kina"].Label);
        Assert.Equal(rateLabel, snapshot.Metrics["bound-kina-hour"].Label);
        Assert.Equal("—", snapshot.Metrics["dropped-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["dropped-kina-hour"].Value);
        foreach (var state in new[] { State(session) with { TrackingAvailable = false },
                     State(session with { Status = LootTrackerSessionStatus.Completed }),
                     State(session with { KinaBound = -1, KinaUnbound = 900 }) })
        {
            var unavailable = OverlayEditorPresentation.Project(state, null, null, Now, TimeZoneInfo.Utc, language);
            Assert.Equal("—", unavailable.Metrics["bound-kina"].Value);
            Assert.Equal("—", unavailable.Metrics["bound-kina-hour"].Value);
        }
    }

    [Fact]
    public void OnlyTheQualifiedCurrentSessionCanSupplyMetricsOrReceiptNotices()
    {
        var session = Session() with { KinaUnbound = 500, RankPointsGained = 0, RankPointsLost = 12 };
        var notice = Notice();
        var unavailable = Project(State(session) with { TrackingAvailable = false }, notice: notice);
        Assert.False(unavailable.SessionActive);
        Assert.Equal("—", unavailable.Metrics["dropped-kina"].Value);
        Assert.Empty(unavailable.Drops);
        Assert.Null(unavailable.RareNotice);
        Assert.False(Project(State(session with { Status = LootTrackerSessionStatus.Completed })).SessionActive);
        Assert.False(Project(State(session with { EndedAt = Now })).SessionActive);
        Assert.False(Project(State(session) with { At = Now.AddSeconds(1) }).SessionActive);
        // A pure Open World loss is a fact even though the old complete rank HUD has a gain visibility gate.
        var current = Project(State(session), notice: notice);
        Assert.Equal("-12", current.Metrics["rank-net"].Value);
        Assert.NotNull(current.RareNotice);
        Assert.Null(Project(State(session), notice: notice with { ProfileId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }).RareNotice);
        Assert.Null(Project(State(session), notice: notice with { SessionId = "old-session" }).RareNotice);
        Assert.Null(Project(State(session), notice: notice with { ElapsedSeconds = 4 }).RareNotice);
    }

    [Fact]
    public void DailyCacheIsMarkedSavedAndPreviousFarmDaysNeverBecomeTodaysGoal()
    {
        var saved = new SavedDailyMonsterKina(Profile, 700_000, Now.AddHours(-1));
        var state = State(Session()) with { CachedDaily = saved, DailyConfirmed = false };
        var current = Project(state);
        Assert.Equal(700_000, current.DailyKina);
        Assert.False(current.DailyConfirmed);
        Assert.Equal(.7, current.DailyProgress.Fraction);
        Assert.Equal("Saved daily total", current.DailyProgress.Detail);
        var confirmed = Project(state with { DailyConfirmed = true, CachedDaily = saved with { Total = 1_000_000 } });
        Assert.True(confirmed.DailyConfirmed);
        Assert.Equal("Daily limit reached", confirmed.DailyProgress.Detail);
        Assert.Equal(1, confirmed.DailyProgress.Fraction);
        var resetAt = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 7, 0, 0, TimeSpan.Zero);
        var previous = Project(state with { CachedDaily = saved with { ObservedAt = resetAt.AddSeconds(-1) } });
        Assert.Null(previous.DailyKina);
        Assert.Null(previous.DailyProgress.Fraction);
        Assert.Equal("Daily total unknown", previous.DailyProgress.Detail);
        var reset = OverlayEditorPresentation.Project(state with { CachedDaily = saved with { ObservedAt = resetAt.AddSeconds(-1) } },
            null, null, resetAt, TimeZoneInfo.Utc, "en");
        Assert.Null(reset.DailyKina);
        Assert.Null(Project(state with { CachedDaily = saved with { ProfileId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } }).DailyKina);
    }

    [Theory]
    [InlineData(650_000L, 200_000L, .45, .2)]
    [InlineData(50_000L, 100_000L, 0, .05)]
    [InlineData(999_999L, long.MaxValue, 0, .999999)]
    public void DailySessionSegmentMatchesNativePveProjectionAndClampsToConfirmedDailyTotal(
        long daily, long income, double start, double width)
    {
        var state = ConfirmedDailyState(PveSession(income), daily);
        var progress = Project(state).DailyProgress;
        Assert.Equal(start, progress.SessionStartFraction!.Value, 9);
        Assert.Equal(width, progress.SessionFraction!.Value, 9);
        Assert.Equal(progress.Fraction!.Value, progress.SessionStartFraction.Value + progress.SessionFraction.Value, 9);

        var native = UnifiedLootOverlaySource.Project(state, AbyssProgressSnapshot.Empty, new());
        var bounds = LootOverlayRenderer.SessionSegmentBounds(native);
        var innerWidth = LootOverlayRenderer.DailyBar.Width - 2d;
        Assert.Equal(start, (bounds.Left - LootOverlayRenderer.DailyBar.Left - 1) / innerWidth, 6);
        Assert.Equal(width, bounds.Width / innerWidth, 6);
    }

    [Theory]
    [InlineData(LootTrackerActivityOrigin.Pve, LootTrackerTimelineEventKind.Drop, true)]
    [InlineData(LootTrackerActivityOrigin.Pve, LootTrackerTimelineEventKind.KinaBound, true)]
    [InlineData(LootTrackerActivityOrigin.Pve, LootTrackerTimelineEventKind.KinaUnbound, true)]
    [InlineData(LootTrackerActivityOrigin.Pve, LootTrackerTimelineEventKind.AbyssPoints, false)]
    [InlineData(LootTrackerActivityOrigin.Lifeskill, LootTrackerTimelineEventKind.KinaUnbound, false)]
    [InlineData(LootTrackerActivityOrigin.Abyss, LootTrackerTimelineEventKind.KinaUnbound, false)]
    [InlineData(null, LootTrackerTimelineEventKind.KinaUnbound, false)]
    public void DailySessionSegmentKeepsTheNativePveTimelineGate(LootTrackerActivityOrigin? origin,
        LootTrackerTimelineEventKind kind, bool expected)
    {
        var session = PveSession() with
        {
            TimelineEvents = [new("activity", kind, Now, TimeSpan.FromMinutes(30)) { Origin = origin }],
        };
        var state = ConfirmedDailyState(session);
        var progress = Project(state).DailyProgress;
        var native = UnifiedLootOverlaySource.Project(state, AbyssProgressSnapshot.Empty, new());
        Assert.Equal(expected, progress.SessionFraction is > 0);
        Assert.Equal(expected, progress.SessionStartFraction is not null);
        Assert.Equal(expected, LootOverlayRenderer.SessionSegmentBounds(native).Width > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void DailySessionSegmentNeverInfersUnboundIncomeFromOtherCurrencyOrMarketValues(long? income)
    {
        var session = PveSession(income) with
        {
            KinaBound = 900_000, AbyssPoints = 300_000,
            TimelineEvents = [new("bound-only", LootTrackerTimelineEventKind.KinaBound, Now, TimeSpan.FromMinutes(30))
                { Origin = LootTrackerActivityOrigin.Pve, Amount = 900_000 }],
        };
        var market = new OverlayEditorMarketSnapshot
        {
            Available = true, SessionId = session.Id, SourceProfileId = Profile, NetKina = 800_000,
        };
        var snapshot = Project(ConfirmedDailyState(session), market);
        Assert.Equal("800,000", snapshot.Metrics["market-kina"].Value);
        Assert.Equal(.65, snapshot.DailyProgress.Fraction);
        Assert.Null(snapshot.DailyProgress.SessionStartFraction);
        Assert.Null(snapshot.DailyProgress.SessionFraction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1_000_000L)]
    [InlineData(1_000_001L)]
    public void DailySessionSegmentRequiresAKnownPositiveGoalBelowTheCap(long? total)
    {
        var state = ConfirmedDailyState(PveSession());
        state = state with { CachedDaily = total is { } value ? new(Profile, value, Now) : null };
        var progress = Project(state).DailyProgress;
        Assert.Null(progress.SessionStartFraction);
        Assert.Null(progress.SessionFraction);
    }

    [Fact]
    public void DailySessionSegmentCannotSurviveTrackingSessionOrDailySourceInvalidation()
    {
        var session = PveSession();
        var state = ConfirmedDailyState(session);
        var originalState = System.Text.Json.JsonSerializer.Serialize(state);
        var current = Project(state);
        Assert.Equal(.2, current.DailyProgress.SessionFraction);
        var invalid = new[]
        {
            state with { Session = null },
            state with { TrackingAvailable = false },
            state with { DailyConfirmed = false },
            state with { At = Now.AddSeconds(1) },
            state with { Session = session with { Status = LootTrackerSessionStatus.Completed } },
            state with { Session = session with { Status = LootTrackerSessionStatus.Interrupted } },
            state with { Session = session with { EndedAt = Now } },
            state with { Session = session with { StartedAt = Now.AddSeconds(1) } },
            state with { Session = session with { Kind = LootTrackerSessionKind.Abyss } },
            state with { Session = session with { Kind = LootTrackerSessionKind.Lifeskill } },
            state with { Session = session with { TimelineEvents = [] } },
            state with { Session = session with { Character = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Example") } },
            state with { CachedDaily = new(Profile, 650_000, Now.AddDays(-1)) },
        };
        foreach (var unavailable in invalid)
        {
            var next = Project(unavailable);
            Assert.Null(next.DailyProgress.SessionStartFraction);
            Assert.Null(next.DailyProgress.SessionFraction);
        }
        Assert.Equal(.45, current.DailyProgress.SessionStartFraction);
        Assert.Equal(.2, current.DailyProgress.SessionFraction);
        Assert.Equal(originalState, System.Text.Json.JsonSerializer.Serialize(state));
    }

    [Fact]
    public void DailyProgressRetainsTheFourArgumentContractWithoutInventingASessionSegment()
    {
        var progress = new OverlayDailyKinaProgress("650,000 / 1,000,000", "Confirmed daily total", "65%", .65);
        Assert.Null(progress.SessionStartFraction);
        Assert.Null(progress.SessionFraction);
    }

    [Fact]
    public void MarketTotalsAndRatesUseOneMatchingEstimateAndMarkTheKnownPartialAsIncomplete()
    {
        var session = Session();
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = Profile, Available = true, OnlineEnabled = true,
            NetKina = 400, PartialNetKina = 400, EligibleRows = 2, PricedRows = 2,
        };
        var current = Project(State(session), market);
        Assert.Equal("400", current.Metrics["market-kina"].Value);
        Assert.Equal("800", current.Metrics["market-kina-hour"].Value);
        var partial = Project(State(session), market with { NetKina = null, PartialNetKina = 100, PricedRows = 1 });
        Assert.Equal("100 + ?", partial.Metrics["market-kina"].Value);
        Assert.Equal("200 + ?", partial.Metrics["market-kina-hour"].Value);
        Assert.Contains("Known partial value: 100", partial.Metrics["market-kina"].Detail);
        Assert.Equal("—", Project(State(session), market with { SessionId = "old-session" }).Metrics["market-kina"].Value);
        Assert.Equal("—", Project(State(session), market with { SourceProfileId = "other-profile" }).Metrics["market-kina"].Value);
        Assert.Equal("0", Project(State(session), market with { NetKina = 0, PartialNetKina = 0 }).Metrics["market-kina"].Value);
        Assert.Equal("0", Project(State(session), market with { NetKina = 0, PartialNetKina = 0 }).Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("de", "12.345 + ?", "49.380 + ?")]
    [InlineData("en", "12,345 + ?", "49,380 + ?")]
    public void KnownMarketPartialAndItsRateUseTheSameSessionDurationAndLocale(string language, string total, string rate)
    {
        var session = Session() with { Elapsed = TimeSpan.FromMinutes(15) };
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = Profile, Available = true, OnlineEnabled = true,
            NetKina = null, PartialNetKina = 12_345, EligibleRows = 2, PricedRows = 1,
        };
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal(total, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["market-kina-hour"].Value);
        Assert.Equal(session.Elapsed, snapshot.SessionElapsed);
        Assert.Null(market.NetKina);
        Assert.Equal(12_345, market.PartialNetKina);
    }

    [Theory]
    [InlineData(0, 0L, "—")]
    [InlineData(0, 100L, "—")]
    [InlineData(1, 0L, "0 + ?")]
    [InlineData(1, -1L, "—")]
    [InlineData(1, null, "—")]
    public void OnlyANonnegativeSubtotalWithAtLeastOnePricedRowCanProduceAPartialDisplay(int pricedRows, long? subtotal, string expected)
    {
        var session = Session();
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = Profile, Available = true,
            NetKina = null, PartialNetKina = subtotal, EligibleRows = 2, PricedRows = pricedRows,
        };
        var snapshot = Project(State(session), market);
        Assert.Equal(expected, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(expected, snapshot.Metrics["market-kina-hour"].Value);
        Assert.Null(market.NetKina);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AZeroSessionDurationNeverProducesAMarketRateEvenForAKnownFullOrPartialAmount(bool complete)
    {
        var session = Session() with { Elapsed = TimeSpan.Zero };
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = Profile, Available = true,
            NetKina = complete ? 100 : null, PartialNetKina = 100, EligibleRows = 2, PricedRows = complete ? 2 : 1,
        };
        var snapshot = Project(State(session), market);
        Assert.Equal(complete ? "100" : "100 + ?", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("unavailable")]
    public void APartialEstimateFromAnotherOrUnavailableOwnerNeverSuppliesEitherMarketValue(string mismatch)
    {
        var session = Session();
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = mismatch == "session" ? "other-session" : session.Id,
            SourceProfileId = mismatch == "profile" ? "other-profile" : Profile,
            Available = mismatch != "unavailable", NetKina = null, PartialNetKina = 100, EligibleRows = 2, PricedRows = 1,
        };
        var snapshot = Project(State(session), market);
        Assert.Equal("—", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
        Assert.Null(market.NetKina);
    }

    [Fact]
    public void BothMarketModulesExposeTheSamePriceProvenanceWithoutLocalProfileIds()
    {
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = "current-session", SourceProfileId = Profile, Available = true, OnlineEnabled = true,
            MarketProfile = new() { Region = "EU", Faction = "Asmodian", ServerId = 1, SalesTaxPercent = 10, RegistrationFeePercent = 2 },
            ServerName = "Example market", OldestObservedAt = Now.AddMinutes(-3), NewestObservedAt = Now,
            NetKina = null, PartialNetKina = 300, PricedRows = 1, EligibleRows = 2, PricedQuantity = 10, EligibleQuantity = 15,
            HasEquipmentReference = true, EquipmentReferenceNetKina = 300,
        };
        var snapshot = Project(State(Session()), market);
        var tooltip = snapshot.Metrics["market-kina"].Tooltip;
        Assert.NotNull(tooltip);
        Assert.Equal(tooltip, snapshot.Metrics["market-kina-hour"].Tooltip);
        Assert.Contains("aion2.exchange", tooltip);
        Assert.Contains("EU", tooltip); Assert.Contains("Asmodian", tooltip); Assert.Contains("Example market", tooltip);
        Assert.Contains("Sales tax: 10 %", tooltip); Assert.Contains("Registration fee: 2 %", tooltip);
        Assert.Contains("2026-11-02 11:57:00 UTC", tooltip); Assert.Contains("2026-11-02 12:00:00 UTC", tooltip);
        Assert.Contains("Quantity coverage: 10/15", tooltip); Assert.Contains("Unknown quantity: 5", tooltip);
        Assert.Contains("Individual item tradability is not confirmed", tooltip);
        Assert.Contains("Equipment uses the base price", tooltip);
        Assert.DoesNotContain(Profile, tooltip);
    }

    [Fact]
    public void DurationKeepsWholeDaysAndDoesNotSubtractInventedPauses()
    {
        var session = Session() with { StartedAt = Now.AddHours(-25.5), Elapsed = TimeSpan.FromHours(25.5), KinaUnbound = 2550 };
        var snapshot = Project(State(session));
        Assert.Equal("25:30:00", snapshot.Metrics["duration"].Value);
        Assert.Equal("100", snapshot.Metrics["dropped-kina-hour"].Value);
    }

    [Fact]
    public void CatalogIsCachedByNameLanguageAndPreservesExactLongQuantities()
    {
        var session = Session() with { Loot = [new("Item:510116011", "source", LootTrackerLootKind.Item, long.MaxValue)] };
        var english = OverlayEditorPresentation.Project(State(session), null, null, Now, TimeZoneInfo.Utc, "en", "de");
        var german = OverlayEditorPresentation.Project(State(session), null, null, Now, TimeZoneInfo.Utc, "de", "de");
        Assert.Same(english.ItemCatalog, german.ItemCatalog);
        var drop = Assert.Single(english.Drops);
        Assert.Equal(MonsterLootItemCatalog.Default.Name(510116011, "de"), drop.Name);
        Assert.Equal("9,223,372,036,854,775,807", drop.QuantityText);
        Assert.Equal("9.223.372.036.854.775.807", Assert.Single(german.Drops).QuantityText);
        Assert.Contains(english.ItemCatalog, item => item.Key == "Item:210350009");
        Assert.Contains(english.ItemCatalog, item => item.Key.StartsWith("PetSoul:", StringComparison.Ordinal));
        Assert.Equal(english.ItemCatalog.Count, english.ItemCatalog.Select(item => item.Key).Distinct().Count());
        var namesChanged = OverlayEditorPresentation.Project(State(session), null, null, Now, TimeZoneInfo.Utc, "en", "en");
        Assert.NotSame(english.ItemCatalog, namesChanged.ItemCatalog);
    }

    [Fact]
    public void OneCaptureReadsOneSessionOneMarketEstimateAndTheSameClockInstant()
    {
        var clock = new Clock();
        var sessionReads = 0; var marketReads = 0; var noticeReads = 0;
        var state = State(Session());
        using var source = new OverlayEditorPresentationSource(() => { sessionReads++; return state; },
            () => { noticeReads++; return null; }, (session, at) =>
            {
                marketReads++; Assert.Same(state.Session, session); Assert.Equal(Now, at);
                return new() { SessionId = session.Id, SourceProfileId = Profile, Available = true, NetKina = 200 };
            }, () => new Lease(), clock);
        var snapshot = source.Capture(language: "en");
        Assert.Equal(1, sessionReads); Assert.Equal(1, marketReads); Assert.Equal(1, noticeReads);
        Assert.Equal(1, clock.Reads);
        Assert.Equal(Now, snapshot.ClockUtcNow);
        Assert.Equal("400", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Fact]
    public void CompleteExamplesNeverReadLiveSourcesAndRemainDeterministicForPureUiHosts()
    {
        using var source = new OverlayEditorPresentationSource(() => throw new InvalidOperationException("No session read"),
            () => throw new InvalidOperationException("No receipt read"), (_, _) => throw new InvalidOperationException("No market read"),
            () => throw new InvalidOperationException("No market lease"), new Clock(),
            genus: () => throw new InvalidOperationException("No live genus read"),
            acquireGenus: () => throw new InvalidOperationException("No menu capture lease"));
        var example = source.Capture(examples: true, language: "en");
        Assert.Equal(23, example.Metrics.Count);
        foreach (var definition in OverlayCatalog.Widgets.Where(definition => definition.Kind is not ("abyss-killfeed" or "abyss-rank-progress")))
            Assert.Contains(definition.Kind, example.Metrics.Keys);
        Assert.True(example.Abyss.WorldContextVisible);
        Assert.NotEmpty(example.Abyss.KillFeed);
        Assert.Equal(4, example.Drops.Count);
        Assert.NotNull(example.RareNotice);
        Assert.Equal(Now, example.ClockUtcNow);
        Assert.Equal(OverlaySnapshot.DemoFor("en").Metrics["dropped-kina"], OverlaySnapshot.DemoFor("en").Metrics["dropped-kina"]);
        Assert.Equal("1,234", example.Metrics["preview-metric"].Value);
        Assert.Equal("267,540", example.Metrics["bound-kina"].Value);
        Assert.NotEqual(example.Metrics["dropped-kina"].Value, example.Metrics["bound-kina"].Value);
        Assert.Equal(.5, example.DailyProgress.SessionStartFraction);
        Assert.Equal(.18472, example.DailyProgress.SessionFraction);
        foreach (var language in new[] { "de", "en" })
        {
            var demo = OverlaySnapshot.DemoFor(language);
            Assert.Equal(example.DailyProgress.SessionStartFraction, demo.DailyProgress.SessionStartFraction);
            Assert.Equal(example.DailyProgress.SessionFraction, demo.DailyProgress.SessionFraction);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItemAnimationPreferenceCanChangeInBothModesWithoutChangingTheirData(bool examples)
    {
        var preferences = new AppSettings { LootDropAnimationsEnabled = true };
        var state = State(Session() with
        {
            KinaUnbound = 600,
            Loot = [new("Item:210230006", "Example rare item", LootTrackerLootKind.Item, 2)],
        });
        var sessionBefore = System.Text.Json.JsonSerializer.Serialize(state);
        using var source = new OverlayEditorPresentationSource(() => state, () => Notice(), (_, _) => new(),
            () => new Lease(), new Clock(), itemAnimationsEnabled: () => preferences.LootDropAnimationsEnabled);
        var enabled = source.Capture(examples, "en");
        Assert.True(enabled.ItemAnimationsEnabled);

        preferences.LootDropAnimationsEnabled = false;
        var disabled = source.Capture(examples, "en");
        Assert.False(disabled.ItemAnimationsEnabled);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(enabled),
            System.Text.Json.JsonSerializer.Serialize(disabled with { ItemAnimationsEnabled = true }));
        Assert.Equal(sessionBefore, System.Text.Json.JsonSerializer.Serialize(state));

        preferences.LootDropAnimationsEnabled = true;
        var reenabled = source.Capture(examples, "en");
        Assert.True(reenabled.ItemAnimationsEnabled);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(enabled), System.Text.Json.JsonSerializer.Serialize(reenabled));
    }

    [Fact]
    public void ItemAnimationDefaultsRemainEnabledForPureSnapshotsAndExamples()
    {
        Assert.True(new OverlaySnapshot().ItemAnimationsEnabled);
        Assert.True(OverlaySnapshot.DemoFor("de").ItemAnimationsEnabled);
        Assert.True(OverlaySnapshot.DemoFor("en").ItemAnimationsEnabled);
    }

    [Fact]
    public void AConcurrentStatePublicationCannotBecomeFutureDataBecauseTheFrameClockWasReadTooEarly()
    {
        var clock = new Clock();
        using var source = new OverlayEditorPresentationSource(() =>
        {
            clock.Advance(TimeSpan.FromTicks(1));
            return State(Session()) with { At = Now.AddTicks(1) };
        }, () => null, (_, _) => new(), () => new Lease(), clock);
        var frame = source.Capture(language: "en");
        Assert.True(frame.SessionActive);
        Assert.Equal(Now.AddTicks(1), frame.ClockUtcNow);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    public void OnlyAnExplicitLiveMarketConsumerAcquiresPrices(bool examples, bool market, int expected)
    {
        var leases = 0; var releases = 0;
        using var source = new OverlayEditorPresentationSource(() => State(Session()), () => null,
            (_, _) => new(), () => { leases++; return new Lease(() => releases++); }, new Clock());
        var consumer = source.Acquire(examples, market);
        Assert.Equal(expected, leases);
        consumer.Dispose(); consumer.Dispose();
        Assert.Equal(expected, releases);
    }

    [Fact]
    public void ConsumersShareOneHeartbeatAndTheLastReleaseStopsIt()
    {
        var clock = new Clock();
        var changes = 0;
        using var source = new OverlayEditorPresentationSource(() => State(Session()), () => null, (_, _) => new(), () => new Lease(), clock);
        source.Changed += () => changes++;
        var first = source.Acquire();
        var second = source.Acquire(examples: true);
        Assert.Equal(1, clock.TimersCreated);
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(1, changes);
        first.Dispose(); clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(2, changes);
        second.Dispose(); clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(2, changes);
        Assert.Equal(0, clock.ActiveTimers);
    }

    private static OverlaySnapshot Project(UnifiedLootOverlayState state, OverlayEditorMarketSnapshot? market = null, RareDropNotice? notice = null) =>
        OverlayEditorPresentation.Project(state, market, notice, Now, TimeZoneInfo.Utc, "en");

    private static LootTrackerSession Session() => new()
    {
        Id = "current-session", Status = LootTrackerSessionStatus.Live, Kind = LootTrackerSessionKind.Mixed,
        UnifiedVersion = 1, StartedAt = Now.AddMinutes(-30), Elapsed = TimeSpan.FromMinutes(30), Character = new(Profile, "Example"),
    };
    private static LootTrackerSession PveSession(long? income = 200_000) => Session() with
    {
        KinaUnbound = income, TimelineVersion = 1,
        TimelineEvents = [new("pve-kina", LootTrackerTimelineEventKind.KinaUnbound, Now, TimeSpan.FromMinutes(30))
            { Origin = LootTrackerActivityOrigin.Pve, Amount = income }],
    };
    private static UnifiedLootOverlayState ConfirmedDailyState(LootTrackerSession session, long total = 650_000) =>
        State(session) with { CachedDaily = new(Profile, total, Now), DailyConfirmed = true };
    private static UnifiedLootOverlayState State(LootTrackerSession session) => new(session, true, new(null, null, null, 0), null, false, Now);
    private static RareDropNotice Notice() => new("notice", "Example rare item", 2, LootItemRarity.Unique, null, Now)
        { SessionId = "current-session", ProfileId = Profile };

    private sealed class Lease(Action? released = null) : IDisposable
    {
        private Action? _released = released;
        public void Dispose() => Interlocked.Exchange(ref _released, null)?.Invoke();
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<ClockTimer> _timers = [];
        private long _stamp;
        public int Reads { get; private set; }
        public int TimersCreated { get; private set; }
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { Reads++; return Now.AddTicks(_stamp); }
        public override long GetTimestamp() => _stamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            var timer = new ClockTimer(this, callback, state, dueTime, period);
            _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _stamp += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire(_stamp);
        }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private long _next = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._stamp + dueTime.Ticks;
            private long _period = period.Ticks;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan due, TimeSpan interval)
            {
                if (Disposed) return false;
                _next = due == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._stamp + due.Ticks; _period = interval.Ticks; return true;
            }
            public void Fire(long now)
            {
                while (!Disposed && _next <= now)
                {
                    _next = _period > 0 ? _next + _period : long.MaxValue;
                    callback(state);
                }
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
