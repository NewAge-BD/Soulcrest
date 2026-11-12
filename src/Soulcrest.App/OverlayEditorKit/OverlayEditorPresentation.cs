using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Soulcrest.Core.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Pure formatting of already qualified session facts. No price requests, journals or audio.</summary>
internal static class OverlayEditorPresentation
{
    private static readonly Lazy<HashSet<int>> ItemIds = new(ReadItemIds);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<OverlayLootItem>> Catalogs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, HashSet<string>> CatalogKeys = new(StringComparer.Ordinal);
    private static readonly int[] CrystalItemIds = [531900005, 531900004, 531900006, 531900007, 531900008];

    internal static LootTrackerSession? CurrentSession(UnifiedLootOverlayState state, DateTimeOffset now) =>
        state.TrackingAvailable && state.At != default && state.At <= now
        && state.Session is { Status: LootTrackerSessionStatus.Live, EndedAt: null, Id.Length: > 0 } session
        && session.StartedAt != default && session.StartedAt <= now && session.Elapsed >= TimeSpan.Zero
        && Guid.TryParse(session.Character?.ProfileId, out var profile) && profile != Guid.Empty ? session : null;

    internal static OverlaySnapshot Project(UnifiedLootOverlayState state, OverlayEditorMarketSnapshot? market,
        RareDropNotice? notice, DateTimeOffset now, TimeZoneInfo localZone, string language, string? nameLanguage = null,
        AbyssRankOverlaySnapshot? abyss = null)
    {
        var german = language == "de";
        var culture = CultureInfo.GetCultureInfo(german ? "de-DE" : "en-US");
        string T(string text) => german ? text : English.GetValueOrDefault(text, text);
        var session = CurrentSession(state, now);
        var elapsed = session?.Elapsed ?? TimeSpan.Zero;
        var names = (nameLanguage ?? language) == "de" ? "de" : "en";
        var drops = Drops(session, names, culture);
        var catalog = Catalog(names);
        var keys = CatalogKeys.GetOrAdd(names, _ => catalog.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));
        var extraChoices = drops.Where(item => !keys.Contains(item.Key)).ToArray();
        var choices = extraChoices.Length == 0 ? catalog : Array.AsReadOnly(catalog.Concat(extraChoices).ToArray());
        var matchingMarket = session is not null && market is { Available: true }
            && market.SessionId == session.Id && market.SourceProfileId == session.Character?.ProfileId ? market : null;
        var net = session is null ? null : LootTrackerPresentation.NetRankPoints(session);
        var metrics = new Dictionary<string, OverlayMetric>(StringComparer.Ordinal);
        void Number(string kind, string label, long? amount, bool signed = false, bool loss = false, string? detail = null) =>
            metrics[kind] = new(T(label), Amount(amount, culture, signed), detail ?? T(session is null ? "Keine laufende Session" : "Aktuelle Session"),
                IsWarning: amount is not null && (signed && amount < 0 || loss && amount > 0),
                Tone: amount > 0 && !loss ? OverlayMetricTone.Positive : amount is null ? OverlayMetricTone.Muted : OverlayMetricTone.Default);
        void Rate(string kind, string label, long? amount, bool signed = false, bool loss = false)
        {
            var rate = amount is null || elapsed <= TimeSpan.Zero || !signed && amount < 0 ? null : (double?)(amount.Value / elapsed.TotalHours);
            if (rate is { } value && !double.IsFinite(value)) rate = null;
            metrics[kind] = new(T(label), RateAmount(rate, culture, signed), T("pro Stunde"),
                IsWarning: rate is not null && (signed && rate < 0 || loss && rate > 0),
                Tone: rate > 0 && !loss ? OverlayMetricTone.Positive : rate is null ? OverlayMetricTone.Muted : OverlayMetricTone.Default);
        }
        Number("dropped-kina", "Erhaltene Kina", Nonnegative(session?.KinaUnbound));
        Number("abyss-points", "Verdiente AP", Nonnegative(session?.AbyssPoints));
        Number("rank-net", "Rangpunkte netto", net, signed: true);
        Number("rank-gained", "Gewonnene Rangpunkte", Nonnegative(session?.RankPointsGained));
        Number("rank-lost", "Verlorene Rangpunkte", Nonnegative(session?.RankPointsLost), loss: true);
        Rate("dropped-kina-hour", "Erhaltene Kina / Stunde", Nonnegative(session?.KinaUnbound));
        Rate("abyss-points-hour", "AP / Stunde", Nonnegative(session?.AbyssPoints));
        Rate("rank-net-hour", "Rangpunkte netto / Stunde", net, signed: true);
        Rate("rank-gained-hour", "Gewonnene Rangpunkte / Stunde", Nonnegative(session?.RankPointsGained));
        Rate("rank-lost-hour", "Verlorene Rangpunkte / Stunde", Nonnegative(session?.RankPointsLost), loss: true);
        var marketDetail = matchingMarket is null ? T("Marktwert unbekannt") : MarketDetail(matchingMarket, culture, T);
        var marketTooltip = matchingMarket is null ? T("Marktwert unbekannt") : MarketTooltip(matchingMarket, culture, T);
        // User request 2026-11-08: show a proven partial estimate without claiming
        // a complete total. Its uncertainty marker must survive ShowLabel=false.
        // Averages must never pick up last-offer fallbacks retained by older snapshots.
        var averageMarket = matchingMarket is { AverageFallbackEnabled: true } or { Uses24HourAverage: true };
        var historicalAge = !averageMarket && matchingMarket?.OldestHistoricalSeenAt is { } lastSeen
            && lastSeen > DateTimeOffset.UnixEpoch && lastSeen - now <= ExchangeMarketCache.FutureSkew
            ? HistoricalAge(lastSeen, now, culture) : null;
        var historicalMarket = !averageMarket && matchingMarket is { NetKina: null, KnownEstimateNetKina: >= 0, HistoricalRows: > 0,
            UnvaluedRows: >= 0, UnvaluedQuantity: >= 0 } && historicalAge is not null;
        var partialMarket = matchingMarket is { NetKina: null, PartialNetKina: >= 0, PricedRows: > 0, UnknownRows: > 0 };
        var marketAmount = Nonnegative(historicalMarket ? matchingMarket!.KnownEstimateNetKina
            : partialMarket ? matchingMarket!.PartialNetKina : matchingMarket?.NetKina);
        Number("market-kina", averageMarket ? "Marktwert netto" : "Aktueller Marktwert netto", marketAmount, detail: marketDetail);
        metrics["market-kina"] = metrics["market-kina"] with { Tooltip = marketTooltip };
        Rate("market-kina-hour", "Marktwert netto / Stunde", marketAmount);
        metrics["market-kina-hour"] = metrics["market-kina-hour"] with { Detail = marketDetail, Tooltip = marketTooltip };
        if (partialMarket || historicalMarket)
            foreach (var kind in new[] { "market-kina", "market-kina-hour" })
            {
                var metric = metrics[kind];
                var incomplete = historicalMarket ? matchingMarket!.UnvaluedRows > 0 : partialMarket;
                metrics[kind] = metric with
                {
                    Value = metric.Value == "—" ? "—" : (historicalMarket ? "≈ " : "") + metric.Value + (incomplete ? " + ?" : ""),
                    HistoricalAge = historicalMarket && metric.Value != "—" ? historicalAge : null,
                    Tone = OverlayMetricTone.Muted, IsWarning = false,
                };
            }
        metrics["duration"] = new(T("Sessiondauer"), session is null ? "—" : Duration(elapsed),
            T(session is null ? "Keine laufende Session" : "Gemeinsame Sessionzeit"));
        metrics["clock"] = new(T("Lokale Uhrzeit"), TimeZoneInfo.ConvertTime(now, localZone).ToString("HH:mm", culture));
        // The shared metric supplies the module header only; its nine qualified rows use GenusInsight.
        metrics["genus-insight"] = new(german ? "Gattungsverständnis" : "Genus Insight", "—");

        var daily = state.CachedDaily is { Total: >= 0 } saved && saved.ObservedAt != default && saved.ObservedAt <= now
            && LootFarmDay.Get(saved.ObservedAt) == LootFarmDay.Get(now)
            && Guid.TryParse(saved.ProfileId, out var dailyProfile) && dailyProfile != Guid.Empty
            && (session is null || saved.ProfileId == session.Character?.ProfileId) ? saved.Total : (long?)null;
        var dailyConfirmed = daily is not null && state.DailyConfirmed && state.TrackingAvailable;
        const long limit = 1_000_000;
        var fraction = daily is { } total ? Math.Clamp(total / (double)limit, 0, 1) : (double?)null;
        double? sessionStartFraction = null, sessionFraction = null;
        // Match the native PvE bar: only known current-session income can explain
        // a portion of today's confirmed total; a full goal has no moving segment.
        if (dailyConfirmed && daily is > 0 and < limit && session is { KinaUnbound: > 0 }
            && session.Kind is not (LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Lifeskill)
            && session.TimelineEvents.Any(fact => fact.Origin == LootTrackerActivityOrigin.Pve
                && fact.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.KinaBound or LootTrackerTimelineEventKind.KinaUnbound))
        {
            var contribution = Math.Clamp(session.KinaUnbound.Value, 0, daily.Value);
            sessionStartFraction = (daily.Value - contribution) / (double)limit;
            sessionFraction = contribution / (double)limit;
        }
        var dailyDetail = T(daily is null ? "Tagesstand unbekannt" : !dailyConfirmed ? "Gespeicherter Tagesstand"
            : daily >= limit ? "Tageslimit erreicht" : "Bestätigter Tagesstand");
        var dailyProgress = new OverlayDailyKinaProgress(daily is { } dailyValue
            ? dailyValue.ToString("N0", culture) + " / " + limit.ToString("N0", culture) : "— / " + limit.ToString("N0", culture),
            dailyDetail, fraction?.ToString("P0", culture) ?? "—", fraction, sessionStartFraction, sessionFraction);
        metrics["daily-kina"] = new(T("Tages-Kina"), dailyProgress.Value, dailyDetail);
        foreach (var (kind, label) in new[] { ("drop-grid", "Drop-Raster"), ("drop-strip", "Drop-Leiste"), ("drop-list", "Drop-Liste") })
            metrics[kind] = new(T(label), drops.Count.ToString("N0", culture), T("Aktuelle Sessionbeute"));
        OverlayRareNotice? visual = session is not null
            && notice is { ElapsedSeconds: >= 0 and < RareDropAlertService.DurationSeconds, Quantity: > 0 }
            && notice.SessionId == session.Id && notice.ProfileId == session.Character?.ProfileId
            ? new(notice.Id, notice.Name, notice.Quantity, notice.Quantity.ToString("N0", culture), notice.IconPath,
                notice.Rarity, notice.ElapsedSeconds, notice.Name + " ×" + notice.Quantity.ToString("N0", culture))
                { LootKey = notice.LootKey } : null;
        metrics["rare-notice"] = new(T("Aktueller Fundhinweis"), visual?.Name ?? "—", visual is null ? T("Kein aktueller Fundhinweis") : visual.QuantityText);
        metrics["preview-metric"] = OverlaySnapshot.LegacyExampleMetric(language);
        return new()
        {
            UiLanguage = german ? "de" : "en", NameLanguage = names, ClockUtcNow = now, SessionId = session?.Id,
            SessionActive = session is not null, TrackingAvailable = state.TrackingAvailable,
            WaitingForWorldContext = state.WaitingForWorldContext && !state.TrackingAvailable && state.At != default && state.At <= now,
            SessionElapsed = elapsed, Drops = drops, ItemCatalog = choices, DailyKina = daily,
            DailyConfirmed = dailyConfirmed, DailyLimit = limit, DailyProgress = dailyProgress, RareNotice = visual,
            Abyss = ProjectAbyss(abyss),
            Metrics = new ReadOnlyDictionary<string, OverlayMetric>(metrics),
        };
    }

    internal static OverlaySnapshot Examples(DateTimeOffset now, TimeZoneInfo localZone, string language, string? nameLanguage = null)
    {
        const string profile = "dddddddddddddddddddddddddddddddd";
        var session = new LootTrackerSession
        {
            Id = "overlay-editor-example", Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1,
            Status = LootTrackerSessionStatus.Live, StartedAt = now.AddMinutes(-42.5), Elapsed = TimeSpan.FromMinutes(42.5),
            Character = new(profile, "Example"), KinaUnbound = 184_720, AbyssPoints = 3_240,
            RankPointsGained = 1_450, RankPointsLost = 230, TimelineVersion = 1,
            TimelineEvents = [new("example-kina", LootTrackerTimelineEventKind.KinaUnbound, now, TimeSpan.FromMinutes(42.5))
                { Origin = LootTrackerActivityOrigin.Pve, Amount = 184_720 }],
            Loot = [new("Item:531900004", "", LootTrackerLootKind.Soul, 24),
                new("Item:210230006", "", LootTrackerLootKind.Item, 1),
                new("Item:510116011", "", LootTrackerLootKind.Item, 12),
                new("Item:511490001", "", LootTrackerLootKind.Item, 3)],
        };
        var state = new UnifiedLootOverlayState(session, true, new(null, null, null, 0),
            new(profile, 684_720, now), true, now);
        var market = new OverlayEditorMarketSnapshot
        {
            SessionId = session.Id, SourceProfileId = profile, Available = true, OnlineEnabled = true,
            NetKina = 912_345, PartialNetKina = 912_345, PricedRows = 3, EligibleRows = 3,
            PricedQuantity = 16, EligibleQuantity = 16, OldestObservedAt = now.AddMinutes(-2), NewestObservedAt = now,
            AverageFallbackEnabled = true, CurrentRows = 2, AverageRows = 1,
            Uses24HourAverage = true, AverageWindowStart = now.AddHours(-24), AverageWindowEnd = now, AverageSamples = 72,
        };
        var item = MonsterLootItemCatalog.Default;
        var notice = new RareDropNotice("example-notice", item.Name(210230006, nameLanguage ?? language), 1,
            item.Rarity(210230006), MonsterLootIconCatalog.Default.ItemAsset(210230006), now)
            { ElapsedSeconds = 1, SessionId = session.Id, ProfileId = profile, LootKey = "Item:210230006" };
        return Project(state, market, notice, now, localZone, language, nameLanguage, ExampleAbyss(now, profile))
            with { GenusInsight = GenusInsightOverlayExamples.Create(now, profile) };
    }

    internal static AbyssRankOverlaySnapshot ProjectAbyss(AbyssRankOverlaySnapshot? snapshot)
    {
        // The existing source alone qualifies world/profile/binding continuity.
        // Cached points and frozen feed rows cannot bypass a hidden native context.
        if (snapshot is not { WorldContextVisible: true, CharacterBound: true, BindingGeneration: > 0 }
            || !Guid.TryParse(snapshot.ProfileId, out var profile) || profile == Guid.Empty)
            return AbyssRankOverlaySnapshot.Empty;
        return snapshot with
        {
            KillFeed = Array.AsReadOnly(AbyssRankOverlayRenderer.KillFeedEntries(snapshot).ToArray()),
        };
    }

    private static AbyssRankOverlaySnapshot ExampleAbyss(DateTimeOffset now, string profile) =>
        new(999_572, CharacterBound: true, Confirmed: true)
        {
            WorldContextVisible = true, ProfileId = profile, BindingGeneration = 1,
            RankPointsObservedAt = now.AddSeconds(-1), RankObservationSequence = 6,
            RankTrackingActive = true, RankContinuityRevision = 1,
            SessionId = "overlay-editor-example-abyss", SessionActive = true,
            SessionNetRankPoints = 1_220, SessionRankPointsPerHour = 1_220 / TimeSpan.FromMinutes(42.5).TotalHours,
            KillFeedEnabled = true, ShowKills = true, ShowDeaths = true, ShowAssists = true,
            KillFeed =
            [
                new(now.AddSeconds(-2), AbyssKillFeedKind.Kill, "Example opponent 1", 320)
                    { Generation = 1, Sequence = 6, AbyssPointGain = 120, OpponentClassId = "Gladiator", OpponentAbyssRankGrade = 1 },
                new(now.AddSeconds(-12), AbyssKillFeedKind.Death, "Example opponent 2", -90)
                    { Generation = 1, Sequence = 5, OpponentClassId = "Sorcerer", OpponentAbyssRankGrade = 5 },
                new(now.AddSeconds(-22), AbyssKillFeedKind.Assist, "Example opponent 3", 15)
                    { Generation = 1, Sequence = 4, AbyssPointGain = 8, IsEstimated = true, OpponentClassId = "Assassin", OpponentAbyssRankGrade = 9 },
                new(now.AddSeconds(-32), AbyssKillFeedKind.Kill, "Example opponent 4", 120)
                    { Generation = 1, Sequence = 3, AbyssPointGain = 45, OpponentClassId = "Ranger", OpponentAbyssRankGrade = 13 },
                new(now.AddSeconds(-42), AbyssKillFeedKind.Death, "Example opponent 5", -40)
                    { Generation = 1, Sequence = 2, OpponentClassId = "Cleric", OpponentAbyssRankGrade = 17 },
                new(now.AddSeconds(-52), AbyssKillFeedKind.Assist, "Example opponent 6", 25)
                    { Generation = 1, Sequence = 1, AbyssPointGain = 10, IsEstimated = true, OpponentClassId = "Chanter", OpponentAbyssRankGrade = 19 },
            ],
        };

    private static IReadOnlyList<OverlayLootItem> Drops(LootTrackerSession? session, string language, CultureInfo culture)
    {
        if (session is null || session.Loot.IsDefaultOrEmpty) return [];
        return Array.AsReadOnly(LootTrackerPresentation.OrderDrops(session.Loot.Where(item => item.Quantity > 0),
            item => MonsterLootItemCatalog.Default.DropRarity(item.Id), item => item.Quantity,
            item => Name(item.Id, item.Name, language), item => item.Id).Select(item =>
            {
                var name = Name(item.Id, item.Name, language);
                var rarity = MonsterLootItemCatalog.Default.DropRarity(item.Id);
                var quantity = item.Quantity.ToString("N0", culture);
                return new OverlayLootItem(item.Id, name, quantity, MonsterLootIconCatalog.Default.DropAsset(item.Id),
                    MonsterLootItemCatalog.IsExceptional(rarity), item.Quantity, name + " ×" + quantity, rarity);
            }).ToArray());
    }

    internal static IReadOnlyList<OverlayLootItem> Catalog(string language) => Catalogs.GetOrAdd(language == "de" ? "de" : "en", selected =>
    {
        var items = new List<OverlayLootItem>();
        var catalog = MonsterLootItemCatalog.Default;
        void Add(string key, string name)
        {
            var rarity = catalog.DropRarity(key);
            items.Add(new(key, name, "", MonsterLootIconCatalog.Default.DropAsset(key),
                MonsterLootItemCatalog.IsExceptional(rarity), Tooltip: name + " · " + key, Rarity: rarity));
        }
        foreach (var id in ItemIds.Value.Order()) Add("Item:" + id.ToString(CultureInfo.InvariantCulture), catalog.Name(id, selected));
        for (var index = 0; index < CrystalItemIds.Length; index++) Add("SoulCrystal:" + (index + 1), catalog.Name(CrystalItemIds[index], selected));
        Add("UniversalSoulCrystal:" + Network.NetworkFarmDrop.UniversalSoulCrystalItemId,
            catalog.Name(Network.NetworkFarmDrop.UniversalSoulCrystalItemId, selected));
        foreach (var pet in PetIdTable.All.Values.OrderBy(pet => pet.Id)) Add("PetSoul:" + pet.Id, pet.En);
        return Array.AsReadOnly(items.ToArray());
    });

    private static string Name(string key, string fallback, string language)
    {
        if (MonsterLootItemCatalog.TryItemId(key, out var id) && ItemIds.Value.Contains(id)) return MonsterLootItemCatalog.Default.Name(id, language);
        var parts = key.Split(':', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            if (parts[0] == "SoulCrystal" && value is >= 1 and <= 5) return MonsterLootItemCatalog.Default.Name(CrystalItemIds[value - 1], language);
            if (parts[0] == "UniversalSoulCrystal" && ItemIds.Value.Contains(value)) return MonsterLootItemCatalog.Default.Name(value, language);
            if (parts[0] == "PetSoul" && PetIdTable.Find(value) is { } pet) return pet.En;
        }
        return string.IsNullOrWhiteSpace(fallback) ? key : fallback;
    }

    private static HashSet<int> ReadItemIds()
    {
        using var stream = typeof(MonsterLootItemCatalog).Assembly.GetManifestResourceStream("Soulcrest.LootItems.json")
            ?? throw new InvalidDataException("Missing embedded loot item labels.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToHashSet();
    }

    private static long? Nonnegative(long? value) => value is >= 0 ? value : null;
    private static string Amount(long? value, CultureInfo culture, bool signed = false) => value is null ? "—"
        : (signed && value > 0 ? "+" : "") + value.Value.ToString("N0", culture);
    private static string RateAmount(double? value, CultureInfo culture, bool signed) => value is null ? "—"
        : (signed && value > 0 ? "+" : "") + value.Value.ToString("N0", culture);
    private static string Duration(TimeSpan elapsed) => string.Create(CultureInfo.InvariantCulture,
        $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

    private static string HistoricalAge(DateTimeOffset lastSeen, DateTimeOffset now, CultureInfo culture)
    {
        var age = now > lastSeen ? now - lastSeen : TimeSpan.Zero;
        return age.TotalMinutes < 1 ? "<1 min" : age.TotalHours < 1
            ? ((long)age.TotalMinutes).ToString("N0", culture) + " min"
            : ((long)age.TotalHours).ToString("N0", culture) + " h";
    }

    private static string MarketDetail(OverlayEditorMarketSnapshot market, CultureInfo culture, Func<string, string> text)
    {
        var coverage = market.PricedRows.ToString("N0", culture) + "/" + market.EligibleRows.ToString("N0", culture) + " " + text("Itemarten bewertet");
        if (market.NetKina is null && market.PartialNetKina is { } partial)
            coverage = text("Bekannter Teilwert") + ": " + partial.ToString("N0", culture) + " Kina · " + coverage;
        if (!market.AverageFallbackEnabled && !market.Uses24HourAverage && market.HistoricalRows > 0)
            coverage = text("Aktuell") + ": " + market.PricedRows.ToString("N0", culture) + " · " + text("Historisch") + ": "
                + market.HistoricalRows.ToString("N0", culture) + " · " + text("Weiterhin unbekannt") + ": "
                + market.UnvaluedRows.ToString("N0", culture);
        if (market.HasEquipmentReference) coverage += " · " + text("Ausrüstung: Basispreis-Richtwert");
        if (market.Stale) coverage += " · " + text("Gespeicherte Preise");
        if (!market.OnlineEnabled) coverage += " · " + text("Online-Marktpreise ausgeschaltet");
        return coverage;
    }

    private static string MarketTooltip(OverlayEditorMarketSnapshot market, CultureInfo culture, Func<string, string> text)
    {
        var unknown = text("Unbekannt");
        string At(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? unknown;
        var model = market.MarketProfile;
        var server = market.ServerName ?? model.ServerId?.ToString(CultureInfo.InvariantCulture) ?? unknown;
        var historical = !market.AverageFallbackEnabled && !market.Uses24HourAverage && market.HistoricalRows > 0;
        return string.Join("\n", new[]
        {
            text("Quelle") + ": aion2.exchange",
            market.AverageFallbackEnabled ? text("Aktuelle Preise; 24h-Durchschnitt nur bei fehlendem Preis.") : null,
            market.AverageFallbackEnabled ? text("Aktuelle Preiszeilen") + ": " + market.CurrentRows.ToString("N0", culture)
                + " · " + text("24h-Ersatzwerte") + ": " + market.AverageRows.ToString("N0", culture) : null,
            market.Uses24HourAverage ? text("24h-Durchschnitt niedrigster Stückpreise") : null,
            market.Uses24HourAverage ? text("Durchschnittsfenster") + ": " + At(market.AverageWindowStart) + " – " + At(market.AverageWindowEnd) : null,
            market.Uses24HourAverage ? text("Preisbeobachtungen") + ": "
                + (market.AverageSamples >= 0 ? market.AverageSamples.ToString("N0", culture) : unknown) : null,
            text("Region") + ": " + (model.Region ?? unknown) + " · " + text("Fraktion") + ": " + (model.Faction ?? unknown) + " · " + text("Server") + ": " + server,
            text("Steuer") + ": " + model.SalesTaxPercent.ToString("0.##", culture) + " % · " + text("Einstellgebühr") + ": " + model.RegistrationFeePercent.ToString("0.##", culture) + " %",
            market.Uses24HourAverage ? null : text("Ältester Preis") + ": " + At(market.OldestObservedAt) + " · "
                + text("Neuester Preis") + ": " + At(market.NewestObservedAt),
            historical
                ? text("Aktuelle Menge") + ": " + market.PricedQuantity.ToString("N0", culture) + " · "
                    + text("Historische Menge") + ": " + market.HistoricalQuantity.ToString("N0", culture) + " · "
                    + text("Weiterhin unbekannte Menge") + ": " + market.UnvaluedQuantity.ToString("N0", culture)
                : text("Mengenabdeckung") + ": " + market.PricedQuantity.ToString("N0", culture) + "/" + market.EligibleQuantity.ToString("N0", culture) + " · " + text("Unbekannte Menge") + ": " + market.UnknownQuantity.ToString("N0", culture),
            MarketDetail(market, culture, text),
            historical ? text("Historische Ersatzpreise") + ": "
                + text("Ältester historischer Preis") + ": " + At(market.OldestHistoricalSeenAt) + " · "
                + text("Neuester historischer Preis") + ": " + At(market.NewestHistoricalSeenAt) : null,
            text("Die Handelbarkeit einzelner Instanzen ist nicht bestätigt."),
            text("Ausrüstung nutzt den Basispreis ohne individuelle Werte."),
            text("Angebotspreise sind Richtwerte, kein garantierter Verkaufserlös."),
        }.Where(line => line is not null));
    }

    internal static IReadOnlyDictionary<string, string> English { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
    {
        ["Erhaltene Kina"] = "Dropped Kina", ["Verdiente AP"] = "Earned AP", ["Rangpunkte netto"] = "Net rank points",
        ["Gewonnene Rangpunkte"] = "Rank points gained", ["Verlorene Rangpunkte"] = "Rank points lost",
        ["Erhaltene Kina / Stunde"] = "Dropped Kina / hour", ["AP / Stunde"] = "AP / hour",
        ["Rangpunkte netto / Stunde"] = "Net rank points / hour", ["Gewonnene Rangpunkte / Stunde"] = "Rank points gained / hour",
        ["Verlorene Rangpunkte / Stunde"] = "Rank points lost / hour", ["Aktueller Marktwert netto"] = "Current net market value",
        ["Marktwert netto"] = "Net market value",
        ["24h-Durchschnitt niedrigster Stückpreise"] = "24h average of lowest unit prices",
        ["Aktuelle Preise; 24h-Durchschnitt nur bei fehlendem Preis."] = "Current prices; 24h averages only when a price is missing.",
        ["Aktuelle Preiszeilen"] = "Current price rows", ["24h-Ersatzwerte"] = "24h fallback rows",
        ["Durchschnittsfenster"] = "Average window", ["Preisbeobachtungen"] = "Price observations",
        ["Marktwert netto / Stunde"] = "Net market value / hour", ["Marktwert unbekannt"] = "Market value unknown",
        ["Sessiondauer"] = "Session duration", ["Gemeinsame Sessionzeit"] = "Shared session time",
        ["Keine laufende Session"] = "No active session", ["Aktuelle Session"] = "Current session", ["pro Stunde"] = "per hour",
        ["Lokale Uhrzeit"] = "Local time", ["Tages-Kina"] = "Daily Kina", ["Tagesstand unbekannt"] = "Daily total unknown",
        ["Gespeicherter Tagesstand"] = "Saved daily total", ["Tageslimit erreicht"] = "Daily limit reached", ["Bestätigter Tagesstand"] = "Confirmed daily total",
        ["Drop-Raster"] = "Drop grid", ["Drop-Leiste"] = "Drop strip", ["Drop-Liste"] = "Drop list", ["Aktuelle Sessionbeute"] = "Current session loot",
        ["Aktueller Fundhinweis"] = "Current drop notice", ["Kein aktueller Fundhinweis"] = "No current drop notice",
        ["Itemarten bewertet"] = "item types priced", ["Bekannter Teilwert"] = "Known partial value",
        ["Ausrüstung: Basispreis-Richtwert"] = "Equipment: base-price estimate", ["Gespeicherte Preise"] = "Cached prices",
        ["Online-Marktpreise ausgeschaltet"] = "Online market prices disabled",
        ["Unbekannt"] = "Unknown", ["Quelle"] = "Source", ["Region"] = "Region", ["Fraktion"] = "Faction", ["Server"] = "Server",
        ["Steuer"] = "Sales tax", ["Einstellgebühr"] = "Registration fee", ["Ältester Preis"] = "Oldest price", ["Neuester Preis"] = "Newest price",
        ["Mengenabdeckung"] = "Quantity coverage", ["Unbekannte Menge"] = "Unknown quantity",
        ["Die Handelbarkeit einzelner Instanzen ist nicht bestätigt."] = "Individual item tradability is not confirmed.",
        ["Ausrüstung nutzt den Basispreis ohne individuelle Werte."] = "Equipment uses the base price without individual rolls.",
        ["Angebotspreise sind Richtwerte, kein garantierter Verkaufserlös."] = "Asking prices are estimates, not guaranteed sale proceeds.",
        ["Aktuell"] = "Current", ["Historisch"] = "Historical", ["Weiterhin unbekannt"] = "Still unknown",
        ["Historische Ersatzpreise"] = "Historical fallback prices", ["Ältester historischer Preis"] = "Oldest historical price",
        ["Neuester historischer Preis"] = "Newest historical price",
        ["Aktuelle Menge"] = "Current quantity", ["Historische Menge"] = "Historical quantity",
        ["Weiterhin unbekannte Menge"] = "Still unknown quantity",
    });
}
