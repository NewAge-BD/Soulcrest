using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Anonymous projections of existing host state. The supplied provider must only return already
/// constructed instances (DiagnosticServiceRegistry); diagnosis must never resolve the live DI container.
/// No contributor starts a source, refreshes a store or serializes a production snapshot.
/// </summary>
public static class DiagnosticContributors
{
    internal static DiagnosticResult CraftingStockState(CraftingStockSnapshot snapshot, DateTimeOffset now)
    {
        ImmutableArray<NetworkStockScope> scopes = snapshot.Scopes.IsDefault ? [] : snapshot.Scopes;
        var status = snapshot.StorageError is not null ? DiagnosticStatus.Error
            : !snapshot.Available ? DiagnosticStatus.Waiting
            : snapshot.IsFresh && snapshot.IsComplete ? DiagnosticStatus.Healthy : DiagnosticStatus.Warning;
        return AnonymousState("crafting-stock", status, now,
            ("profileBound", snapshot.Available), ("fresh", snapshot.IsFresh), ("complete", snapshot.IsComplete),
            ("effectiveItemKinds", snapshot.EffectiveItems.Count), ("observedItemKinds", snapshot.ObservedItems.Count),
            ("observedMinimumItemKinds", snapshot.ObservedMinimumItems.Count), ("manualItemKinds", snapshot.ManualItems.Count),
            ("manualOverrideItemKinds", snapshot.EffectiveItems.Count(row => row.Value.Origin == StockOrigin.ManualOverride)),
            ("legacyManualItemKinds", snapshot.EffectiveItems.Count(row => row.Value.Origin == StockOrigin.LegacyManual)),
            ("freshItemKinds", snapshot.EffectiveItems.Count(row => row.Value.Freshness == StockFreshness.Fresh)),
            ("historicalItemKinds", snapshot.EffectiveItems.Count(row => row.Value.Freshness == StockFreshness.Historical)),
            ("scopeCount", scopes.Length), ("knownScopeCount", scopes.Count(scope => scope.Completeness is StockCompleteness.Partial or StockCompleteness.Complete)),
            ("completeScopeCount", scopes.Count(scope => scope.Completeness == StockCompleteness.Complete)),
            ("bagScopeKnown", scopes.Any(scope => scope.Scope == StockScope.Bag && scope.Completeness is StockCompleteness.Partial or StockCompleteness.Complete)),
            ("characterStorageScopeKnown", scopes.Any(scope => scope.Scope == StockScope.CharacterStorage && scope.Completeness is StockCompleteness.Partial or StockCompleteness.Complete)),
            ("serverStorageScopeKnown", scopes.Any(scope => scope.Scope == StockScope.ServerStorage && scope.Completeness is StockCompleteness.Partial or StockCompleteness.Complete)),
            ("storageError", snapshot.StorageError is not null));
    }

    internal static DiagnosticResult ResourceMapLinksState(ResourceMapSources sources, AppSettings settings,
        bool serviceAvailable, bool dialogOpen, DateTimeOffset now)
    {
        var defaults = settings.ResourceMapDefaultSelection;
        return AnonymousState("resource-map-links", sources.UnavailableMaps.Length > 0 ? DiagnosticStatus.Warning
            : sources.ItemCount == 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy, now,
            ("serviceAvailable", serviceAvailable), ("sourceItemKinds", sources.ItemCount),
            ("unavailableMapCount", sources.UnavailableMaps.Length),
            ("defaultWorldmap", defaults.Worldmap), ("defaultMinimap", defaults.Minimap), ("defaultSoulcrest", defaults.Soulcrest),
            ("dontShowAgain", settings.ResourceMapDontShowAgain), ("targetedItemKinds", settings.TargetedResourceMapItems.Length),
            ("dialogOpen", dialogOpen));
    }

    internal static DiagnosticResult CraftingProgressOverlayState(bool enabled, DiagnosticOverlayRuntimeState? runtime,
        CraftingProgressSnapshot? snapshot, bool stockAvailable, bool committedLootAvailable, bool networkStockAvailable,
        DateTimeOffset now, IReadOnlyList<(string Key, object? Value)>? contextFacts = null)
    {
        var age = runtime is null ? (double?)null : (now - runtime.ObservedAt).TotalMilliseconds;
        var status = !enabled ? DiagnosticStatus.Disabled
            : runtime?.Disposed == true || snapshot?.Error is not null ? DiagnosticStatus.Error
            : runtime is null || snapshot is null ? DiagnosticStatus.Waiting
            : age is < 0 or > 5000 ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy;
        var rows = snapshot?.Rows ?? [];
        var facts = contextFacts?.ToList() ?? [];
        void Set(string key, object? value) { facts.RemoveAll(row => row.Key == key); facts.Add((key, value)); }
        Set("enabled", enabled); Set("nativeVisibilityKnown", runtime is not null);
        Set("nativeVisible", runtime?.Visible); Set("handleCreated", runtime?.HandleCreated);
        Set("disposed", runtime?.Disposed); Set("runtimeAgeMs", age);
        Set("progressServiceAvailable", snapshot is not null); Set("goalPinned", snapshot?.Pinned is not null);
        Set("targetQuantity", snapshot?.Pinned?.TargetQuantity); Set("confirmedQuantity", snapshot?.Pinned?.ConfirmedQuantity);
        Set("rowCount", snapshot is null ? null : rows.Length); Set("planItemCount", snapshot?.Pinned?.PlanItems.Length);
        Set("planFresh", snapshot?.PlanIsFresh); Set("planBusy", snapshot?.PlanBusy);
        Set("fresh", snapshot?.IsFresh); Set("historical", snapshot?.IsHistorical);
        Set("hasContent", snapshot?.HasContent); Set("error", snapshot?.Error is not null);
        Set("ignoredReceiptCount", snapshot?.IgnoredReceipts);
        Set("stockSourceAvailable", stockAvailable); Set("committedLootSourceAvailable", committedLootAvailable);
        Set("networkStockSourceAvailable", networkStockAvailable);
        Set("availableSourceCount", (stockAvailable ? 1 : 0) + (committedLootAvailable ? 1 : 0) + (networkStockAvailable ? 1 : 0));
        foreach (var source in Enum.GetValues<CraftingProgressSource>())
            Set(char.ToLowerInvariant(source.ToString()[0]) + source.ToString()[1..] + "SourceRows",
                snapshot is null ? null : rows.Count(row => row.SourceQuantities.GetValueOrDefault(source) > 0));
        return AnonymousState("overlay-crafting-progress", status, now, facts.ToArray());
    }

    private static DiagnosticResult AnonymousState(string id, DiagnosticStatus status, DateTimeOffset now,
        params (string Key, object? Value)[] facts) => new(id, status, now.ToUniversalTime(), status switch
        {
            DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
            DiagnosticStatus.Warning => "Unvollständige oder veraltete Daten; Details prüfen.",
            DiagnosticStatus.Error => "Ein Fehler wurde erkannt; Details prüfen.",
            DiagnosticStatus.Disabled => "Diese Funktion ist ausgeschaltet.",
            _ => "Die Funktion wartet auf passende Daten."
        }, new ReadOnlyDictionary<string, object?>(facts.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal)));

    internal static DiagnosticResult GenusInsightOverlaySnapshot(bool enabled, DiagnosticOverlayRuntimeState? runtime,
        GenusInsightSnapshot? source, GenusMenuSnapshot? menu, DateTimeOffset now,
        IReadOnlyList<(string Key, object? Value)>? contextFacts = null)
    {
        var age = runtime is null ? (double?)null : (now - runtime.ObservedAt).TotalMilliseconds;
        var categoryKnown = GenusInsightPresentation.KnownGenus(menu?.Genus);
        var presetKnown = menu?.ViewedPreset is >= 1 and <= 3;
        var current = source is { IsCurrent: true, IsHistorical: false, Generation: > 0 }
            && !string.IsNullOrWhiteSpace(source.ProfileId);
        var board = categoryKnown ? source?.Boards.FirstOrDefault(item => item.Genus == menu!.Genus) : null;
        var confirmed = board?.Slots.Count(slot => slot.IsCurrent) ?? 0;
        var allowed = current && menu is { MenuVisible: true, SourceGeneration: > 0 } && categoryKnown && presetKnown;
        var status = !enabled ? DiagnosticStatus.Disabled : runtime?.Disposed == true ? DiagnosticStatus.Error
            : runtime is null ? DiagnosticStatus.Waiting : age is < 0 or > 5000 ? DiagnosticStatus.Warning
            : !allowed || !runtime.Visible || !runtime.HandleCreated ? DiagnosticStatus.Waiting
            : board?.Slots.Count != 9 || confirmed != 9 ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy;
        var facts = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in contextFacts ?? []) facts[key] = value;
        facts["enabled"] = enabled;
        facts["nativeVisibilityKnown"] = runtime is not null;
        facts["nativeVisible"] = runtime?.Visible;
        facts["handleCreated"] = runtime?.HandleCreated;
        facts["disposed"] = runtime?.Disposed;
        facts["runtimeObservedAtUtc"] = runtime?.ObservedAt.ToUniversalTime();
        facts["runtimeAgeMs"] = age;
        facts["sourceCurrent"] = current;
        facts["historical"] = source?.IsHistorical;
        facts["menuVisible"] = menu?.MenuVisible;
        facts["categoryKnown"] = categoryKnown;
        facts["viewedPresetKnown"] = presetKnown;
        facts["liveDisplayAllowed"] = allowed;
        facts["slots"] = board?.Slots.Count;
        facts["confirmedSlots"] = confirmed;
        return new("overlay-genus-insight", status, now.ToUniversalTime(), status switch
        {
            DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
            DiagnosticStatus.Warning => "Unvollständige oder veraltete Daten; Details prüfen.",
            DiagnosticStatus.Error => "Ein Fehler wurde erkannt; Details prüfen.",
            DiagnosticStatus.Disabled => "Diese Funktion ist ausgeschaltet.",
            _ => "Die Funktion wartet auf passende Daten."
        }, new ReadOnlyDictionary<string, object?>(facts));
    }

    internal static DiagnosticResult MerchantOverlaySnapshot(bool enabled, DiagnosticOverlayRuntimeState? runtime,
        MerchantOverlayHealthSnapshot health, DateTimeOffset now,
        IReadOnlyList<(string Key, object? Value)>? contextFacts = null)
    {
        var status = MerchantOverlayStatus(enabled, runtime, health, now);
        var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = enabled, ["nativeVisibilityKnown"] = runtime is not null,
            ["nativeVisible"] = runtime?.Visible, ["handleCreated"] = runtime?.HandleCreated,
            ["disposed"] = runtime?.Disposed, ["runtimeObservedAtUtc"] = runtime?.ObservedAt.ToUniversalTime(),
            ["runtimeAgeMs"] = runtime is null ? null : (now - runtime.ObservedAt).TotalMilliseconds,
            ["serviceAvailable"] = null, ["hasOfferSnapshot"] = null, ["offers"] = null, ["petRowCount"] = null
        };
        foreach (var (key, value) in contextFacts ?? []) facts[key] = value;
        foreach (var (key, value) in MerchantOverlayFacts(health)) facts[key] = value;
        if (status == DiagnosticStatus.Healthy && (facts["serviceAvailable"] is not true || facts["hasOfferSnapshot"] is not true
            || facts["offers"] is not 10 || facts["petRowCount"] is not int or <= 0
            || health.PetRows == 0 || health.BadgeCount == 0)) status = DiagnosticStatus.Waiting;
        return new("overlay-merchant", status, now.ToUniversalTime(), status switch
        {
            DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
            DiagnosticStatus.Warning => "Unvollständige oder veraltete Daten; Details prüfen.",
            DiagnosticStatus.Error => "Ein Fehler wurde erkannt; Details prüfen.",
            DiagnosticStatus.Disabled => "Diese Funktion ist ausgeschaltet.",
            _ => "Die Funktion wartet auf passende Daten."
        }, new ReadOnlyDictionary<string, object?>(facts));
    }

    internal static DiagnosticStatus MerchantOverlayStatus(bool enabled, DiagnosticOverlayRuntimeState? runtime,
        MerchantOverlayHealthSnapshot health, DateTimeOffset now)
    {
        if (!enabled) return DiagnosticStatus.Disabled;
        if (runtime?.Disposed == true || health.WorkerState == "faulted" || health.Observation == "capture-error"
            || health.Presentation == "presentation-error") return DiagnosticStatus.Error;
        if (runtime is null) return DiagnosticStatus.Waiting;
        var runtimeAge = (now - runtime.ObservedAt).TotalMilliseconds;
        if (runtimeAge is < 0 or > 5000 || health.AnalysisAgeMilliseconds > 1000 || health.FrameAgeMilliseconds > 1000
            || health.WorkerState == "unknown" || health.Observation == "unknown" || health.Presentation == "unknown")
            return DiagnosticStatus.Warning;
        // A successful native transfer may be old while unchanged badges use new accepted frames.
        // Presentation time is informational; analysis/frame freshness and current visibility decide.
        return runtime.Visible && runtime.HandleCreated && health.WorkerState == "running"
            && !health.PlacementHidden && !health.ForegroundSuppressed && health.Presentation == "shown"
            && health.Observation is "layout-ready" or "capture-held"
            && health.AnalysisAgeMilliseconds is >= 0 and <= 1000 && health.FrameAgeMilliseconds is >= 0 and <= 1000
                ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting;
    }

    private static IEnumerable<(string Key, object? Value)> MerchantOverlayFacts(MerchantOverlayHealthSnapshot health)
    {
        yield return ("analysisReason", health.Observation);
        yield return ("presentationState", health.Presentation);
        yield return ("workerState", health.WorkerState);
        yield return ("placementHidden", health.PlacementHidden);
        yield return ("foregroundSuppressed", health.ForegroundSuppressed);
        yield return ("totalOffers", health.TotalOffers);
        yield return ("petRows", health.PetRows);
        yield return ("badgeCount", health.BadgeCount);
        yield return ("analysisObservedAtUtc", health.AnalysisObservedAtUtc);
        yield return ("presentationObservedAtUtc", health.PresentationObservedAtUtc);
        yield return ("analysisAgeMs", health.AnalysisAgeMilliseconds);
        yield return ("analysisFrameAgeMs", health.FrameAgeMilliseconds);
        yield return ("analysisFresh", health.AnalysisAgeMilliseconds is >= 0 and <= 1000);
        yield return ("analysisFrameFresh", health.FrameAgeMilliseconds is >= 0 and <= 1000);
        yield return ("layoutMisses", health.LayoutMisses);
        yield return ("captureMisses", health.CaptureMisses);
        yield return ("heldFrames", health.Held);
        yield return ("renders", health.Renders);
        yield return ("errors", health.Errors);
        yield return ("transitionCount", health.Transitions.Count);
        for (var index = 0; index < health.Transitions.Count; index++)
        {
            yield return ($"transition{index}AtUtc", health.Transitions[index].At);
            yield return ($"transition{index}Reason", health.Transitions[index].Reason);
        }
    }

    internal static DiagnosticResult LootSessionSnapshot(LootTrackerSnapshot snapshot, bool sourceAvailable,
        bool statusError, long? itemThreshold, long automaticallyDeleted, bool cleanupIncomplete, DateTimeOffset now,
        bool? trackingEnabled, bool? waitingForWorldContext = null, LootRecordingFaultSnapshot? recordingFault = null)
    {
        var sessions = snapshot.Sessions;
        var age = snapshot.At is { } observed ? now - observed : (TimeSpan?)null;
        var fresh = age is { } elapsed ? elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(5) : (bool?)null;
        var status = statusError || cleanupIncomplete || recordingFault is not null ? DiagnosticStatus.Error
            : trackingEnabled == false ? DiagnosticStatus.Disabled
            : trackingEnabled is null || !sourceAvailable || fresh != true || itemThreshold is null
                ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy;
        return new("loot-sessions", status,
            now.ToUniversalTime(), status == DiagnosticStatus.Error ? "Ein Fehler wurde erkannt; Details prüfen."
                : status == DiagnosticStatus.Disabled ? "Loot-Aufzeichnung ist ausgeschaltet."
                : status == DiagnosticStatus.Waiting ? "Warte auf einen bestätigten eigenen Weltkontext."
                : "Zustand verfügbar, keine Auffälligkeit erkannt.",
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
            {
                ["sessions"] = sessions.Length,
                ["liveSessions"] = sessions.Count(session => session.Status == LootTrackerSessionStatus.Live),
                ["pausedSessions"] = sessions.Count(session => session.Status == LootTrackerSessionStatus.Live && session.IsPaused),
                ["interruptedSessions"] = sessions.Count(session => session.Status == LootTrackerSessionStatus.Interrupted),
                ["sourceAvailable"] = sourceAvailable,
                ["trackingEnabled"] = trackingEnabled,
                ["waitingForWorldContext"] = waitingForWorldContext,
                ["snapshotFresh"] = fresh,
                ["snapshotAgeMilliseconds"] = age?.Ticks / TimeSpan.TicksPerMillisecond,
                ["idlePauseSeconds"] = LootTrackerPresentation.IdlePauseSeconds,
                ["idleCloseSeconds"] = LootTrackerPresentation.IdleCloseSeconds,
                ["smallSessionItemThreshold"] = itemThreshold is { } threshold ? LootTrackerSessionSize.NormalizeItemThreshold(threshold) : null,
                ["automaticSmallSessionDeletionEnabled"] = itemThreshold is { } configured ? configured > 0 : null,
                ["automaticallyDeletedSmallSessions"] = Math.Max(0, automaticallyDeleted),
                ["smallSessionCleanupIncomplete"] = cleanupIncomplete,
                ["abyssSessionsExcludedFromIdleAndAutomaticDeletion"] = true,
                ["observedAtUtc"] = snapshot.At?.ToUniversalTime(),
                ["recordingFaultPresent"] = recordingFault is not null,
                ["recordingFaultAtUtc"] = recordingFault?.At.ToUniversalTime(),
                ["recordingFaultStage"] = recordingFault?.Stage.ToString(),
                ["recordingFaultKind"] = recordingFault?.Kind.ToString(),
                ["recordingFaultExceptionType"] = recordingFault?.ExceptionType,
                ["recordingFaultHResult"] = recordingFault?.HResult
            }));
    }

    internal static DiagnosticResult CaptureSnapshot(GameCaptureService.CaptureDiagnostics capture,
        bool? mapTrackingEnabled, bool? borderSuppressed, DateTimeOffset now, bool? borderSuppressionSupported = null,
        string? borderPermission = null)
    {
        var stage = DiagnosticSupportProbe.CaptureStage(capture.Stage);
        var method = DiagnosticSupportProbe.CaptureMethod(capture.Method);
        var color = DiagnosticSupportProbe.CaptureColor(capture);
        DateTimeOffset? since = capture.StageSince == default ? null : new DateTimeOffset(capture.StageSince.ToUniversalTime());
        var elapsed = since is { } started ? Math.Max(0, (now - started).TotalMilliseconds) : (double?)null;
        var stalled = stage != "ready" && elapsed > 5000;
        DateTimeOffset? lastAt = capture.LastFrameAt is { } last ? new DateTimeOffset(last.ToUniversalTime()) : null;
        var frameAge = lastAt is { } at ? (now - at).TotalMilliseconds : (double?)null;
        var fresh = frameAge is >= 0 and < 5000;
        var status = mapTrackingEnabled == false && capture.Grabs == 0 ? DiagnosticStatus.Disabled
            : stalled || capture.LastProblem is not null || stage == "unknown" || method == "unknown" ? DiagnosticStatus.Warning
            : fresh ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting;
        // Only implementation-owned permission values may reach an exported report.
        var permission = borderPermission is "unsupported" or "DeniedBySystem" or "NotDeclaredByApp" or "DeniedByUser"
            or "UserPromptRequired" or "Allowed" ? borderPermission : null;
        return new("capture", status, now.ToUniversalTime(), stalled
            ? "Die Bildaufnahme wartet länger als fünf Sekunden auf die Verarbeitung."
            : status switch
            {
                DiagnosticStatus.Warning => "Unvollständige oder veraltete Daten; Details prüfen.",
                DiagnosticStatus.Disabled => "Diese Funktion ist ausgeschaltet.",
                DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
                _ => "Die Funktion wartet auf passende Daten."
            }, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
            {
                ["method"] = method, ["windowCapture"] = method == "wgc-window", ["stage"] = stage,
                ["stageSinceUtc"] = since, ["stageElapsedMs"] = elapsed, ["stageStalled"] = stalled,
                ["framesArrived"] = capture.FramesArrived, ["lastFrameAtUtc"] = lastAt, ["fresh"] = fresh,
                ["grabs"] = capture.Grabs, ["gdiFallbacks"] = capture.GdiFallbacks, ["busyFrames"] = capture.BusyFrames,
                ["problemReported"] = capture.LastProblem is not null, ["regionConfigured"] = capture.Bounds is not null,
                ["captureWidth"] = capture.Bounds?.Width, ["captureHeight"] = capture.Bounds?.Height,
                ["colorMode"] = color.Mode, ["sdrWhiteNits"] = color.SdrWhiteNits,
                ["borderSuppressed"] = borderSuppressed, ["borderSuppressionSupported"] = borderSuppressionSupported,
                ["borderPermission"] = permission
            }));
    }

    public static IReadOnlyList<IDiagnosticContributor> Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return DiagnosticFeatureCatalog.All.Select(feature => feature.Group).Distinct(StringComparer.Ordinal)
            .Select(group => (IDiagnosticContributor)new GroupContributor(services, group)).ToArray();
    }

    private sealed class GroupContributor(IServiceProvider services, string group) : IDiagnosticContributor
    {
        public IReadOnlySet<string> FeatureIds { get; } = DiagnosticFeatureCatalog.All
            .Where(feature => feature.Group == group).Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);

        public DiagnosticResult Collect(string featureId)
        {
            if (!FeatureIds.Contains(featureId)) throw new ArgumentException("Unknown diagnostic feature.", nameof(featureId));
            return new Reader(services, featureId).Collect();
        }
    }

    private sealed class Reader(IServiceProvider services, string id)
    {
        private T? Existing<T>() where T : class => services.GetService(typeof(T)) as T;
        private AppSettings? Settings => Existing<SettingsService>()?.Current;

        private DiagnosticResult Result(DiagnosticStatus status, params (string Key, object? Value)[] facts) =>
            new(id, status, DateTimeOffset.UtcNow, status switch
            {
                DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
                DiagnosticStatus.Warning => "Unvollständige oder veraltete Daten; Details prüfen.",
                DiagnosticStatus.Error => "Ein Fehler wurde erkannt; Details prüfen.",
                DiagnosticStatus.Disabled => "Diese Funktion ist ausgeschaltet.",
                DiagnosticStatus.Waiting => "Die Funktion wartet auf passende Daten.",
                _ => "Für diese Funktion ist noch kein laufender Dienst verfügbar."
            }, new ReadOnlyDictionary<string, object?>(facts.ToDictionary(fact => fact.Key,
                fact => fact.Value is double number && !double.IsFinite(number) ? null : fact.Value, StringComparer.Ordinal)));

        private DiagnosticResult Missing() => Result(DiagnosticStatus.Unavailable, ("serviceAvailable", false));
        private static string EnumValue<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
        private static string Language(string? value) => value is "en" or "de" or "auto" or "" ? value : "other";
        private static DiagnosticStatus DomainStatus<T>(NetworkCombatDomain<T> domain) where T : class => domain.Status switch
        {
            NetworkCombatSourceStatus.Conflict => DiagnosticStatus.Error,
            NetworkCombatSourceStatus.Stale or NetworkCombatSourceStatus.Partial => DiagnosticStatus.Warning,
            NetworkCombatSourceStatus.Complete when domain.IsFresh && domain.Value is not null => DiagnosticStatus.Healthy,
            _ => DiagnosticStatus.Waiting
        };
        private DiagnosticResult Domain<T>(NetworkCombatDomain<T> domain, params (string, object?)[] facts) where T : class =>
            Result(DomainStatus(domain), [("sourceStatus", EnumValue(domain.Status)), ("fresh", domain.IsFresh),
                ("hasValue", domain.Value is not null), ("observedAtUtc", domain.ObservedAt?.ToUniversalTime()), .. facts]);

        public DiagnosticResult Collect() => id switch
        {
            "system" => System(), "settings-storage" => Storage(), "ui" => Ui(), "app-themes" => AppThemes(), "updates" => Updates(),
            "network" => Network(), "capture" => Capture(),
            "map" => Map(), "navigation" or "routes" or "map-leveling" => Navigation(),
            "pets" => Pets(), "dungeon-pets" => DungeonPets(), "exploration" => Exploration(), "merchant" => Merchant(),
            "boss-rush" => Bosses(), "alerts" => Alerts(),
            "loot-market" => Market(), "loot-replay" => Replay(), "loot-alerts" => DropAlerts(),
            "loot-killfeed" => KillFeed(), "loot-abyss" => Abyss(),
            "loot-pve" or "loot-lifeskill" or "loot-sessions" or "loot-statistics" => Loot(),
            "character" => Character(), "combat-skills" => Skills(),
            "crafting-calculator" or "crafting-dependency-map" => Crafting(),
            "item-name-colors" => ItemNameColors(),
            "combat-daevanion" => Daevanion(), "combat-genus-insight" => Genus(),
            "combat-genus-favorites" => GenusInsightFavoriteDiagnostics.Capture(Existing<GenusInsightFavoriteService>(), DateTimeOffset.UtcNow),
            "combat-achievements" => Achievements(), "combat-pantheon" => Pantheon(),
            "combat-stat-overview" or "combat-wings" or "combat-gear" or "combat-pets" or "combat-titles" => Combat(),
            "overlay-editor" => Editor(), "overlay-hotkeys" => Hotkeys(),
            _ when id.StartsWith("widget-", StringComparison.Ordinal) => Widget(),
            _ when id.StartsWith("overlay-", StringComparison.Ordinal) => Overlay(),
            _ => throw new InvalidOperationException("Diagnostic feature has no implementation.")
        };

        private DiagnosticResult AppThemes()
        {
            var settings = Existing<SettingsService>();
            if (settings is null) return Missing();
            var runtime = Existing<AppThemeRuntime>()?.Snapshot;
            var selected = AppThemeDefinition.NormalizeId(settings.Current.AppTheme);
            var status = settings.AppThemeSaveError is not null || runtime?.NativeError is not null || runtime?.WebError is not null
                ? DiagnosticStatus.Error
                : runtime is not null && runtime.RequestedId == selected && runtime.NativeAppliedId == selected && runtime.WebAppliedId == selected
                    ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting;
            return Result(status, ("selectedThemeId", selected), ("requestedThemeId", runtime?.RequestedId),
                ("nativeAppliedThemeId", runtime?.NativeAppliedId), ("webAppliedThemeId", runtime?.WebAppliedId),
                ("applicationObservedAtUtc", runtime?.AppliedAtUtc), ("saveError", settings.AppThemeSaveError),
                ("nativeError", runtime?.NativeError), ("webError", runtime?.WebError),
                ("customTitleBarColorsSupported", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)), ("mainWindowOnly", true));
        }

        private DiagnosticResult ItemNameColors()
        {
            if (Existing<SettingsService>() is null) return Missing();
            var items = ItemNamePresentation.ItemCount;
            var grades = ItemNamePresentation.GradeCount;
            return Result(items > 0 && grades == 7 ? DiagnosticStatus.Healthy : DiagnosticStatus.Warning,
                ("presentationSchema", 1), ("knownItems", items), ("knownGrades", grades),
                ("unknownItemsStayNeutral", true), ("changesBusinessRarity", false),
                ("paletteAvailable", grades == 7));
        }

        private DiagnosticResult Crafting()
        {
            var service = Existing<CraftingCalculatorService>();
            if (service is null) return Missing();
            var state = service.Snapshot;
            var proficiency = Existing<CraftingProficiencyStateService>();
            var source = proficiency?.Snapshot;
            var result = state.Result;
            var transfer = state.SelectedRecipeId is { } recipeId && service.Catalog.FindRecipe(recipeId) is { } recipe
                && Soulcrest.Core.Crafting.TransferCraftingRules.IsTransfer(service.Catalog, recipe);
            var dependencyMap = transfer ? null : CraftingDependencyMapProjection.Build(service.Catalog, state);
            var priceTradeability = service.NeededItems(state).Select(id => service.Catalog.FindItem(id)?.IsTradable).ToArray();
            var oldestPrice = state.BuyPrices.Values.Concat(state.SellPrices.Values)
                .Where(row => row.IsMarket)
                .Select(row => row.ObservedAt).Min();
            var priceAge = oldestPrice is { } pricedAt ? DateTimeOffset.UtcNow - pricedAt : (TimeSpan?)null;
            var status = state.IsError || proficiency?.StorageError is not null || state.ScenarioStorage.Error is not null ? DiagnosticStatus.Error
                : !service.Active || state.Busy || state.SelectedRecipeId is null || state.ObservedLevels.Count == 0 && state.SimulatedLevels.Count == 0
                    ? DiagnosticStatus.Waiting
                : state.PriceRefreshFailure is not null ? DiagnosticStatus.Warning
                : result?.Status == Soulcrest.Core.Crafting.CraftingPlanStatus.Complete && state.ProficiencyFresh
                    && priceAge is { } quoteAge && quoteAge >= TimeSpan.Zero && quoteAge <= ExchangeMarketCache.FetchTtl
                    && state.CalculatedAt is { } at && DateTimeOffset.UtcNow - at is { TotalSeconds: >= 0 and <= 60 } ? DiagnosticStatus.Healthy
                : DiagnosticStatus.Warning;
            // Planning may be healthy while the requested observed crafting progress still has no qualified source.
            if (id == "crafting-dependency-map" && status == DiagnosticStatus.Healthy) status = DiagnosticStatus.Warning;
            return Result(status, ("metadataBuild", service.Catalog.MetadataBuild), ("recipeCount", service.Catalog.Recipes.Length),
                ("qualifiedRecipeCount", service.Catalog.Recipes.Count(row => row.MechanicsQualified)),
                ("itemCount", service.Catalog.Items.Length), ("npcOfferCount", service.Catalog.NpcOffers.Length),
                ("profession", state.Profession), ("recipeSelected", state.SelectedRecipeId is not null),
                ("settingsFaction", string.IsNullOrWhiteSpace(state.Faction) ? null : CraftingCalculatorPresentation.Faction(state.Faction)),
                ("selectedRecipeFaction", state.SelectedRecipeId is { } factionRecipe && service.Catalog.FindRecipe(factionRecipe) is { } factionDefinition
                    ? CraftingCalculatorPresentation.Faction(factionDefinition.Faction) : null),
                ("factionCompatibleRootCount", service.Catalog.Recipes.Count(row => !service.Catalog.IsComboRecipe(row.Id)
                    && CraftingCalculatorPresentation.MatchesFaction(row.Faction, state.Faction))),
                ("calculationMode", transfer ? "transfer-costs-only" : "market-profit"),
                ("planPresentation", transfer ? "transfer-tree" : "dependency-map"),
                ("dependencyMapItems", dependencyMap?.Nodes.Length),
                ("dependencyMapEdges", dependencyMap?.Nodes.Sum(node => node.DependencyItemIds.Length)),
                ("dependencyMapTruncated", dependencyMap?.Truncated),
                ("dependencyQuantitiesKnown", dependencyMap?.QuantitiesKnown),
                ("stockCoverageSource", "manual-scenario"),
                ("stockCoveredItemKinds", dependencyMap?.Nodes.Count(node => node.IsComplete)),
                ("craftEventSourceAvailable", false), ("observedCraftLossSourceAvailable", false),
                ("transferEquipmentType", transfer ? state.Subcategory : null),
                ("transferRootCount", service.Catalog.Recipes.Count(row => !service.Catalog.IsComboRecipe(row.Id)
                    && Soulcrest.Core.Crafting.TransferCraftingRules.IsTransfer(service.Catalog, row))),
                ("transferGuaranteeSource", transfer ? Soulcrest.Core.Crafting.TransferCraftingRules.PolicyGuaranteeSource : null),
                ("ownedItemKinds", state.OwnedItems.Count),
                ("ownedEquipmentKinds", state.OwnedItems.Count(row => Soulcrest.Core.Crafting.TransferCraftingRules.IsEquipment(service.Catalog, row.Key))),
                ("targetMode", CraftingCalculatorPresentation.IsComboEquipmentTarget(service.Catalog, state.SelectedRecipeId) ? "exact-combo-output" : "starting-recipe-attempts"), ("targetAttempts", state.TargetAttempts),
                ("exactComboTarget", CraftingCalculatorPresentation.IsComboEquipmentTarget(service.Catalog, state.SelectedRecipeId)),
                ("referenceSimulation", !state.ReferenceLevels.IsEmpty), ("referenceProfessionCount", state.ReferenceLevels.Count),
                ("simulationSamples", state.SimulationSamples),
                ("requiredProfessionCount", state.RequiredProficiencyLevels.Count),
                ("scenarioStorageAvailable", state.ScenarioStorage.Available), ("scenarioStorageReadOnly", state.ScenarioStorage.IsReadOnly),
                ("scenarioStorageDirty", state.ScenarioStorage.IsDirty), ("scenarioStorageSavedAtUtc", state.ScenarioStorage.SavedAt),
                ("scenarioStorageError", state.ScenarioStorage.Error),
                ("proficiencySourceAvailable", source is not null), ("proficiencyFresh", state.ProficiencyFresh),
                ("usingSavedProficiency", state.UsingSavedProficiency), ("observedProfessionCount", state.ObservedLevels.Count),
                ("simulatedProfessionCount", state.SimulatedLevels.Count), ("proficiencyObservedAtUtc", state.ProficiencyObservedAt?.ToUniversalTime()),
                ("marketKnown", state.ServerName is not null), ("buyPriceCount", state.BuyPrices.Count),
                ("marketEligibleItemCount", priceTradeability.Count(tradable => tradable == true)),
                ("excludedNonTradableItemCount", priceTradeability.Count(tradable => tradable == false)),
                ("unknownTradeabilityItemCount", priceTradeability.Count(tradable => tradable is null)),
                ("salePriceCount", state.SellPrices.Count), ("busy", state.Busy),
                ("retainingResultDuringRefresh", state.Busy && state.Result is not null),
                ("averageFallbackPriceCount", state.BuyPrices.Values.Concat(state.SellPrices.Values)
                    .Where(row => row.Source == Soulcrest.Core.Crafting.CraftingPriceSource.ExchangeAverage).Select(row => row.ItemId).Distinct().Count()),
                ("priceRefreshFailureStage", state.PriceRefreshFailure?.Stage.ToString()),
                ("priceRefreshFailureKind", state.PriceRefreshFailure?.Kind.ToString()),
                ("priceRefreshHttpStatus", state.PriceRefreshFailure?.HttpStatus),
                ("priceRefreshRetryAtUtc", state.PriceRefreshFailure?.RetryAt?.ToUniversalTime()),
                ("calculationFailureStage", state.CalculationFailure?.Stage.ToString()),
                ("calculationFailureKind", state.CalculationFailure?.Kind.ToString()),
                ("requiredPriceCount", state.RequiredPriceCount), ("oldestPriceObservedAtUtc", oldestPrice?.ToUniversalTime()),
                ("oldestPriceAgeSeconds", priceAge?.TotalSeconds),
                ("active", service.Active), ("calculatedAtUtc", state.CalculatedAt?.ToUniversalTime()),
                ("calculationStatus", result?.Status.ToString()), ("calculationIssueCount", result?.Issues.Length),
                ("completedSimulationCount", result?.Samples), ("hasExpectedCost", result?.ExpectedCost is not null),
                ("hasP90Budget", result?.P90Cost is not null), ("hasProfit", result?.ExpectedProfit is not null),
                ("hasExactTargetOutput", result?.TargetOutput is not null),
                ("expectedOutputKinds", result?.Outputs.Length),
                ("expectedOutputQuantity", result?.Outputs.Sum(row => row.ExpectedQuantity)),
                ("hasExpectedNetRevenue", result?.ExpectedNetRevenue is not null));
        }

        private DiagnosticResult CraftingStock() => Existing<CraftingStockService>() is { } stock
            ? CraftingStockState(stock.Snapshot, DateTimeOffset.UtcNow) : Missing();

        private DiagnosticResult ResourceMapLinks()
        {
            if (Existing<ResourceMapSources>() is not { } sources || Settings is not { } settings) return Missing();
            var service = Existing<ResourceMapService>();
            return ResourceMapLinksState(sources, settings, service is not null, service?.Pending is not null, DateTimeOffset.UtcNow);
        }

        private DiagnosticResult System()
        {
            var support = DiagnosticSupportProbe.SystemFacts();
            var webViewInstalled = support.First(fact => fact.Key == "webView2RuntimeInstalled").Value as bool?;
            var monitorAvailable = support.First(fact => fact.Key == "monitorProbeAvailable").Value as bool?;
            return Result(webViewInstalled == false ? DiagnosticStatus.Error
                : webViewInstalled is null || monitorAvailable != true ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy, [
                ("windowsMajor", Environment.OSVersion.Version.Major), ("windowsBuild", Environment.OSVersion.Version.Build),
                ("process64Bit", Environment.Is64BitProcess), ("processorCount", Environment.ProcessorCount),
                ("managedMemoryBytes", GC.GetTotalMemory(false)), ("settingsAvailable", Settings is not null), .. support]);
        }

        private DiagnosticResult Storage()
        {
            if (Settings is not { } settings) return Missing();
            // Existence only: no user-file contents, absolute paths or write probes enter diagnosis.
            return Result(DiagnosticStatus.Waiting, ("dataDirectoryExists", Directory.Exists(AppPaths.DataDirectory)),
                ("settingsFileExists", File.Exists(AppPaths.SettingsFile)), ("logsDirectoryExists", Directory.Exists(AppPaths.LogsDirectory)),
                ("networkPreference", settings.LootTrackingEnabled), ("mapTrackingPreference", settings.MapTrackingActive),
                ("onlineMarketPreference", settings.OnlineMarketPricesEnabled));
        }

        private DiagnosticResult Ui()
        {
            if (Settings is not { } settings) return Missing();
            var stalls = UiResponsiveness.Recent;
            var ui = Existing<UiState>();
            return Result(stalls.Any(stall => stall.At > DateTime.Now.AddMinutes(-5) && stall.Duration.TotalSeconds > 2)
                ? DiagnosticStatus.Warning : ui is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("uiLanguage", Language(settings.UiLanguage)), ("nameLanguage", Language(settings.NameLanguage)),
                ("tutorialCompleted", settings.TutorialDone), ("tutorialOpen", ui?.TutorialOpen),
                ("mapRegionConfigured", settings.MapRegion is not null), ("recentStalls", stalls.Count),
                ("longestStallMs", stalls.Count == 0 ? null : stalls.Max(stall => stall.Duration.TotalMilliseconds)),
                ("mapDataAvailable", Existing<ProgressService>()?.MapDataDirectory is not null));
        }

        private DiagnosticResult Updates()
        {
            if (Existing<UpdateService>() is not { } updates) return Missing();
            return Result(updates.State == UpdateState.Failed ? DiagnosticStatus.Error
                : updates.State == UpdateState.Idle ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("state", EnumValue(updates.State)), ("checkedAtUtc", updates.CheckedAt?.ToUniversalTime()),
                ("availableVersions", updates.Available.Count), ("automaticCheck", Settings?.CheckUpdatesOnStart));
        }

        private DiagnosticResult Network()
        {
            if (Existing<NetworkLootService>() is not { } network) return Missing();
            var progress = network.ProgressDiagnostics;
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : network.StatusIsError ? DiagnosticStatus.Error
                : !network.Running || network.Packets == 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("enabled", Settings?.LootTrackingEnabled), ("running", network.Running),
                ("npcapInstalled", NetworkLootService.NpcapInstalled), ("packets", network.Packets),
                ("messages", network.Messages), ("framingResyncs", network.Resyncs),
                ("identityBound", progress.IdentityBound), ("identityBindings", progress.IdentityBindings),
                ("contextResets", progress.ContextResets), ("rejectedMessages", progress.RejectedProgressMessages),
                ("pendingMessages", progress.PendingMessages), ("pendingBytes", progress.PendingBytes),
                ("pendingOverflow", progress.PendingOverflow), ("unboundSoulMessages", progress.UnboundSoulMessages));
        }

        private DiagnosticResult Capture()
        {
            if (Existing<GameCaptureService>() is not { } service) return Missing();
            var permission = DiagnosticSupportProbe.BorderPermission();
            return CaptureSnapshot(service.Diagnostics, Settings?.MapTrackingActive, service.BorderSuppressed,
                DateTimeOffset.UtcNow, permission.Supported, permission.Permission);
        }

        private DiagnosticResult Map()
        {
            if (Existing<MapTrackingService>() is not { } map) return Missing();
            var world = Existing<WorldContextService>()?.Snapshot;
            return Result(Settings?.MapTrackingActive == false ? DiagnosticStatus.Disabled
                : map.StatusIsError ? DiagnosticStatus.Error : map.Found ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("running", map.Running), ("found", map.Found), ("paused", map.Paused), ("fps", map.Fps),
                ("mode", EnumValue(map.Mode)), ("regionConfigured", map.Region is not null),
                ("previewAvailable", map.PreviewDataUrl is not null), ("positionAvailable", map.Position is not null),
                ("worldMapOpen", map.WorldMapOpen), ("escMenuOpen", map.EscMenuOpen),
                ("worldStatus", world is null ? null : EnumValue(world.Status)),
                ("worldKind", world is null ? null : EnumValue(world.Kind)),
                ("worldObservedAtUtc", world?.ObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult Navigation()
        {
            if (Existing<MapTargetsService>() is not { } targets) return Missing();
            if (id == "navigation") return Result(DiagnosticStatus.Healthy,
                ("targetCount", targets.Targets.Count), ("visibleTargets", targets.VisibleTargets.Count),
                ("progressionTarget", targets.Progression is not null), ("bossTargets", targets.BossNavigationTargets.Count),
                ("positionAvailable", Existing<MapTrackingService>()?.Position is not null));
            var routes = targets.Routes;
            if (id == "map-leveling") return Result(targets.ActiveLevelingRouteId is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("savedLevelingRoutes", routes.Count(route => route.IsLeveling)), ("activeLevelingRoute", targets.ActiveLevelingRouteId is not null),
                ("progressionEnabled", Settings?.ProgressionEnabled));
            return Result(DiagnosticStatus.Healthy, ("savedRoutes", routes.Count), ("activeRoutes", targets.ActiveRoutes.Count),
                ("savedStops", routes.Sum(route => route.Stops.Count)), ("levelingRoutes", routes.Count(route => route.IsLeveling)));
        }

        private DiagnosticResult Pets()
        {
            if (Existing<ProgressService>() is not { } progress) return Missing();
            var pets = progress.Catalog.Pets.Select(pet => progress.EntrySnapshot(pet.Id)).ToArray();
            var sources = Soulcrest.Core.Pets.PetAcquisitionCatalog.All.Values.ToArray();
            var network = Existing<NetworkLootService>();
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : network?.PetBaselineAt is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("dungeonPets", sources.Length),
                ("dungeonAvailablePets", sources.Count(pet => pet.Sources.Any(source => source.HasAvailableVariants))),
                ("dungeonUpcomingPets", sources.Count(pet => pet.Sources.Any(source => source.HasUpcomingVariants))),
                ("dungeonUnconfirmedPets", sources.Count(pet => pet.Sources.Any(source => source.HasUnconfirmedVariants))),
                ("catalogPets", pets.Length), ("manualOverrides", pets.Count(pet => pet.ManualOverride)),
                ("focusedPets", pets.Count(pet => pet.Focus)), ("baselineAtUtc", network?.PetBaselineAt?.ToUniversalTime()),
                ("sourceRunning", network?.Running), ("acceptedSoulMessages", network?.ProgressDiagnostics.AcceptedSoulMessages),
                ("acceptedSoulPoints", network?.ProgressDiagnostics.AcceptedSoulPoints));
        }

        private DiagnosticResult DungeonPets()
        {
            if (Existing<PetDungeonMapService>() is not { } service) return Missing();
            var state = service.Diagnostics();
            return Result(state.DisplayState == DungeonPetMapDisplayState.Error ? DiagnosticStatus.Error
                : state.Maps == 0 ? DiagnosticStatus.Waiting
                : state.MissingImages > 0 ? DiagnosticStatus.Warning
                : state.DisplayState == DungeonPetMapDisplayState.Ready ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("catalogMaps", state.Maps), ("petSpecies", state.PetSpecies),
                ("placements", state.Placements), ("missingOrInvalidImages", state.MissingImages),
                ("displayState", state.DisplayState.ToString()), ("displayObservedAtUtc", state.DisplayObservedAt),
                ("imageChecksumsRequired", true), ("changesTracking", false));
        }

        private DiagnosticResult Exploration()
        {
            if (Existing<ExplorationService>() is not { } exploration) return Missing();
            var snapshot = exploration.Snapshot();
            var source = Existing<NetworkLootService>()?.ProgressSources;
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : source?.IdentityIsLive != true ? DiagnosticStatus.Waiting
                : source.QuestFormatRejected || source.TeleportFormatRejected ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy,
                ("profileCount", exploration.Characters.Count), ("completedPlaces", snapshot.Completed.Count),
                ("knownPlaces", snapshot.Progress.Count(pair => pair.Value.State is not null)),
                ("manualOverrides", snapshot.Progress.Count(pair => pair.Value.ManualOverride)),
                ("ownBound", source?.IdentityIsLive), ("questBaselineAtUtc", source?.QuestBaselineAt?.ToUniversalTime()),
                ("teleportBaselineAtUtc", source?.TeleportBaselineAt?.ToUniversalTime()),
                ("questFormatRejected", source?.QuestFormatRejected), ("teleportFormatRejected", source?.TeleportFormatRejected));
        }

        private DiagnosticResult Merchant()
        {
            if (Existing<MerchantService>() is not { } merchant) return Missing();
            var snapshot = merchant.Snapshot;
            var rows = merchant.Rows;
            var petRows = rows.Count;
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : snapshot is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("hasOfferSnapshot", snapshot is not null), ("offerCount", snapshot?.Offers.Count),
                ("observedAtUtc", snapshot?.At.ToUniversalTime()), ("petRowCount", petRows),
                ("dungeonRows", rows.Count(row => row.DungeonAvailability is not null)),
                ("counterEnabled", Settings?.MerchantOverlayEnabled));
        }

        private DiagnosticResult Bosses()
        {
            if (Existing<BossRushService>() is not { } bosses) return Missing();
            var rows = bosses.Schedule;
            return Result(rows.Count == 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("catalogBosses", bosses.Catalogue.Count), ("timerRows", rows.Count),
                ("cachedRows", rows.Count(row => row.Cached)), ("calendarRows", rows.Count(row => row.Calendar)),
                ("confirmedSpawns", rows.Count(row => row.Spawned)), ("navigationRows", bosses.NavigationSchedule.Count),
                ("navigationEnabled", Settings?.BossRushEnabled), ("remindersEnabled", Settings?.BossAlertsEnabled));
        }

        private DiagnosticResult Alerts()
        {
            if (Existing<AlertsService>() is not { } alerts) return Missing();
            var items = alerts.Items;
            var health = alerts.Diagnostics;
            return Result(!health.StoreReadable || health.StorageFailures > 0 ? DiagnosticStatus.Error
                : health.TimerRunning ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("reminderCount", items.Count), ("enabledReminders", items.Count(item => item.Enabled)),
                ("soundReminders", items.Count(item => item.Enabled && item.PlaySound)),
                ("upcomingReminders", alerts.Upcoming.Count(item => item.At is not null)),
                ("catalogTimers", alerts.EventTimers.Count), ("timerRunning", health.TimerRunning),
                ("storeReadable", health.StoreReadable), ("storageFailures", health.StorageFailures),
                ("lastTickAtUtc", health.LastTickUtc), ("retainedNotices", health.RetainedNotices),
                ("bossRemindersEnabled", Settings?.BossAlertsEnabled));
        }

        private DiagnosticResult Loot()
        {
            if (Existing<UnifiedLootTrackerService>() is not { } tracker) return Missing();
            var snapshot = tracker.Snapshot;
            var sessions = snapshot.Sessions;
            var fault = tracker.RecordingFault;
            if (id == "loot-statistics") return Result(DiagnosticStatus.Healthy,
                ("sessions", sessions.Length), ("timelineEvents", sessions.Sum(session => session.TimelineEvents.Length)),
                ("minuteBins", sessions.Sum(session => session.MinuteIncome.Length)),
                ("routeSegments", sessions.Sum(session => session.RouteSegments.Length)),
                ("limitedCoverageSessions", sessions.Count(session => session.CoverageLimited)), ("observedAtUtc", snapshot.At?.ToUniversalTime()));
            if (id == "loot-sessions") return LootSessionSnapshot(snapshot, tracker.SourceAvailable,
                tracker.StatusIsError, Existing<SettingsService>()?.GetLootTrackerSmallSessionItemThreshold(),
                tracker.AutomaticallyDeletedSmallSessionCount, tracker.SmallSessionCleanupIncomplete, DateTimeOffset.UtcNow,
                Settings?.LootTrackingEnabled, tracker.OverlayState.WaitingForWorldContext, fault);
            var enabled = id == "loot-pve" ? Settings?.MonsterLootTrackingEnabled : Settings?.LifeskillTrackingEnabled;
            return Result(enabled == false ? DiagnosticStatus.Disabled : tracker.StatusIsError ? DiagnosticStatus.Error
                : tracker.SourceAvailable ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("enabled", enabled), ("sourceAvailable", tracker.SourceAvailable),
                ("waitingForWorldContext", tracker.OverlayState.WaitingForWorldContext), ("sessions", sessions.Length),
                ("dailyKinaConfirmed", id == "loot-pve" ? tracker.DailyMonsterKinaConfirmed : null),
                ("sessionsWithActivity", id == "loot-pve" ? sessions.Count(session => session.MonsterCount > 0)
                    : sessions.Count(session => session.HarvestCount > 0 || session.FailedHarvestCount > 0)),
                ("idlePauseSeconds", LootTrackerPresentation.IdlePauseSeconds),
                ("idleCloseSeconds", LootTrackerPresentation.IdleCloseSeconds), ("observedAtUtc", snapshot.At?.ToUniversalTime()),
                ("recordingFaultPresent", fault is not null), ("recordingFaultAtUtc", fault?.At.ToUniversalTime()),
                ("recordingFaultStage", fault?.Stage.ToString()), ("recordingFaultKind", fault?.Kind.ToString()),
                ("recordingFaultExceptionType", fault?.ExceptionType), ("recordingFaultHResult", fault?.HResult));
        }

        private DiagnosticResult Abyss()
        {
            if (Existing<AbyssProgressService>() is not { } abyss) return Missing();
            var snapshot = abyss.ProgressSnapshot;
            return Result(Settings?.AbyssTrackingEnabled == false ? DiagnosticStatus.Disabled
                : abyss.StatusIsError ? DiagnosticStatus.Error : snapshot.Confirmed ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("enabled", Settings?.AbyssTrackingEnabled), ("confirmed", snapshot.Confirmed),
                ("balanceConfirmed", snapshot.AbyssPointBalanceConfirmed), ("farmConfirmed", snapshot.FarmedAbyssPointsConfirmed),
                ("rankConfirmed", snapshot.RankPointsConfirmed), ("rankTrackingActive", snapshot.RankTrackingActive),
                ("capKnown", snapshot.Cap is not null), ("remainingBudgetKnown", snapshot.Remaining is not null),
                ("observedAtUtc", snapshot.ObservedAt?.ToUniversalTime()), ("rankObservedAtUtc", snapshot.RankPointsObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult KillFeed()
        {
            if (Existing<AbyssKillFeedService>() is not { } feed) return Missing();
            var snapshot = feed.Snapshot;
            return Result(Settings?.AbyssKillFeedEnabled == false ? DiagnosticStatus.Disabled : DiagnosticStatus.Healthy,
                ("enabled", Settings?.AbyssKillFeedEnabled), ("entries", snapshot.Length),
                ("showKills", Settings?.AbyssKillFeedShowKills), ("showDeaths", Settings?.AbyssKillFeedShowDeaths),
                ("showAssists", Settings?.AbyssKillFeedShowAssists), ("maximumEntries", AbyssKillFeedService.MaximumEntries));
        }

        private DiagnosticResult Market()
        {
            var snapshot = Existing<ExchangeLootValuationService>()?.Snapshot;
            var automatic = Existing<ExchangeMarketAutoSelectionService>()?.Snapshot;
            var servers = Existing<ExchangeMarketCache>()?.GetServers();
            var liveMarket = Existing<OverlayEditorMarketService>()?.Snapshot;
            if (snapshot is null && automatic is null && liveMarket is null) return Missing();
            var online = snapshot?.OnlineEnabled ?? liveMarket?.OnlineEnabled ?? Settings?.OnlineMarketPricesEnabled;
            return Result(online == false ? DiagnosticStatus.Disabled : snapshot?.StatusIsError == true || automatic?.IsError == true || liveMarket?.StatusIsError == true ? DiagnosticStatus.Error
                : automatic is not null && (!automatic.OwnCharacterKnown || automatic.Automatic && (!automatic.MappingKnown || automatic.Loading))
                    || snapshot is null && liveMarket is null || snapshot?.PendingCount > 0 || liveMarket is { Available: true } && (liveMarket.Loading || liveMarket.UnknownRows > 0)
                    ? DiagnosticStatus.Waiting : liveMarket is { Available: true, Stale: true } ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy,
                ("onlineEnabled", online), ("entries", snapshot?.Entries.Length), ("pending", snapshot?.PendingCount),
                ("estimated", snapshot?.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.Estimated)),
                ("referenceOnly", snapshot?.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.ReferenceOnly)),
                ("unknown", snapshot?.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.Unknown)),
                ("automaticSelectionAvailable", automatic is not null), ("detectedOwnProfileAvailable", automatic?.OwnCharacterKnown),
                ("automaticServerSelection", automatic?.Automatic), ("automaticServerMapped", automatic?.MappingKnown),
                ("automaticServerLoading", automatic?.Loading), ("automaticServerError", automatic?.IsError),
                ("catalogueServers", servers?.Length), ("nativeServerKeys", servers?.Count(server => server.GameServerKey is > 0)),
                ("ambiguousNativeServerKeys", servers?.Where(server => server.GameServerKey is > 0)
                    .GroupBy(server => server.GameServerKey).Count(group => group.Count() > 1)),
                ("liveMarketAverage24Hours", liveMarket?.Uses24HourAverage), ("liveMarketAvailable", liveMarket?.Available),
                ("liveMarketAverageFallbackEnabled", liveMarket?.AverageFallbackEnabled),
                ("liveMarketCurrentRows", liveMarket?.CurrentRows), ("liveMarketAverageRows", liveMarket?.AverageRows),
                ("liveMarketPricedRows", liveMarket?.PricedRows), ("liveMarketUnknownRows", liveMarket?.UnknownRows),
                ("liveMarketSamples", liveMarket?.AverageSamples), ("liveMarketLoading", liveMarket?.Loading),
                ("liveMarketStale", liveMarket?.Stale), ("liveMarketWindowStart", liveMarket?.AverageWindowStart),
                ("liveMarketWindowEnd", liveMarket?.AverageWindowEnd));
        }

        private DiagnosticResult Replay()
        {
            if (Existing<LootTrackerReplayService>() is not { } replay) return Missing();
            var rows = replay.Snapshot;
            return Result(rows.Any(row => row.Status == LootTrackerReplayStatus.Failed) ? DiagnosticStatus.Warning
                : rows.Any(row => row.IsBusy) ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("knownReplays", rows.Length), ("preparing", rows.Count(row => row.IsBusy)),
                ("ready", rows.Count(row => row.CanPlay)), ("failed", rows.Count(row => row.Status == LootTrackerReplayStatus.Failed)),
                ("stale", rows.Count(row => row.Status == LootTrackerReplayStatus.Stale)));
        }

        private DiagnosticResult DropAlerts()
        {
            if (Settings is not { } settings) return Missing();
            var health = Existing<RareDropAlertService>()?.Diagnostics;
            return Result(!settings.LootDropAlertsEnabled ? DiagnosticStatus.Disabled
                : health is null || health.Disposed ? DiagnosticStatus.Unavailable
                : health.AudioFailures > 0 ? DiagnosticStatus.Warning
                : health.ReceivedDrops == 0 && health.AudioCompletions == 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("enabled", settings.LootDropAlertsEnabled), ("soundEnabled", settings.LootDropSoundEnabled),
                ("animationsEnabled", settings.LootDropAnimationsEnabled), ("soundVolume", settings.LootDropSoundVolume),
                ("serviceAvailable", health is not null), ("rarityOverrides", settings.LootDropRarityAlerts.Count),
                ("sourceAttached", health?.SourceAttached), ("presentationVisible", health?.PresentationVisible),
                ("receivedDrops", health?.ReceivedDrops), ("audioStarts", health?.AudioStarts),
                ("audioCompletions", health?.AudioCompletions), ("audioFailures", health?.AudioFailures),
                ("retainedNotices", health?.RetainedNotices), ("audioActive", health?.AudioActive));
        }

        private DiagnosticResult Character()
        {
            if (Existing<CharacterCombatStateService>() is not { } character) return Missing();
            var snapshot = character.Snapshot;
            return Result(snapshot.IsFresh ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("fresh", snapshot.IsFresh), ("profileBound", snapshot.ProfileId is not null),
                ("classKnown", snapshot.ClassId is not null), ("levelKnown", snapshot.CharacterLevel is not null),
                ("combatScoreKnown", snapshot.CurrentCombatScore is not null), ("missingRequirements", snapshot.MissingRequirements.Count),
                ("observedAtUtc", snapshot.ObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult Combat()
        {
            if (Existing<CharacterCombatStateService>() is not { } character) return Missing();
            var snapshot = character.Snapshot;
            return id switch
            {
                "combat-stat-overview" => Domain(snapshot.NativeStats, ("nativeStatCount", snapshot.NativeStats.Value?.Count),
                    ("displayRuleCount", Soulcrest.Core.Combat.CharacterDisplayStatProjection.Rules.Count),
                    ("tableInferredRuleCount", Soulcrest.Core.Combat.CharacterDisplayStatProjection.Rules.Count(rule =>
                        rule.Qualification == Soulcrest.Core.Combat.CharacterDisplayStatQualification.TableInferred)),
                    ("tableInferredValues", snapshot.DisplayStats.Rows.Count(row => row.Value is not null
                        && row.Status == Soulcrest.Core.Combat.CombatKnowledgeStatus.Complete
                        && row.Qualification == Soulcrest.Core.Combat.CharacterDisplayStatQualification.TableInferred)),
                    ("hpMaximumKnown", snapshot.HpMaximum.Value is not null), ("mpMaximumKnown", snapshot.MpMaximum.Value is not null),
                    ("evaluationAvailable", snapshot.Breakdown is not null), ("missingRequirements", snapshot.MissingRequirements.Count)),
                "combat-wings" => Domain(snapshot.WingOwnership, ("ownedWings", snapshot.WingOwnership.Value?.Wings.Count),
                    ("equipmentStatus", EnumValue(snapshot.EquippedWing.Status)), ("equipmentFresh", snapshot.EquippedWing.IsFresh)),
                "combat-gear" => Domain(snapshot.Equipment, ("equipmentItems", snapshot.Equipment.Value?.Items.Length),
                    ("abnormalStatus", EnumValue(snapshot.Abnormals.Status)), ("abnormalsFresh", snapshot.Abnormals.IsFresh)),
                "combat-pets" => Domain(snapshot.Pets, ("collectionAvailable", Existing<ProgressService>() is not null)),
                "combat-titles" => Domain(snapshot.Titles, ("ownedTitles", snapshot.TitleOwnership.Value?.Count),
                    ("ownershipStatus", EnumValue(snapshot.TitleOwnership.Status)), ("equippedCategories", snapshot.Titles.Value?.EquippedByCategory.Count)),
                _ => throw new InvalidOperationException("Unexpected combat feature.")
            };
        }

        private DiagnosticResult Skills()
        {
            if (Existing<OwnSkillStateService>() is not { } source) return Missing();
            var snapshot = source.Snapshot;
            return Result(!snapshot.IsKnown ? DiagnosticStatus.Waiting : snapshot.IsFresh ? DiagnosticStatus.Healthy : DiagnosticStatus.Warning,
                ("known", snapshot.IsKnown), ("fresh", snapshot.IsFresh), ("skillCount", snapshot.Skills.Count),
                ("ownProfileBound", snapshot.ProfileId is not null), ("observedAtUtc", snapshot.ObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult Daevanion()
        {
            var source = Existing<CharacterCombatStateService>();
            var plan = Existing<DaevanionPlanService>();
            if (source is null && plan is null) return Missing();
            var snapshot = source?.Snapshot;
            var planning = plan?.Snapshot;
            return Result(planning?.IsBusy == true ? DiagnosticStatus.Waiting : snapshot is null ? DiagnosticStatus.Unavailable
                : DomainStatus(snapshot.ActivatedBoards), ("boardSourceStatus", snapshot is null ? null : EnumValue(snapshot.ActivatedBoards.Status)),
                ("knownBoards", snapshot?.ActivatedBoards.Value?.Boards.Count), ("pointPools", snapshot?.PointPools.Count),
                ("plannerAvailable", plan is not null), ("plannerBusy", planning?.IsBusy), ("canCalculate", planning?.CanCalculate),
                ("missingRequirements", planning?.MissingRequirements.Count), ("savedPlans", planning?.Routes.Count),
                ("manualMeasurements", planning?.Measurements.Count), ("boardObservationAvailable", Existing<DaevanionService>()?.Snapshot is not null));
        }

        private DiagnosticResult Genus()
        {
            if (Existing<CharacterCombatStateService>() is not { } source) return Missing();
            // The presentation service's Snapshot evaluates and changes preset authority. Diagnose
            // the immutable producer and menu observations without entering that state machine.
            var ownCharacter = source.Snapshot;
            var shared = Existing<INetworkGenusInsightSource>()?.GenusInsightSnapshot;
            var character = GenusInsightStateService.ForGenusSource(ownCharacter, shared);
            var menu = Existing<GenusMenuObservationService>()?.Snapshot;
            if (character.Pets.Value is { } pets && menu?.ViewedPreset is { } viewed)
                character = character with { Pets = character.Pets with { Value = pets with { SelectedGenusPanel = viewed } } };
            var snapshot = GenusInsightProjection.Create(character);
            if (!snapshot.IsCurrent && source.SavedGenusInsight is { } saved) snapshot = GenusInsightProjection.CreateSaved(saved);
            return Result(snapshot.IsCurrent ? DomainStatus(character.Pets) : snapshot.IsHistorical ? DiagnosticStatus.Warning : DiagnosticStatus.Waiting,
                ("current", snapshot.IsCurrent), ("historical", snapshot.IsHistorical), ("boards", snapshot.Boards.Count),
                ("slots", snapshot.Boards.Sum(board => board.Slots.Count)), ("appliedEffects", snapshot.AppliedEffects.Count),
                ("catalogStats", GenusInsightCatalog.Default.Stats.Count),
                ("boundCatalogStats", GenusInsightNativeBindings.Stats.Count),
                ("unboundCatalogStats", GenusInsightCatalog.Default.Stats.Count - GenusInsightNativeBindings.Stats.Count),
                ("rangeQualifiedRolls", snapshot.Boards.Sum(board => board.Slots.Count(slot => slot.RollFraction is not null))),
                ("effectsStatus", EnumValue(snapshot.AppliedEffectsStatus)), ("menuVisible", menu?.MenuVisible),
                ("observedPresetKnown", snapshot.Preset is not null), ("viewedPresetKnown", menu?.ViewedPreset is not null),
                ("presentationServiceAvailable", Existing<GenusInsightStateService>() is not null),
                ("combatPetSourceStatus", EnumValue(ownCharacter.Pets.Status)),
                ("sharedSourceAvailable", shared is not null), ("sharedSourceFresh", shared?.Domain.IsFresh),
                ("sharedSourceStatus", shared is null ? null : EnumValue(shared.Domain.Status)),
                ("observedAtUtc", snapshot.ObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult Achievements()
        {
            if (Existing<AchievementStateService>() is not { } source) return Missing();
            var snapshot = source.CurrentOwnSnapshot;
            var tracker = Existing<AchievementTrackerService>()?.Snapshot;
            return Result(snapshot.State.Issues.Length > 0 || snapshot.State.HistoryGap ? DiagnosticStatus.Warning
                : snapshot.IsLive ? DiagnosticStatus.Healthy : snapshot.IsHistorical ? DiagnosticStatus.Warning : DiagnosticStatus.Waiting,
                ("live", snapshot.IsLive), ("historical", snapshot.IsHistorical), ("hasProgressEvidence", snapshot.HasProgressEvidence),
                ("observedFamilies", snapshot.State.ObservedFamilyIds.Count), ("observedCounters", snapshot.State.ObservedValues.Count),
                ("historyGap", snapshot.State.HistoryGap), ("saving", snapshot.State.Saving), ("issues", snapshot.State.Issues.Length),
                ("trackerRows", tracker?.Rows.Length), ("trackerLive", tracker?.IsLive),
                ("observedAtUtc", snapshot.State.ObservedAt?.ToUniversalTime()));
        }

        private DiagnosticResult Pantheon()
        {
            // PantheonStateService.Snapshot calls Refresh and may persist profiles. Read the producer only.
            if (Existing<NetworkLootService>() is not { } source) return Missing();
            var snapshot = source.PantheonSnapshot;
            return Domain(snapshot.Decorations, ("sourceRunning", source.Running),
                ("ownBound", snapshot.Character is not null), ("decorations", snapshot.Decorations.Value?.Decorations.Count),
                ("presentationServiceAvailable", Existing<PantheonStateService>() is not null));
        }

        private DiagnosticResult Editor()
        {
            if (Existing<IOverlayService>() is not { } editor) return Missing();
            var overlays = editor.Overlays;
            var runtime = OverlayEditorLiveDiagnostics.CaptureRecorded();
            var rendering = OverlayEditorRenderDiagnostics.Current.Capture();
            return Result(editor.LoadError is not null ? DiagnosticStatus.Error
                : overlays.All(overlay => !overlay.Settings.Enabled) ? DiagnosticStatus.Disabled
                : overlays.Any(overlay => editor.GetState(overlay.Id).IsError) || rendering.LastAttemptFailed ? DiagnosticStatus.Error
                : overlays.Any(overlay => editor.GetState(overlay.Id).IsVisible || editor.GetState(overlay.Id).Previewing)
                    ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting, ("overlays", overlays.Count),
                ("enabledOverlays", overlays.Count(overlay => overlay.Settings.Enabled)),
                ("visibleOverlays", overlays.Count(overlay => editor.GetState(overlay.Id).IsVisible)),
                ("previewingOverlays", overlays.Count(overlay => editor.GetState(overlay.Id).Previewing)),
                ("recordingRequestedOverlays", overlays.Count(overlay => !overlay.Settings.CaptureExcluded)),
                ("recordingExcludedOverlays", overlays.Count(overlay => overlay.Settings.CaptureExcluded)),
                ("recordingSourceSafe", Existing<GameCaptureService>()?.CapturesGameWindowOnly),
                ("widgets", overlays.Sum(overlay => overlay.Settings.Widgets.Count)), ("templates", editor.Templates.Count),
                ("loadError", editor.LoadError is not null), ("templateError", editor.TemplateError is not null),
                ("runtimeObservations", runtime.Entries.Count), ("runtimeErrors", runtime.Entries.Count(entry => entry.Action == "error")),
                ("blockedPolicies", runtime.Entries.Count(entry => entry.Policy is { Result: false })),
                ("lastFailedGates", runtime.Entries.LastOrDefault(entry => entry.Policy is not null)?.Policy?.FailedGates.ToString()),
                ("renderSucceeded", rendering.Succeeded), ("renderFailed", rendering.Failed),
                ("lastRenderAttemptFailed", rendering.LastAttemptFailed), ("lastRenderFailureStage", rendering.FailureStage?.ToString()),
                ("lastRenderExceptionType", rendering.ExceptionType), ("lastRenderHResult", rendering.HResult),
                ("observationEnabled", OverlayEditorLiveDiagnostics.Enabled));
        }

        private DiagnosticResult Widget()
        {
            if (Existing<IOverlayService>() is not { } editor) return Missing();
            var kind = id[7..];
            var overlays = editor.Overlays;
            var mandatory = kind == OverlayLockWidget.Kind;
            var applicable = mandatory ? overlays.ToArray()
                : overlays.Where(overlay => overlay.Settings.Widgets.Any(widget => widget.Kind == kind)).ToArray();
            return Result(applicable.Length == 0 ? DiagnosticStatus.Disabled
                : applicable.Any(overlay => editor.GetState(overlay.Id).IsError) ? DiagnosticStatus.Error
                : applicable.All(overlay => !overlay.Settings.Enabled) ? DiagnosticStatus.Disabled
                : applicable.Any(overlay => editor.GetState(overlay.Id).IsVisible || editor.GetState(overlay.Id).Previewing)
                    ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("configuredWindows", applicable.Length), ("enabledWindows", applicable.Count(overlay => overlay.Settings.Enabled)),
                ("visibleWindows", applicable.Count(overlay => editor.GetState(overlay.Id).IsVisible)),
                ("instances", mandatory ? applicable.Length : applicable.Sum(overlay => overlay.Settings.Widgets.Count(widget => widget.Kind == kind))),
                ("mandatoryControl", mandatory), ("invalidTriggerRules", applicable.Count(overlay => overlay.Settings.TriggerRules is { } rules && !OverlayTriggerRules.Valid(rules))));
        }

        private DiagnosticResult Hotkeys()
        {
            if (Settings is not { } settings) return Missing();
            var service = Existing<OverlayHotkeyService>();
            var rows = OverlayHotkeys.Actions.Select(action => new
            {
                action.Id,
                Binding = settings.OverlayHotkeys.GetValueOrDefault(action.Id),
                Error = service?.Error(action.Id) is not null
            }).ToArray();
            var configured = rows.Count(row => row.Binding is not null);
            var invalid = rows.Count(row => row.Binding is { IsValid: false });
            var duplicates = rows.Where(row => row.Binding is not null).GroupBy(row => row.Binding).Sum(group => Math.Max(0, group.Count() - 1));
            var health = service?.Diagnostics;
            var facts = new List<(string, object?)>
            {
                ("knownActions", rows.Length), ("configuredActions", configured), ("invalidBindings", invalid),
                ("duplicateBindings", duplicates), ("registeredActions", health?.RegisteredActions),
                ("registrationErrors", health?.RegistrationErrors), ("attached", health?.Attached),
                ("capturingAssignment", health?.Capturing), ("disposed", health?.Disposed)
            };
            foreach (var row in rows)
            {
                facts.Add((row.Id + "Configured", row.Binding is not null));
                facts.Add((row.Id + "RegistrationError", service is null ? null : row.Error));
            }
            return Result(configured == 0 ? DiagnosticStatus.Disabled : health is null ? DiagnosticStatus.Unavailable
                : invalid > 0 || duplicates > 0 || health.RegistrationErrors > 0 || health.Disposed ? DiagnosticStatus.Error
                : !health.Attached || health.Capturing ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy, facts.ToArray());
        }

        private DiagnosticResult Overlay()
        {
            if (Settings is not { } settings) return Missing();
            var overlayId = id[8..];
            var enabled = OverlayRegistry.IsEnabled(settings, overlayId);
            var facts = new List<(string, object?)> { ("enabled", enabled), ("includedInRecordings", settings.OverlaysInRecordings) };
            var runtime = Existing<DiagnosticOverlayRuntime>()?.Get(overlayId);
            var age = runtime is null ? (double?)null : Math.Max(0, (DateTimeOffset.UtcNow - runtime.ObservedAt).TotalMilliseconds);
            facts.Add(("nativeVisibilityKnown", runtime is not null));
            facts.Add(("nativeVisible", runtime?.Visible)); facts.Add(("handleCreated", runtime?.HandleCreated));
            facts.Add(("disposed", runtime?.Disposed)); facts.Add(("runtimeObservedAtUtc", runtime?.ObservedAt.ToUniversalTime()));
            facts.Add(("runtimeAgeMs", age));
            var descriptor = OverlayRegistry.Get(overlayId);
            facts.Add(("freelyPositioned", descriptor.CanPlace));
            if (descriptor.CanPlace)
            {
                var layout = OverlayRegistry.ReadLayout(settings, overlayId);
                facts.Add(("scale", layout.Scale)); facts.Add(("width", layout.Width));
            }
            facts.Add(("playerTrackingRunning", Existing<MapTrackingService>()?.Running));
            MerchantOverlayHealthSnapshot? merchantHealth = null;
            switch (overlayId)
            {
                case "loot":
                    facts.Add(("petBaselineAtUtc", Existing<NetworkLootService>()?.PetBaselineAt?.ToUniversalTime()));
                    facts.Add(("catalogPets", Existing<ProgressService>()?.Catalog.Pets.Count));
                    break;
                case "achievements":
                    var achievements = Existing<AchievementTrackerService>()?.Snapshot;
                    facts.Add(("trackerLive", achievements?.IsLive)); facts.Add(("trackerRows", achievements?.Rows.Length));
                    break;
                case "genus-insight":
                    // Read immutable producer/menu observations only. The display service evaluates
                    // and persists preset authority, so diagnosis must never call its Snapshot getter.
                    var genusCharacter = Existing<CharacterCombatStateService>()?.Snapshot;
                    var genusMenu = Existing<GenusMenuObservationService>()?.Snapshot;
                    var sharedGenus = Existing<INetworkGenusInsightSource>()?.GenusInsightSnapshot;
                    facts.Add(("combatPetSourceStatus", genusCharacter is null ? null : EnumValue(genusCharacter.Pets.Status)));
                    if (genusCharacter is not null)
                        genusCharacter = GenusInsightStateService.ForGenusSource(genusCharacter, sharedGenus);
                    if (genusCharacter?.Pets.Value is { } genusValue && genusMenu?.ViewedPreset is { } viewed)
                        genusCharacter = genusCharacter with { Pets = genusCharacter.Pets with
                            { Value = genusValue with { SelectedGenusPanel = viewed } } };
                    var genusSource = genusCharacter is null ? null : GenusInsightProjection.Create(genusCharacter);
                    facts.Add(("presentationServiceAvailable", Existing<GenusInsightStateService>() is not null));
                    facts.Add(("sharedSourceAvailable", sharedGenus is not null));
                    facts.Add(("sharedSourceFresh", sharedGenus?.Domain.IsFresh));
                    facts.Add(("sharedSourceStatus", sharedGenus is null ? null : EnumValue(sharedGenus.Domain.Status)));
                    return GenusInsightOverlaySnapshot(enabled, runtime, genusSource, genusMenu, DateTimeOffset.UtcNow, facts);
                case "boss":
                    var bosses = Existing<BossRushService>()?.OverlaySpawns;
                    facts.Add(("upcomingBossRows", bosses?.Count)); facts.Add(("cachedBossRows", bosses?.Count(boss => boss.Cached)));
                    break;
                case "leveling":
                    facts.Add(("activeLevelingRoute", Existing<MapTargetsService>()?.ActiveLevelingRouteId is not null));
                    facts.Add(("progressionEnabled", settings.ProgressionEnabled));
                    break;
                case "alerts":
                    facts.Add(("catalogTimers", Existing<AlertsService>()?.EventTimers.Count));
                    facts.Add(("explicitTimerSelection", settings.TimerOverlayTimerIds?.Length));
                    break;
                case "notifications":
                    var alerts = Existing<AlertsService>()?.Items;
                    facts.Add(("enabledReminders", alerts?.Count(alert => alert.Enabled)));
                    facts.Add(("soundReminders", alerts?.Count(alert => alert.Enabled && alert.PlaySound)));
                    break;
                case "event-timeline":
                    facts.Add(("catalogTimers", Existing<AlertsService>()?.EventTimers.Count));
                    facts.Add(("bossTimers", Existing<BossRushService>()?.Schedule.Count));
                    facts.Add(("hours", settings.EventTimelineHours));
                    break;
                case "map":
                    facts.Add(("mapFound", Existing<MapTrackingService>()?.Found));
                    facts.Add(("positionAvailable", Existing<MapTrackingService>()?.Position is not null));
                    facts.Add(("showRoutes", settings.ShowRoutesInGame));
                    break;
                case "merchant":
                    var merchant = Existing<MerchantService>();
                    var offers = merchant?.Snapshot;
                    facts.Add(("serviceAvailable", merchant is not null));
                    facts.Add(("hasOfferSnapshot", offers is not null));
                    facts.Add(("petRowCount", merchant?.Rows.Count));
                    facts.Add(("offers", offers?.Offers.Count));
                    facts.Add(("lastOfferAtUtc", offers?.At.ToUniversalTime()));
                    // Read already collected anonymous state; never create a Form or start a source.
                    merchantHealth = (Existing<MerchantOverlayHealth>() ?? MerchantOverlayForm.Health).Snapshot();
                    break;
                case "daevanion":
                    facts.Add(("boardObservationAvailable", Existing<DaevanionService>()?.Snapshot is not null));
                    DaevanionRecognitionDiagnosticFacts.Append(facts, Existing<DaevanionService>()?.RecognitionDiagnostics);
                    facts.Add(("boardSourceStatus", Existing<CharacterCombatStateService>() is { } character ? EnumValue(character.Snapshot.ActivatedBoards.Status) : null));
                    break;
            }
            if (merchantHealth is not null)
                return MerchantOverlaySnapshot(enabled, runtime, merchantHealth, DateTimeOffset.UtcNow, facts);
            return Result(!enabled ? DiagnosticStatus.Disabled : runtime is null ? DiagnosticStatus.Waiting
                : runtime.Disposed ? DiagnosticStatus.Error : age > 5000 ? DiagnosticStatus.Warning : DiagnosticStatus.Healthy, facts.ToArray());
        }
    }
}
