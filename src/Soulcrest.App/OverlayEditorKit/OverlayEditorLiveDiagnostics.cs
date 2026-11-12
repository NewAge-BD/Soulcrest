using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.OverlayEditorKit;

[Flags]
public enum OverlayLiveFailedGates
{
    None = 0, Disabled = 1, Empty = 2, GlobalForeground = 4, GameUnavailable = 8,
    SessionVisibility = 16, InvalidRules = 32, Area = 64, ForegroundRule = 128,
    SessionRule = 256, WorldMapRule = 512, EscMenuRule = 1024, MinimapRule = 2048,
    GenusUnavailable = 4096,
}

[Flags]
public enum OverlayLiveAuthorityChanges
{
    None = 0, SessionIdChanged = 1, SessionActiveChanged = 2, TrackingChanged = 4,
    AbyssProfileChanged = 8, BindingChanged = 16, WorldVisibleChanged = 32,
    GenusBindingChanged = 64, GenusMenuChanged = 128,
}

public sealed record OverlayLivePolicyInputs(bool Enabled, int WidgetCount, string Visibility,
    bool GameAvailable, bool GameForeground, bool AllowBackground, OverlayTriggerArea? Area,
    bool TrackingAvailable, bool SessionActive, bool? ObservedSessionActive,
    bool? WorldMap, bool? EscMenu, bool? MinimapMissing, bool HasRules, bool RulesValid, int AreasMask,
    OverlayTriggerRequirement ForegroundRule, OverlayTriggerRequirement SessionRule,
    OverlayTriggerRequirement WorldMapRule, OverlayTriggerRequirement EscMenuRule, OverlayTriggerRequirement MinimapRule,
    bool Result, OverlayLiveFailedGates FailedGates, bool CaptureExcluded = true);

public sealed record OverlayLiveDiagnosticEntry(long Sequence, double AtMs, int? Window,
    string Action, string Reason, string Phase, long? Epoch = null, OverlayLivePolicyInputs? Policy = null,
    OverlayLiveAuthorityChanges AuthorityChanges = OverlayLiveAuthorityChanges.None,
    string? ExceptionType = null, int? HResult = null);

/// <summary>Accepted render inputs and handoff times; availability flags do not inspect native pixels.</summary>
public sealed record OverlayLiveFrameObservation(int Window, long Started, long Captured, long Presented,
    double? LastStartedAtMs = null, double? LastCapturedAtMs = null, double? LastPresentedAtMs = null,
    long? LastPresentedEpoch = null, IReadOnlyDictionary<string, bool>? PresentedRankInputsKnown = null);

public sealed record OverlayLiveDiagnosticSnapshot(long Version, IReadOnlyList<OverlayLiveDiagnosticEntry> Entries, double NowMs = 0)
{
    public IReadOnlyList<OverlayLiveFrameObservation> Frames { get; init; } = [];
}

/// <summary>Opt-in, anonymous, bounded RAM diagnostics. No source reads, file writes or game identifiers.</summary>
public static class OverlayEditorLiveDiagnostics
{
    private static readonly OverlayLiveDiagnosticBuffer Buffer = new();
    private static int _diagnosticLeases;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    public static bool Enabled => Volatile.Read(ref _diagnosticLeases) > 0
        || ValidPort(Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT"));
    internal static bool ValidPort(string? value) => int.TryParse(value, out var port) && port is > 1024 and < 65536;
    public static OverlayLiveDiagnosticSnapshot Capture() => Enabled ? Buffer.Capture() : new(0, []);
    /// <summary>Reads retained anonymous observations without enabling collection or starting a renderer.</summary>
    public static OverlayLiveDiagnosticSnapshot CaptureRecorded() => Buffer.Capture();

    /// <summary>User-started diagnosis can observe the existing runtime without opening a developer port.</summary>
    public static IDisposable BeginDiagnosticCollection()
    {
        Interlocked.Increment(ref _diagnosticLeases);
        return new DiagnosticLease();
    }

    private sealed class DiagnosticLease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _diagnosticLeases);
        }
    }
    public static string Serialize(OverlayLiveDiagnosticSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    internal static void Policy(OverlaySettings settings, OverlaySnapshot snapshot, OverlayEditorLiveContext context, bool result)
    {
        if (Enabled) Buffer.Policy(settings, Inputs(settings, snapshot, context, result));
    }

    internal static void Lifecycle(OverlaySettings? settings, string action, string reason, string phase, long epoch,
        OverlayLiveAuthorityChanges changes = OverlayLiveAuthorityChanges.None)
    {
        if (Enabled) Buffer.Add(settings, action, reason, phase, epoch, changes);
    }

    internal static void Error(OverlaySettings? settings, Exception error, string phase, long epoch)
    {
        if (Enabled) Buffer.Add(settings, "error", "exception", phase, epoch, error: error);
    }

    internal static void FrameStarted(OverlaySettings settings)
    {
        if (Enabled) Buffer.FrameStarted(settings);
    }

    internal static void FrameCaptured(OverlaySettings settings)
    {
        if (Enabled) Buffer.FrameCaptured(settings);
    }

    internal static void FramePresented(OverlaySettings settings, OverlaySnapshot snapshot, long epoch)
    {
        if (Enabled) Buffer.FramePresented(settings, snapshot, epoch);
    }

    internal static OverlayLiveAuthorityChanges AuthorityChanges(OverlaySnapshot previous, OverlaySnapshot current)
    {
        var changes = OverlayLiveAuthorityChanges.None;
        if (previous.SessionId != current.SessionId) changes |= OverlayLiveAuthorityChanges.SessionIdChanged;
        if (previous.SessionActive != current.SessionActive) changes |= OverlayLiveAuthorityChanges.SessionActiveChanged;
        if (previous.TrackingAvailable != current.TrackingAvailable) changes |= OverlayLiveAuthorityChanges.TrackingChanged;
        if (previous.Abyss.ProfileId != current.Abyss.ProfileId) changes |= OverlayLiveAuthorityChanges.AbyssProfileChanged;
        if (previous.Abyss.BindingGeneration != current.Abyss.BindingGeneration) changes |= OverlayLiveAuthorityChanges.BindingChanged;
        if (previous.Abyss.WorldContextVisible != current.Abyss.WorldContextVisible) changes |= OverlayLiveAuthorityChanges.WorldVisibleChanged;
        if (previous.GenusInsight.ProfileId != current.GenusInsight.ProfileId || previous.GenusInsight.Generation != current.GenusInsight.Generation
            || previous.GenusInsight.IsCurrent != current.GenusInsight.IsCurrent) changes |= OverlayLiveAuthorityChanges.GenusBindingChanged;
        if (previous.GenusInsight.MenuVisible != current.GenusInsight.MenuVisible || previous.GenusInsight.SelectedGenus != current.GenusInsight.SelectedGenus
            || previous.GenusInsight.ViewedPreset != current.GenusInsight.ViewedPreset || previous.GenusInsight.MenuSourceGeneration != current.GenusInsight.MenuSourceGeneration
            || GenusInsightOverlayContent.Available(previous.GenusInsight) != GenusInsightOverlayContent.Available(current.GenusInsight))
            changes |= OverlayLiveAuthorityChanges.GenusMenuChanged;
        return changes;
    }

    internal static OverlayLivePolicyInputs Inputs(OverlaySettings settings, OverlaySnapshot snapshot, OverlayEditorLiveContext context, bool result)
    {
        var rules = settings.TriggerRules;
        var valid = rules is null || OverlayTriggerRules.Valid(rules);
        var session = snapshot.TrackingAvailable ? snapshot.SessionActive : (bool?)null;
        var failed = OverlayLiveFailedGates.None;
        if (!settings.Enabled) failed |= OverlayLiveFailedGates.Disabled;
        if (settings.Widgets.Count == 0) failed |= OverlayLiveFailedGates.Empty;
        if (!context.AllowBackground && !context.GameForeground) failed |= OverlayLiveFailedGates.GlobalForeground;
        if (settings.Visibility != "always" && !context.GameAvailable) failed |= OverlayLiveFailedGates.GameUnavailable;
        if (settings.Visibility == "session" && (!snapshot.TrackingAvailable || !snapshot.SessionActive)) failed |= OverlayLiveFailedGates.SessionVisibility;
        if (GenusInsightOverlayContent.Dedicated(settings) && !GenusInsightOverlayContent.Available(snapshot.GenusInsight)) failed |= OverlayLiveFailedGates.GenusUnavailable;
        if (!valid) failed |= OverlayLiveFailedGates.InvalidRules;
        if (rules is not null && valid)
        {
            if (rules.Areas.Count != 0 && (context.Area is not { } area || !Enum.IsDefined(area) || !rules.Areas.Contains(area))) failed |= OverlayLiveFailedGates.Area;
            if (!Matches(rules.Foreground, context.GameForeground)) failed |= OverlayLiveFailedGates.ForegroundRule;
            if (!Matches(rules.SessionActive, session)) failed |= OverlayLiveFailedGates.SessionRule;
            if (!Matches(rules.WorldMap, context.Visual.WorldMap)) failed |= OverlayLiveFailedGates.WorldMapRule;
            if (!Matches(rules.EscMenu, context.Visual.EscMenu)) failed |= OverlayLiveFailedGates.EscMenuRule;
            if (!Matches(rules.MinimapMissing, context.Visual.MinimapMissing)) failed |= OverlayLiveFailedGates.MinimapRule;
        }
        var areas = valid && rules is not null ? rules.Areas.Aggregate(0, (mask, area) => mask | (1 << (int)area)) : 0;
        return new(settings.Enabled, settings.Widgets.Count,
            settings.Visibility is "always" or "session" or "game" ? settings.Visibility : "other",
            context.GameAvailable, context.GameForeground, context.AllowBackground, context.Area,
            snapshot.TrackingAvailable, snapshot.SessionActive, session, context.Visual.WorldMap, context.Visual.EscMenu,
            context.Visual.MinimapMissing, rules is not null, valid, areas,
            rules?.Foreground ?? default, rules?.SessionActive ?? default, rules?.WorldMap ?? default,
            rules?.EscMenu ?? default, rules?.MinimapMissing ?? default, result, failed, settings.CaptureExcluded);
    }

    private static bool Matches(OverlayTriggerRequirement rule, bool? observed) => rule switch
    {
        OverlayTriggerRequirement.Any => true, OverlayTriggerRequirement.Required => observed == true,
        OverlayTriggerRequirement.Excluded => observed == false, _ => false,
    };
}

internal sealed class OverlayLiveDiagnosticBuffer
{
    private sealed class WindowState(int ordinal) { internal int Ordinal { get; } = ordinal; internal OverlayLivePolicyInputs? Policy { get; set; } }
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<OverlaySettings, WindowState> _windows = new();
    private readonly OverlayLiveDiagnosticEntry?[] _entries = new OverlayLiveDiagnosticEntry[128];
    private readonly Dictionary<int, OverlayLiveFrameObservation> _frames = [];
    private readonly Func<double> _elapsed;
    private int _next, _count, _ordinal;
    private long _version;
    private double _lastTime;

    internal OverlayLiveDiagnosticBuffer(Func<double>? elapsed = null)
    {
        var start = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    internal void Policy(OverlaySettings settings, OverlayLivePolicyInputs inputs)
    {
        lock (_gate)
        {
            var state = Window(settings);
            if (state.Policy == inputs) return;
            state.Policy = inputs;
            Append(new(0, 0, state.Ordinal, "policy", "evaluation", "policy", Policy: inputs));
        }
    }

    internal void Add(OverlaySettings? settings, string action, string reason, string phase, long epoch,
        OverlayLiveAuthorityChanges changes = OverlayLiveAuthorityChanges.None, Exception? error = null)
    {
        lock (_gate) Append(new(0, 0, settings is null ? null : Window(settings).Ordinal, action, reason, phase,
            epoch, AuthorityChanges: changes, ExceptionType: error?.GetType().Name, HResult: error?.HResult));
    }

    // Separate frame counters keep routine painting from evicting policy/lifecycle events.
    // Record only availability flags, never values, labels, source IDs or session identities.
    internal void FrameStarted(OverlaySettings settings)
    {
        lock (_gate)
        {
            var frame = Frame(settings);
            Store(frame with { Started = frame.Started + 1, LastStartedAtMs = Now() });
        }
    }

    internal void FrameCaptured(OverlaySettings settings)
    {
        lock (_gate)
        {
            var frame = Frame(settings);
            Store(frame with { Captured = frame.Captured + 1, LastCapturedAtMs = Now() });
        }
    }

    internal void FramePresented(OverlaySettings settings, OverlaySnapshot snapshot, long epoch)
    {
        lock (_gate)
        {
            var known = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var widget in settings.Widgets)
                if (widget.Kind is "rank-net" or "rank-gained" or "rank-lost" or "rank-net-hour" or "rank-gained-hour" or "rank-lost-hour")
                    known[widget.Kind] = snapshot.Metrics.TryGetValue(widget.Kind, out var metric)
                        && !string.IsNullOrWhiteSpace(metric.Value) && metric.Value != "—";
            var frame = Frame(settings);
            Store(frame with { Presented = frame.Presented + 1, LastPresentedAtMs = Now(), LastPresentedEpoch = epoch,
                PresentedRankInputsKnown = new System.Collections.ObjectModel.ReadOnlyDictionary<string, bool>(known) });
        }
    }

    private OverlayLiveFrameObservation Frame(OverlaySettings settings)
    {
        var ordinal = Window(settings).Ordinal;
        return _frames.GetValueOrDefault(ordinal) ?? new(ordinal, 0, 0, 0);
    }

    private void Store(OverlayLiveFrameObservation frame)
    {
        _frames[frame.Window] = frame;
        if (_frames.Count > 32)
            _frames.Remove(_frames.Values.MinBy(value => value.LastStartedAtMs ?? value.LastCapturedAtMs ?? value.LastPresentedAtMs ?? 0)!.Window);
        _version++;
    }

    private double Now() => _lastTime = Math.Max(_lastTime, _elapsed());

    internal OverlayLiveDiagnosticSnapshot Capture()
    {
        lock (_gate)
        {
            var entries = new OverlayLiveDiagnosticEntry[_count];
            for (var i = 0; i < _count; i++) entries[i] = _entries[(_next - _count + i + _entries.Length) % _entries.Length]!;
            _lastTime = Math.Max(_lastTime, _elapsed());
            return new(_version, Array.AsReadOnly(entries), _lastTime)
                { Frames = Array.AsReadOnly(_frames.Values.OrderBy(frame => frame.Window).ToArray()) };
        }
    }

    private WindowState Window(OverlaySettings settings) => _windows.GetValue(settings, _ => new(++_ordinal));
    private void Append(OverlayLiveDiagnosticEntry entry)
    {
        _lastTime = Math.Max(_lastTime, _elapsed());
        _entries[_next] = entry with { Sequence = ++_version, AtMs = _lastTime };
        _next = (_next + 1) % _entries.Length;
        _count = Math.Min(_count + 1, _entries.Length);
    }
}
