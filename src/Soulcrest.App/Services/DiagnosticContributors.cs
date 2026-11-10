using System.Collections.ObjectModel;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;

namespace Soulcrest.App.Services;

/// <summary>
/// Anonymous projections of existing host state. The supplied provider must only return already
/// constructed instances (DiagnosticServiceRegistry); diagnosis must never resolve the live DI container.
/// No contributor starts a source, refreshes a store or serializes a production snapshot.
/// </summary>
public static class DiagnosticContributors
{
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
        bool? trackingEnabled)
    {
        var sessions = snapshot.Sessions;
        var age = snapshot.At is { } observed ? now - observed : (TimeSpan?)null;
        var fresh = age is { } elapsed ? elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(5) : (bool?)null;
        var status = statusError || cleanupIncomplete ? DiagnosticStatus.Error
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
                ["snapshotFresh"] = fresh,
                ["snapshotAgeMilliseconds"] = age?.Ticks / TimeSpan.TicksPerMillisecond,
                ["idlePauseSeconds"] = LootTrackerPresentation.IdlePauseSeconds,
                ["idleCloseSeconds"] = LootTrackerPresentation.IdleCloseSeconds,
                ["smallSessionItemThreshold"] = itemThreshold is { } threshold ? LootTrackerSessionSize.NormalizeItemThreshold(threshold) : null,
                ["automaticSmallSessionDeletionEnabled"] = itemThreshold is { } configured ? configured > 0 : null,
                ["automaticallyDeletedSmallSessions"] = Math.Max(0, automaticallyDeleted),
                ["smallSessionCleanupIncomplete"] = cleanupIncomplete,
                ["abyssSessionsExcludedFromIdleAndAutomaticDeletion"] = true,
                ["observedAtUtc"] = snapshot.At?.ToUniversalTime()
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
            "system" => System(), "settings-storage" => Storage(), "ui" => Ui(), "updates" => Updates(),
            "network" => Network(), "capture" => Capture(),
            "map" => Map(), "navigation" or "routes" or "map-leveling" => Navigation(),
            "pets" => Pets(), "exploration" => Exploration(), "merchant" => Merchant(),
            "boss-rush" => Bosses(), "alerts" => Alerts(),
            "loot-market" => Market(), "loot-replay" => Replay(), "loot-alerts" => DropAlerts(),
            "loot-killfeed" => KillFeed(), "loot-abyss" => Abyss(),
            "loot-pve" or "loot-lifeskill" or "loot-sessions" or "loot-statistics" => Loot(),
            "character" => Character(), "combat-skills" => Skills(),
            "combat-daevanion" => Daevanion(), "combat-genus-insight" => Genus(),
            "combat-achievements" => Achievements(), "combat-pantheon" => Pantheon(),
            "combat-stat-overview" or "combat-wings" or "combat-gear" or "combat-pets" or "combat-titles" => Combat(),
            "overlay-editor" => Editor(), "overlay-hotkeys" => Hotkeys(),
            _ when id.StartsWith("widget-", StringComparison.Ordinal) => Widget(),
            _ when id.StartsWith("overlay-", StringComparison.Ordinal) => Overlay(),
            _ => throw new InvalidOperationException("Diagnostic feature has no implementation.")
        };

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
            var network = Existing<NetworkLootService>();
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : network?.PetBaselineAt is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("catalogPets", pets.Length), ("manualOverrides", pets.Count(pet => pet.ManualOverride)),
                ("focusedPets", pets.Count(pet => pet.Focus)), ("baselineAtUtc", network?.PetBaselineAt?.ToUniversalTime()),
                ("sourceRunning", network?.Running), ("acceptedSoulMessages", network?.ProgressDiagnostics.AcceptedSoulMessages),
                ("acceptedSoulPoints", network?.ProgressDiagnostics.AcceptedSoulPoints));
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
            var petRows = merchant.Rows.Count;
            return Result(Settings?.LootTrackingEnabled == false ? DiagnosticStatus.Disabled
                : snapshot is null ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("hasOfferSnapshot", snapshot is not null), ("offerCount", snapshot?.Offers.Count),
                ("observedAtUtc", snapshot?.At.ToUniversalTime()), ("petRowCount", petRows),
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
            if (id == "loot-statistics") return Result(DiagnosticStatus.Healthy,
                ("sessions", sessions.Length), ("timelineEvents", sessions.Sum(session => session.TimelineEvents.Length)),
                ("minuteBins", sessions.Sum(session => session.MinuteIncome.Length)),
                ("routeSegments", sessions.Sum(session => session.RouteSegments.Length)),
                ("limitedCoverageSessions", sessions.Count(session => session.CoverageLimited)), ("observedAtUtc", snapshot.At?.ToUniversalTime()));
            if (id == "loot-sessions") return LootSessionSnapshot(snapshot, tracker.SourceAvailable,
                tracker.StatusIsError, Existing<SettingsService>()?.GetLootTrackerSmallSessionItemThreshold(),
                tracker.AutomaticallyDeletedSmallSessionCount, tracker.SmallSessionCleanupIncomplete, DateTimeOffset.UtcNow,
                Settings?.LootTrackingEnabled);
            var enabled = id == "loot-pve" ? Settings?.MonsterLootTrackingEnabled : Settings?.LifeskillTrackingEnabled;
            return Result(enabled == false ? DiagnosticStatus.Disabled : tracker.StatusIsError ? DiagnosticStatus.Error
                : tracker.SourceAvailable ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
                ("enabled", enabled), ("sourceAvailable", tracker.SourceAvailable), ("sessions", sessions.Length),
                ("dailyKinaConfirmed", id == "loot-pve" ? tracker.DailyMonsterKinaConfirmed : null),
                ("sessionsWithActivity", id == "loot-pve" ? sessions.Count(session => session.MonsterCount > 0)
                    : sessions.Count(session => session.HarvestCount > 0 || session.FailedHarvestCount > 0)),
                ("idlePauseSeconds", LootTrackerPresentation.IdlePauseSeconds),
                ("idleCloseSeconds", LootTrackerPresentation.IdleCloseSeconds), ("observedAtUtc", snapshot.At?.ToUniversalTime()));
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
            if (Existing<ExchangeLootValuationService>() is not { } market) return Missing();
            var snapshot = market.Snapshot;
            return Result(!snapshot.OnlineEnabled ? DiagnosticStatus.Disabled : snapshot.StatusIsError ? DiagnosticStatus.Error
                : snapshot.PendingCount > 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy,
                ("onlineEnabled", snapshot.OnlineEnabled), ("entries", snapshot.Entries.Length), ("pending", snapshot.PendingCount),
                ("estimated", snapshot.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.Estimated)),
                ("referenceOnly", snapshot.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.ReferenceOnly)),
                ("unknown", snapshot.Entries.Count(entry => entry.Status == ExchangeLootValueStatus.Unknown)));
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
            var character = source.Snapshot;
            var snapshot = GenusInsightProjection.Create(character);
            if (!snapshot.IsCurrent && source.SavedGenusInsight is { } saved) snapshot = GenusInsightProjection.CreateSaved(saved);
            var menu = Existing<GenusMenuObservationService>()?.Snapshot;
            return Result(snapshot.IsCurrent ? DomainStatus(character.Pets) : snapshot.IsHistorical ? DiagnosticStatus.Warning : DiagnosticStatus.Waiting,
                ("current", snapshot.IsCurrent), ("historical", snapshot.IsHistorical), ("boards", snapshot.Boards.Count),
                ("slots", snapshot.Boards.Sum(board => board.Slots.Count)), ("appliedEffects", snapshot.AppliedEffects.Count),
                ("effectsStatus", EnumValue(snapshot.AppliedEffectsStatus)), ("menuVisible", menu?.MenuVisible),
                ("observedPresetKnown", snapshot.Preset is not null), ("viewedPresetKnown", menu?.ViewedPreset is not null),
                ("presentationServiceAvailable", Existing<GenusInsightStateService>() is not null),
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
            return Result(editor.LoadError is not null || overlays.Any(overlay => editor.GetState(overlay.Id).IsError)
                ? DiagnosticStatus.Error : runtime.Entries.Count == 0 ? DiagnosticStatus.Waiting : DiagnosticStatus.Healthy, ("overlays", overlays.Count),
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
