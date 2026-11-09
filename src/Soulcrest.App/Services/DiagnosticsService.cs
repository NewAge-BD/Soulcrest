using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

public sealed record DiagnosticHint(bool Severe, string Text);

/// <summary>Selectable, bounded, passive feature diagnosis. Nothing is uploaded (2026-11-09).</summary>
public sealed class DiagnosticsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SettingsService _settings;
    private readonly GameCaptureService _capture;
    private readonly MapTrackingService _map;
    private readonly NetworkLootService? _network;
    private readonly NetworkDiagnosticRecorder? _recorder;
    private readonly IReadOnlyList<IDiagnosticContributor> _contributors;
    private readonly Dictionary<string, Task<DiagnosticResult>> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly System.Threading.Timer? _timer;
    private readonly TimeSpan _timeout;
    private IDisposable? _recordingObservation;
    private IReadOnlyList<DiagnosticResult> _results = Array.Empty<DiagnosticResult>();
    private IReadOnlyList<DiagnosticResult> _backgroundResults = Array.Empty<DiagnosticResult>();
    private long _request;
    private int _backgroundChecking;
    private bool _packaging;
    private volatile bool _disposed;

    public DiagnosticsService(SettingsService settings, GameCaptureService capture, TrackerService loot,
        MapTrackingService map, NetworkLootService? network = null,
        IReadOnlyList<IDiagnosticContributor>? contributors = null, bool runTimer = true,
        TimeSpan? contributorTimeout = null)
    {
        _settings = settings;
        _capture = capture;
        _map = map;
        _network = network;
        _recorder = network?.DiagnosticRecorder;
        _contributors = contributors ?? DiagnosticContributors.Create(new DiagnosticServiceRegistry(settings, capture, loot, map, network));
        _timeout = contributorTimeout ?? TimeSpan.FromSeconds(2);
        if (_recorder is not null) _recorder.Changed += Notify;
        if (runTimer) _timer = new(_ => _ = BackgroundCheckAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    public IReadOnlyList<DiagnosticFeature> Features => DiagnosticFeatureCatalog.All;
    public IReadOnlyList<DiagnosticResult> Results => Volatile.Read(ref _results);
    public IReadOnlyList<DiagnosticResult> BackgroundResults => Volatile.Read(ref _backgroundResults);
    public IReadOnlyList<DiagnosticHint> Hints => Results.Where(result => result.Status is DiagnosticStatus.Warning or DiagnosticStatus.Error)
        .Select(result => new DiagnosticHint(result.Status == DiagnosticStatus.Error, result.Summary)).ToArray();
    public DateTime? CheckedAt => Results.Count == 0 ? null : Results.Max(result => result.CheckedAt).LocalDateTime;
    public string ReportPath => Path.Combine(AppPaths.LogsDirectory, "diagnose.txt");
    public NetworkDiagnosticRecorderSnapshot Recording => _recorder?.Snapshot ?? NetworkDiagnosticRecorderSnapshot.Empty;
    public event Action? Changed;

    public bool StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_packaging || _recorder?.Start() != true) return false;
            _recordingObservation ??= BeginObservation();
            return true;
        }
    }

    public IDisposable BeginObservation() => OverlayEditorKit.OverlayEditorLiveDiagnostics.BeginDiagnosticCollection();

    public Task StopRecordingAsync() => _recorder?.StopAsync() ?? Task.CompletedTask;

    // Compatibility for existing callers. New UI awaits CheckAsync on the selected features.
    public void Check() => CheckAsync(Features.Select(feature => feature.Id)).GetAwaiter().GetResult();
    public string CreatePackage() => CreatePackageAsync(Features.Select(feature => feature.Id)).GetAwaiter().GetResult();

    public async Task CheckAsync(IEnumerable<string> selected, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ids = ValidateSelection(selected);
        long request;
        lock (_gate) request = ++_request;
        var results = await CollectAsync(ids, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (await PublishAsync(FormatReport(ids, results), results, request, cancellationToken).ConfigureAwait(false)) Notify();
    }

    private async Task BackgroundCheckAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _backgroundChecking, 1) != 0) return;
        try
        {
            // Only quick infrastructure snapshots. Never overwrite a user's selected report.
            string[] selected = ["system", "network", "capture"];
            var results = await CollectAsync(selected, CancellationToken.None).ConfigureAwait(false);
            if (_disposed) return;
            Volatile.Write(ref _backgroundResults, results);
            if (Volatile.Read(ref _request) == 0)
            {
                await PublishAsync(FormatReport(selected, results), results, 0, CancellationToken.None).ConfigureAwait(false);
            }
            Notify();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally { Volatile.Write(ref _backgroundChecking, 0); }
    }

    private static string[] ValidateSelection(IEnumerable<string> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var ids = selected.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) throw new ArgumentException("Select at least one diagnostic feature.", nameof(selected));
        _ = DiagnosticFeatureCatalog.Resolve(ids);
        return ids;
    }

    private async Task<IReadOnlyList<DiagnosticResult>> CollectAsync(string[] selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var features = DiagnosticFeatureCatalog.Resolve(selected);
        var tasks = features.Select(feature => CollectOneAsync(feature.Id, token)).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private async Task<DiagnosticResult> CollectOneAsync(string id, CancellationToken token)
    {
        var contributor = _contributors.FirstOrDefault(item => item.FeatureIds.Contains(id));
        if (contributor is null) return Failure(id, DiagnosticStatus.Unavailable, "Für diese Funktion ist kein Diagnosebeitrag verfügbar.", "missing-contributor");
        Task<DiagnosticResult> task;
        lock (_gate)
        {
            if (!_inflight.TryGetValue(id, out task!) || task.IsCompleted)
                _inflight[id] = task = Task.Run(() => Freeze(contributor.Collect(id), id));
        }
        try { return await task.WaitAsync(_timeout, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var failure = Failure(id, DiagnosticStatus.Unavailable, "Die Prüfung antwortet nicht rechtzeitig.", "timeout");
            if (id != "capture") return failure;
            // Driver/OS support probes may hang while the independent capture snapshot remains readable.
            var capture = DiagnosticContributors.CaptureSnapshot(_capture.Diagnostics,
                _settings.Current.MapTrackingActive, _capture.BorderSuppressed, DateTimeOffset.UtcNow);
            var facts = capture.Facts.Concat(failure.Facts).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return failure with { Facts = new ReadOnlyDictionary<string, object?>(facts) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure(id, DiagnosticStatus.Error, "Die Prüfung ist fehlgeschlagen.", "contributor-failed"); }
    }

    private static DiagnosticResult Freeze(DiagnosticResult result, string id)
    {
        if (result.FeatureId != id || !Enum.IsDefined(result.Status)) throw new InvalidOperationException("Invalid diagnostic result.");
        // Explicit contributors own the field allowlists; preserve only bounded scalar facts.
        var facts = result.Facts.Take(128).ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            double number when !double.IsFinite(number) => null,
            float number when !float.IsFinite(number) => null,
            bool or byte or short or int or long or ushort or uint or ulong or float or double or decimal => pair.Value,
            DateTimeOffset at => at.ToUniversalTime(),
            DateTime at => at.ToUniversalTime(),
            Enum value => value.ToString(),
            string value when value.Length <= 256 => value,
            _ => null
        }, StringComparer.Ordinal);
        return result with { Facts = new ReadOnlyDictionary<string, object?>(facts) };
    }

    private static DiagnosticResult Failure(string id, DiagnosticStatus status, string summary, string reason) =>
        new(id, status, DateTimeOffset.UtcNow, summary, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["Reason"] = reason }));

    public Task<string> CreatePackageAsync(IEnumerable<string> selected, CancellationToken cancellationToken = default) =>
        CreatePackageAtAsync(selected, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), cancellationToken);

    internal async Task<string> CreatePackageAtAsync(IEnumerable<string> selected, string directory, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_packaging) throw new InvalidOperationException("Ein Diagnose-Paket wird bereits erstellt.");
            _packaging = true;
        }
        try { return await CreatePackageCoreAsync(selected, directory, cancellationToken).ConfigureAwait(false); }
        finally { lock (_gate) _packaging = false; }
    }

    private async Task<string> CreatePackageCoreAsync(IEnumerable<string> selected, string directory, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ids = ValidateSelection(selected);
        if (Recording.State is NetworkDiagnosticRecorderState.Recording or NetworkDiagnosticRecorderState.Stopping)
            throw new InvalidOperationException("Beende die Aufnahme, bevor du das Diagnose-Paket erstellst.");
        long request;
        lock (_gate) request = ++_request;
        // An explicitly completed recording is a requested attachment even for a non-network feature.
        var checks = Recording.StartedAt is not null ? ids.Append("network").Distinct(StringComparer.Ordinal).ToArray() : ids;
        var results = await CollectAsync(checks, cancellationToken).ConfigureAwait(false);
        var report = FormatReport(ids, results);
        var includesNetwork = results.Any(result => result.FeatureId == "network");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Soulcrest-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var partial = path + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var attachments = new List<string> { "diagnose.txt", "settings.json", "manifest.json" };
                await WriteEntryAsync(zip, "diagnose.txt", report, cancellationToken).ConfigureAwait(false);
                foreach (var result in results)
                {
                    await WriteEntryAsync(zip, $"features/{result.FeatureId}.json", JsonSerializer.Serialize(result, JsonOptions), cancellationToken).ConfigureAwait(false);
                    attachments.Add($"features/{result.FeatureId}.json");
                }
                var settings = new Dictionary<string, object?>();
                if (includesNetwork) settings["LootTrackingEnabled"] = _settings.Current.LootTrackingEnabled;
                if (results.Any(result => result.FeatureId == "system")) settings["UiLanguage"] = _settings.Current.UiLanguage is "de" or "en" ? _settings.Current.UiLanguage : "auto";
                await WriteEntryAsync(zip, "settings.json", JsonSerializer.Serialize(settings, JsonOptions), cancellationToken).ConfigureAwait(false);
                if (results.Any(result => result.FeatureId is "map" or "capture")) attachments.Add(AddPreview(zip, "karten-bereich", _map.PreviewDataUrl));
                if (includesNetwork && _recorder is not null)
                    await _recorder.WriteToArchiveAsync(zip, cancellationToken).ConfigureAwait(false);
                var recording = includesNetwork ? Recording : null;
                if (recording is not null) attachments.AddRange(recording.Artifacts.Select(artifact => "network/" + artifact.Name));
                var manifest = new
                {
                    SchemaVersion = 1, Version = AppPaths.Version, CreatedAtUtc = DateTimeOffset.UtcNow,
                    SelectedFeatures = ids,
                    IncludedFeatures = results.Select(result => result.FeatureId).ToArray(),
                    AdditionalSources = results.Select(result => result.FeatureId).Except(ids).ToArray(),
                    Checks = results.Select(result => new { result.FeatureId, result.Status, result.CheckedAt }).ToArray(),
                    Recording = recording,
                    Attachments = attachments,
                    TextData = "Allowlisted anonymous diagnostic projections; no raw logs or complete settings.",
                    RawCapture = "Optional unchanged game traffic; may contain sensitive game data. Local manual sharing only."
                };
                await WriteEntryAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, path);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
        try
        {
            if (await PublishAsync(report, results, request, cancellationToken).ConfigureAwait(false)) Notify();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A complete ZIP remains usable even when the separate local report cannot be replaced.
            lock (_gate)
                if (!_disposed && request == _request) Volatile.Write(ref _results, results);
            Notify();
        }
        return path;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken token)
    {
        await using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
    }

    private async Task<bool> PublishAsync(string report, IReadOnlyList<DiagnosticResult> results, long request, CancellationToken token)
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        var temporary = ReportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, report, Encoding.UTF8, token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || request != _request) return false;
                File.Move(temporary, ReportPath, overwrite: true);
                Volatile.Write(ref _results, results);
                return true;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string FormatReport(string[] selected, IReadOnlyList<DiagnosticResult> results)
    {
        var report = new StringBuilder();
        report.AppendLine($"Soulcrest-Diagnose {DateTimeOffset.UtcNow:O}");
        report.AppendLine($"Version: {AppPaths.Version}");
        report.AppendLine("SelectedFeatures: " + string.Join(", ", selected));
        foreach (var result in results)
        {
            report.AppendLine($"\n[{result.FeatureId}] {result.Status}; CheckedAtUtc: {result.CheckedAt.ToUniversalTime():O}");
            report.AppendLine(result.Summary);
            foreach (var (key, value) in result.Facts)
                report.AppendLine($"{key}: {FormatValue(value)}");
        }
        return report.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "unknown", DateTimeOffset at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown"
    };

    internal string CaptureReport()
    {
        var capture = _capture.Diagnostics;
        return $"Soulcrest-Aufnahmestatus {DateTimeOffset.UtcNow:O}\nVersion: {AppPaths.Version}\n" +
            $"Methode: {DiagnosticSupportProbe.CaptureMethod(capture.Method)}\nSchritt: {DiagnosticSupportProbe.CaptureStage(capture.Stage)}\nSchritt seit (UTC): {capture.StageSince:O}\n" +
            $"WGC-Bilder: {capture.FramesArrived}\nAbrufe: {capture.Grabs}\nGPU-bedingt übersprungene Bilder: {capture.BusyFrames}\n" +
            $"Netzwerkfortschritt: {_network?.ProgressIsLive == true}\n" +
            $"Pet-Baseline: {_network?.PetBaselineAt:O}\nQuest-Baseline: {_network?.QuestBaselineAt:O}\nTeleport-Baseline: {_network?.TeleportBaselineAt:O}\n" +
            FormatNetworkProgressDiagnostics(_network?.ProgressDiagnostics) + FormatAchievementIngressDiagnostics(_network?.AchievementDiagnostics);
    }

    private void Notify()
    {
        if (Recording.State is not (NetworkDiagnosticRecorderState.Recording or NetworkDiagnosticRecorderState.Stopping))
            Interlocked.Exchange(ref _recordingObservation, null)?.Dispose();
        if (!_disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        Interlocked.Exchange(ref _recordingObservation, null)?.Dispose();
        if (_recorder is not null) _recorder.Changed -= Notify;
    }

    internal static string FormatKinaDiagnostics(NetworkKinaSnapshot? wallet, NetworkDailyMonsterKinaSnapshot? daily)
    {
        static string Value(long? amount) => amount?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        // Only totals and freshness; no native identity, stack key, character name or endpoint.
        return $"[Monsterloot-Netzwerkdiagnose]\nWalletReady: {wallet?.IsReady == true}; WalletRejected: {wallet?.FormatRejected == true}\n" +
            $"WalletUnbound: {Value(wallet?.UnboundBalance)}; WalletBound: {Value(wallet?.BoundBalance)}\n" +
            $"DailyReady: {daily?.IsReady == true}; DailyMonsterKina: {Value(daily?.Total)}; DailyObservedAtUtc: {daily?.At?.ToUniversalTime():O}\n";
    }

    /// <summary>Formats one atomic counter snapshot; arbitrary strings cannot enter this diagnostic block.</summary>
    internal static string FormatNetworkProgressDiagnostics(NetworkProgressDiagnostics? snapshot)
    {
        if (snapshot is null) return "[Anonyme Netzwerkdiagnose]\nNicht verfügbar.\n";
        static string Reason(string? value) => value is "none" or "manual" or "capture-closed" or "capture-stopped"
            or "stream-gap" or "world-entry-preparation" or "world-entry" or "unsupported-own-entry" or "identity-bind-rejected"
            or "tcp-start" or "tcp-pending-overflow" or "tcp-reset" or "tcp-finished" or "stream-limit"
            or "framing-gap" or "ownership-removed" or "incoming-dropped"
            or "capture-handle-error" or "capture-handle-limit"
            or "world-entry-candidate" or "entry-candidate-expired" or "duplicate-entry-baseline" or "unsupported-entry-baseline"
            or "pet-continuity-expired" or "pet-continuity-unread"
            ? value : "unknown";
        var reason = Reason(snapshot.LastResetReason);
        static string Utc(DateTimeOffset? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "none";
        static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var opcode = snapshot.LastRejectedOpcode is { } code ? $"0x{code:X4}" : "none";
        var result = FormattableString.Invariant($"""
            [Anonyme Netzwerkdiagnose]
            IdentityBound: {snapshot.IdentityBound.ToString().ToLowerInvariant()}; IdentityBindings: {snapshot.IdentityBindings}
            ContextResets: {snapshot.ContextResets}; LastResetReason: {reason}; LastResetAtUtc: {Utc(snapshot.LastResetAt)}
            RejectedProgressMessages: {snapshot.RejectedProgressMessages}; LastRejectedOpcode: {opcode}; LastRejectedAtUtc: {Utc(snapshot.LastRejectedAt)}
            AcceptedSoulMessages: {snapshot.AcceptedSoulMessages}; AcceptedSoulPoints: {snapshot.AcceptedSoulPoints}; LastSoulAtUtc: {Utc(snapshot.LastSoulAt)}
            PendingMessages: {snapshot.PendingMessages}; PendingBytes: {snapshot.PendingBytes}; PendingOverflow: {snapshot.PendingOverflow.ToString().ToLowerInvariant()}
            UnboundSoulMessages: {snapshot.UnboundSoulMessages}; UnboundSoulPoints: {snapshot.UnboundSoulPoints}
            """) + "\n";
        var events = snapshot.Events.TakeLast(128).ToArray();
        result += FormattableString.Invariant($"[Letzte anonyme Netzwerkereignisse: {events.Length}]\n");
        foreach (var item in events)
        {
            var kind = Enum.IsDefined(item.Kind) ? item.Kind.ToString() : "unknown";
            var eventOpcode = item.Opcode is { } eventCode ? $"0x{eventCode:X4}" : "none";
            result += FormattableString.Invariant($"{Utc(item.At)} Kind:{kind}; Opcode:{eventOpcode}; Pet:{Number(item.PetNumber)}; Level:{Number(item.Level)}; Souls:{Number(item.Souls)}; Count:{Number(item.Count)}; Reason:{Reason(item.ResetReason ?? "none")}\n");
        }
        return result;
    }

    /// <summary>Only fixed counters and enum states; no names, native IDs, payloads or endpoints.</summary>
    internal static string FormatAchievementIngressDiagnostics(AchievementIngressDiagnostics? snapshot)
    {
        if (snapshot is null) return "[Anonyme Achievement-Eingangsdiagnose]\nNicht verfügbar.\n";
        var status = Enum.IsDefined(snapshot.SourceStatus) ? snapshot.SourceStatus.ToString() : "Unknown";
        return FormattableString.Invariant($"""
            [Anonyme Achievement-Eingangsdiagnose]
            Entry3620Framed: {snapshot.Entry3620Framed}; Entry3620Parsed: {snapshot.Entry3620Parsed}
            Preparation3621Framed: {snapshot.Own3621Framed}; Preparation3621Parsed: {snapshot.Own3621Parsed}
            Own3633Framed: {snapshot.Own3633Framed}; Own3633Parsed: {snapshot.Own3633Parsed}; OwnBindings: {snapshot.OwnBindings}
            Lists5683Framed: {snapshot.Lists5683Framed}; Updates5684Framed: {snapshot.Updates5684Framed}; Rewards5686Framed: {snapshot.Rewards5686Framed}
            AcceptedBaselines: {snapshot.AcceptedBaselines}; AcceptedUpdates: {snapshot.AcceptedUpdates}; AcceptedRewards: {snapshot.AcceptedRewards}
            Invalidations: {snapshot.Invalidations}; QueueOverflows: {snapshot.QueueOverflows}; PendingNotifications: {snapshot.PendingNotifications}; SubscriberFailures: {snapshot.SubscriberFailures}
            FramingResyncs: {snapshot.FramingResyncs}; CharacterBound: {snapshot.CharacterBound}; SourceFresh: {snapshot.SourceFresh}; SourceStatus: {status}
            """) + "\n";
    }

    internal static void AddReport(ZipArchive zip, string captureReport, string? fullReport, string? error)
    {
        using var writer = new StreamWriter(zip.CreateEntry("diagnose.txt").Open(), Encoding.UTF8);
        writer.WriteLine(captureReport);
        writer.WriteLine("[Letzter abgeschlossener Gesamtbericht]");
        writer.WriteLine(fullReport ?? "Noch kein Gesamtbericht abgeschlossen. Der Aufnahmestatus oben ist davon unabhaengig.");
        if (error is not null) writer.WriteLine("Fehler der erweiterten Pruefung: " + error);
    }

    /// <summary>A kept picture (JPEG bytes), or a note that there is none yet.</summary>
    internal static void AddPicture(ZipArchive zip, string name, byte[]? jpeg)
    {
        if (jpeg is { Length: > 0 })
        {
            using var entry = zip.CreateEntry($"bilder/{name}.jpg").Open();
            entry.Write(jpeg);
            return;
        }
        using var note = new StreamWriter(zip.CreateEntry($"bilder/{name}-fehlt.txt").Open());
        note.Write("Seit dem Start von Soulcrest wurde kein Bild gespeichert.");
    }

    internal static string AddPreview(ZipArchive zip, string name, string? dataUrl)
    {
        if (dataUrl is not null && (dataUrl.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)))
        {
            try
            {
                var bytes = Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
                var path = $"bilder/{name}.{(dataUrl.StartsWith("data:image/png", StringComparison.Ordinal) ? "png" : "jpg")}";
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(bytes);
                return path;
            }
            catch (FormatException) { }
        }
        using var note = new StreamWriter(zip.CreateEntry($"bilder/{name}-fehlt.txt").Open());
        note.Write("Noch keine gültige Vorschau vorhanden. Für die Diagnose wurde keine neue Aufnahme gestartet.");
        return $"bilder/{name}-fehlt.txt";
    }

}
