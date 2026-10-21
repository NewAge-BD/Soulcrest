using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.Ocr;
using Soulcrest.Ocr.MapTracking;

namespace Soulcrest.App.Services;

/// <summary>Player position in map coordinates (the marker coordinates of the data package).</summary>
public sealed record PlayerPosition(string MapId, double X, double Y, DateTimeOffset At)
{
    public long ContextGeneration { get; init; }
}

/// <summary>
/// Map identity from a successful image match. WorldMap identifies the open map, not the player's
/// location. At changes only on a verified match, never when optical flow moves an old placement.
/// </summary>
public sealed record DetectedMapContext(string MapId, bool WorldMap, DateTimeOffset At)
{
    public long ContextGeneration { get; init; }
}

/// <summary>
/// Where the reference map lies on screen right now: map coordinates -> screen pixels. Used by the
/// in-game route overlay to draw lines on the in-game map.
/// </summary>
/// <param name="FrameScale">Screen pixels per frame pixel (the world map is matched at half size).</param>
/// <param name="WorldMap">The full-screen world map is open: the player is not at the anchor there.</param>
/// <param name="PlayerWorld">On the world map: the last position from the small map (null when the
/// world map shows another zone).</param>
public sealed record MapPlacement(string MapId, MapFix Fix, double WorldPerPixel, Rectangle Region, Point2d Anchor,
    double FrameScale = 1, bool WorldMap = false, Point2d? PlayerWorld = null)
{
    /// <summary>When the screen picture this placement follows arrived (Stopwatch timestamp, 0 = unknown).</summary>
    public long CapturedAt { get; init; }
    public long ContextGeneration { get; init; }

    public PointF WorldToScreen(double x, double y)
    {
        var frame = Fix.ReferenceToFrame(new Point2d(x / WorldPerPixel, y / WorldPerPixel));
        return new PointF((float)(Region.X + frame.X * FrameScale), (float)(Region.Y + frame.Y * FrameScale));
    }

    public PointF? PlayerOnScreen => WorldMap
        ? PlayerWorld is { } p ? WorldToScreen(p.X, p.Y) : null
        : new PointF((float)(Region.X + Anchor.X * FrameScale), (float)(Region.Y + Anchor.Y * FrameScale));
}

/// <summary>
/// Tracks the player on the in-game map (technique of the Map Overlay project, re-implemented in C#,
/// docs/MAP_TRACKING.md): the user marks the screen area of the in-game map once; every frame of that
/// area is matched against the SIFT reference of a map, and the player marker's fixed place in the
/// area (anchor) gives the position. Passive: screen pixels only.
/// With network tracking enabled, only its supported own-map bridge is searched. The broad visual
/// search is available solely when the saved network switch is off.
/// </summary>
public sealed class MapTrackingService : IDisposable
{
    private readonly SettingsService settings;
    private readonly ProgressService progress;
    private readonly GameCaptureService capture;
    private readonly WorldContextService? _worldContext;
    private WorldContextSnapshot? _appliedWorldContext;
    private volatile bool _appliedNetworkEnabled;
    private long _appliedNetworkRevision;
    private long? _lastOwnBindingGeneration;

    public MapTrackingService(SettingsService settings, ProgressService progress, GameCaptureService capture,
        WorldContextService? worldContext = null)
    {
        this.settings = settings;
        this.progress = progress;
        this.capture = capture;
        _worldContext = worldContext;
        _appliedWorldContext = worldContext?.Snapshot;
        _appliedNetworkEnabled = settings.Current.LootTrackingEnabled;
        _appliedNetworkRevision = settings.NetworkTrackingRevision;
        _lastOwnBindingGeneration = _appliedWorldContext?.BindingGeneration;
        settings.Changed += SettingsChanged;
        if (worldContext is not null)
        {
            worldContext.Changed += WorldContextChanged;
            if (!ReferenceEquals(_appliedWorldContext, worldContext.Snapshot)) WorldContextChanged();
        }
    }

    public long ContextGeneration => Interlocked.Read(ref _mapContextGeneration);
    internal long? WorldContextBindingGeneration => settings.Current.LootTrackingEnabled ? _worldContext?.Snapshot.BindingGeneration : null;
    // Without location facts the network cannot confirm an area; visual tracking applies as with network off.
    private bool NetworkContextEnabled => settings.Current.LootTrackingEnabled && _worldContext?.HasLocationFacts != false;
    public MapTrackingMode Mode => EffectiveFeaturePolicy.TrackingMode(NetworkContextEnabled, _worldContext?.Snapshot);
    public bool UsesVisualFallback => Mode == MapTrackingMode.VisualFallback;
    public bool NetworkSuspended => Mode == MapTrackingMode.NetworkSuspended;
    public bool? NetworkInInstance => NetworkContextEnabled ? EffectiveFeaturePolicy.ConfirmedInstance(_worldContext?.Snapshot) : null;
    public bool CanUsePlayerPosition
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var generation = ContextGeneration;
            return Running && Found && !Paused && !WorldMapOpen && !EscMenuOpen && !InInstance
                && Position is { } position && position.ContextGeneration == generation
                && position.At <= now && now - position.At <= TimeSpan.FromSeconds(2)
                && DetectedMap is { WorldMap: false } detected && detected.MapId == position.MapId
                && detected.ContextGeneration == generation && detected.At <= now
                && now - detected.At <= TimeSpan.FromSeconds(5)
                && EffectiveFeaturePolicy.AllowsLiveMap(NetworkContextEnabled, _worldContext?.Snapshot, position.MapId)
                && IsCurrentVisualContext(generation);
        }
    }

    internal bool IsCurrentVisualContext(long generation)
    {
        var revision = settings.NetworkTrackingRevision;
        var networkEnabled = settings.Current.LootTrackingEnabled;
        var source = _worldContext?.Snapshot;
        return revision == Interlocked.Read(ref _appliedNetworkRevision) && networkEnabled == _appliedNetworkEnabled
            && EffectiveFeaturePolicy.TrackingMode(networkEnabled && _worldContext?.HasLocationFacts != false, source)
                != MapTrackingMode.NetworkSuspended
            && (!networkEnabled || ReferenceEquals(source, Volatile.Read(ref _appliedWorldContext)))
            && generation == ContextGeneration && networkEnabled == settings.Current.LootTrackingEnabled
            && revision == settings.NetworkTrackingRevision
            && (!networkEnabled || ReferenceEquals(source, _worldContext?.Snapshot));
    }

    public bool IsCurrentProgressionContext(long? generation) => generation is { } current
        ? IsCurrentVisualContext(current) : StandaloneVisualFallback;

    private bool StandaloneVisualFallback => _worldContext is null && UsesVisualFallback
        && IsCurrentVisualContext(ContextGeneration);

    public bool CanSelectProgression(string mapId)
    {
        // Legacy standalone map previews have no network context source. The application always
        // provides one and requires current, matching minimap evidence for automatic guidance.
        if (StandaloneVisualFallback) return true;
        return CanUsePlayerPosition && Position?.MapId == mapId;
    }

    internal bool TryArrive(MapTargetsService targets, string id, PlayerPosition position)
    {
        var changed = TryMutateNavigation(position.ContextGeneration, position.MapId, position,
            () => targets.ArriveWithoutNotification(id));
        // No source, map or arrival lock may be held while subscribers run (including Stop/Own).
        if (changed) targets.NotifyChanged();
        return changed;
    }

    internal bool TrySetProgression(MapTargetsService targets, MapTarget? target, string mapId, long? generation)
    {
        var changed = TryMutateNavigation(generation, mapId, null,
            () => targets.SetProgressionWithoutNotification(target));
        if (changed) targets.NotifyChanged();
        return changed;
    }

    private bool TryMutateNavigation(long? generation, string mapId, PlayerPosition? expectedPosition, Func<bool> mutation)
    {
        bool Apply()
        {
            lock (_flowGate)
            {
                if (!IsCurrentProgressionContext(generation) || !CanSelectProgression(mapId)
                    || expectedPosition is not null && (!CanUsePlayerPosition || !ReferenceEquals(Position, expectedPosition)))
                    return false;
                return mutation();
            }
        }
        var source = _worldContext?.Snapshot;
        // Lock order: World -> map -> targets. The mutation has no service callbacks.
        return source is null ? Apply() : _worldContext!.TryApplyWhileCurrent(source, Apply);
    }

    private void WorldContextChanged()
    {
        lock (_flowGate)
        {
            _preparationCancel?.Cancel();
            var source = _worldContext?.Snapshot;
            if (source?.Source == "own-world" && source.BindingGeneration != _lastOwnBindingGeneration)
            {
                _lastOwnBindingGeneration = source.BindingGeneration;
                _lastOwnEnteredAt = Stopwatch.GetTimestamp();
            }
            InvalidateMapContext();
            Found = false;
            Placement = null;
            _worldMapOpen = false;
            _escMenuOpen = false;
            _worldMisses = 0;
            _flowBroken = true;
            _lostSince = 0;
            _inInstance = UsesVisualFallback && _inInstance;
            Paused = NetworkSuspended || UsesVisualFallback && Paused;
            // Publication can precede this handler while another map operation holds _flowGate.
            // Readers reject that pending revision immediately, even before our visual epoch moves.
            Volatile.Write(ref _appliedWorldContext, source);
        }
        _wake.TrySetResult();
        SafeEvent.Raise(Changed, "Weltkontext");
        PlacementMoved?.Invoke();
    }

    private void SettingsChanged()
    {
        if (_appliedNetworkEnabled == settings.Current.LootTrackingEnabled
            && Interlocked.Read(ref _appliedNetworkRevision) == settings.NetworkTrackingRevision) return;
        lock (_flowGate)
        {
            if (_appliedNetworkEnabled == settings.Current.LootTrackingEnabled
                && Interlocked.Read(ref _appliedNetworkRevision) == settings.NetworkTrackingRevision) return;
            _preparationCancel?.Cancel();
            InvalidateMapContext();
            _appliedNetworkEnabled = settings.Current.LootTrackingEnabled;
            Interlocked.Exchange(ref _appliedNetworkRevision, settings.NetworkTrackingRevision);
            Found = false;
            Placement = null;
            _worldMapOpen = false;
            _escMenuOpen = false;
            _worldMisses = 0;
            _misses = 0;
            _flowBroken = true;
            _lostSince = 0;
            _inInstance = false;
            Paused = NetworkSuspended;
        }
        _wake.TrySetResult();
        SafeEvent.Raise(Changed, "Kartenquelle");
        PlacementMoved?.Invoke();
    }

    private const int IntervalMs = 250;
    // Standing still, the expensive detection (130-270 ms) runs less often: each quiet detection doubles
    // the gap up to 2 s. Any movement of the map picture, a lost flow, the world map or an unsure
    // position bring back the 250 ms (user request 2026-10-05; the network has no own position).
    private const int MaxIdleIntervalMs = 2000;
    private const double StillPixels = 1.5;

    /// <summary>
    /// Gap until the next detection: 250 ms while anything is unsure, up to 2 s standing still. On the open
    /// world map the flow carries the overlay while it is dragged, detection only corrects its drift once a
    /// second: at 250 ms the world map's detection (0.5-0.8 s each) ran without pause and took almost three
    /// cores (log 2026-10-07). Closing the world map breaks the flow, which brings back 250 ms at once.
    /// </summary>
    internal static int DetectionIntervalMs(bool found, bool worldMap, int misses, double motionPixels, bool flowLost, int stillRuns)
    {
        if (!found || misses > 0 || flowLost)
            return IntervalMs;
        if (motionPixels < StillPixels)
            return Math.Min(MaxIdleIntervalMs, IntervalMs << Math.Clamp(stillRuns, 0, 3));
        return worldMap ? WorldMapMovingIntervalMs : IntervalMs;
    }

    private const int WorldMapMovingIntervalMs = 1000;

    /// <summary>Current gap between two detections (diagnosis).</summary>
    public int DetectionInterval { get; private set; } = IntervalMs;
    private const int ReferenceZoomWidth = 4096; // reference image size, as in Map Overlay
    public const int MissesBeforeOtherMaps = 3;
    private const int HoldFrames = 4; // ~1 s at 4 frames per second
    // Past those, the last placement is held while the flow follows the minimap without a break, up to
    // 15 s: a strongly zoomed small minimap over coast and sand gave the detection 10-170 keypoints and lost
    // the position again and again until the tracking went to rest (colleague's diagnosis 2026-10-07).
    internal static readonly TimeSpan FlowHoldLimit = TimeSpan.FromSeconds(15);
    private volatile bool _flowBroken = true; // the flow could not follow since the last found position
    private long _lastFixAt;
    // Switching to another map needs clearly more evidence than staying: maps share decorations
    // (compass rose, emblems), a cut-out of one Abyss map reached 20 pairs on another one.
    private const int SwitchMinInliers = 30;

    private sealed record MapDefinition(string Id, string Label, string Tiles, int RefZoom, int Size, int MaxNativeZoom, int TileSize = 256)
    {
        /// <summary>Zoom of the ~4096 px reference used to find which map is shown.</summary>
        public int CoarseZoom => Math.Clamp(RefZoom - (int)Math.Round(Math.Log2(Size / (double)ReferenceZoomWidth)), 0, MaxNativeZoom);

        /// <summary>
        /// One level finer for the map the player is on: the in-game map shows about 3× the detail of the
        /// coarse reference; live capture 2026-10-03: 242 instead of 54 consistent pairs.
        /// </summary>
        public int FineZoom => Math.Min(MaxNativeZoom, CoarseZoom + 1);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, MapLocator> _coarse = [];
    private readonly Dictionary<string, MapLocator> _fine = [];
    private Task? _preparing;
    private CancellationTokenSource? _preparationCancel;
    private Task? _detecting;
    private CancellationTokenSource? _cancel;
    private Task? _loop;
    private string? _currentMap;
    private int _misses;
    private int _searchTick;
    private int _recoverySearchTick;
    private string? _confirmationMap;
    private readonly Dictionary<string, Task> _confirmationPreparations = [];
    private readonly Dictionary<string, Task> _worldPreparations = [];
    private double? _lastZoom;
    private readonly List<string> _recentMaps = [];

    // World map mode (user report 2026-10-03: opening the in-game world map scrambled the overlay; the
    // marked area then shows a piece of the world map, and its fixed anchor is not the player).
    // The world map is captured and matched at a quarter of the screen size (fast capture, enough detail).
    public const double WorldMapScale = 0.25;
    private readonly Dictionary<string, MapLocator> _world = [];
    private volatile bool _worldMapOpen;
    private Rectangle _worldScreen;
    private string? _worldMapId;
    private int _worldMisses;

    /// <summary>The in-game world map is open: the overlay follows it, the player position stays as it was.</summary>
    public bool WorldMapOpen => _worldMapOpen;

    private readonly EscMenuDetector _escMenu = new();
    private volatile bool _escMenuOpen;
    public bool EscMenuOpen => _escMenuOpen;

    internal void UpdateEscMenuContext(bool open, long? contextGeneration = null)
    {
        lock (_flowGate)
        {
            if (NetworkSuspended || contextGeneration is { } generation && !IsCurrentVisualContext(generation)) return;
            if (_escMenuOpen == open) return;
            _escMenuOpen = open;
            if (open)
            {
                InvalidateMapContext();
                Found = false;
                Placement = null;
                _flowBroken = true;
            }
        }
        SafeEvent.Raise(Changed, "ESC-Menü");
        if (open) PlacementMoved?.Invoke();
    }


    public event Action? Changed;

    /// <summary>The placement moved (optical flow, up to 60 times per second): redraw the in-game overlay.</summary>
    public event Action? PlacementMoved;

    /// <summary>Frames per second of the fast loop (capture + flow), capped at 60.</summary>
    public int Fps { get; private set; }

    private const double FrameMs = 1000.0 / 60;

    // User request 2026-10-15: react to fresh world-map pictures while preserving the loop budget and
    // wall-clock emblem/detection deadlines. Waiting never means an unavailable capture or broken flow.
    internal static double WorldMapFrameWaitMs(double iterationElapsedMs, double sinceEmblemMs,
        double sinceDetectionMs, int detectionIntervalMs) => Math.Max(0,
            Math.Min(FrameMs - iterationElapsedMs, Math.Min(EmblemCheckMs - sinceEmblemMs, detectionIntervalMs - sinceDetectionMs)));

    // Screen capture via Windows Graphics Capture of the game window (small map 0.5 ms, quarter-size
    // world map 5.6 ms; GDI: ~85 ms with the game running), shared with the other trackers.
    /// <summary>"Spielfenster (WGC)", "Monitor (WGC)" or "GDI": which capture the tracking uses.</summary>
    public string CaptureMethod => capture.MethodText;

    // Internal seams keep loop/cancellation regressions fully offline; production uses the shared capture.
    internal Func<Rectangle, double, Mat>? CaptureFrameForTests { get; set; }
    internal Func<Action, CancellationToken, Task>? ScheduleDetectionForTests { get; set; }
    internal Action<string, bool>? SearchStartedForTests { get; set; }
    internal Action? NetworkWaitStartedForTests { get; set; }

    private Mat? GrabCurrent(Rectangle area, double scale, long generation)
    {
        if (!IsCurrentVisualContext(generation) || _cancel?.IsCancellationRequested == true) return null;
        var overrideCapture = CaptureFrameForTests;
        return CaptureReadback.TryCapture(() => overrideCapture is null ? capture.Grab(area, scale) : overrideCapture(area, scale));
    }

    // User diagnosis 2026-10-12: monitor fallback repeatedly reported Abyss without a valid minimap context.
    // Only isolated game-window frames remain usable while another application has the focus.
    internal static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(2);

    internal bool AcceptCaptureContext(bool gameWindowOnly, bool gameForeground, TimeSpan frameAge)
    {
        var current = frameAge >= TimeSpan.Zero && frameAge <= MaximumFrameAge;
        if (current && (gameWindowOnly || gameForeground)) return true;
        // A first match can be running even when no position has been published yet.
        InvalidateMapContext();
        Found = false;
        DetectedMap = null;
        Placement = null;
        _worldMapOpen = false;
        _worldMisses = 0;
        _flowBroken = true;
        _lostSince = 0; // an unavailable/covered capture is not evidence of a dungeon
        Fps = 0;
        SetStatus(current
            ? "Warte auf das Spielfenster – Bildschirmaufnahme ist im Hintergrund nicht eindeutig."
            : "Kein aktuelles Spielbild – warte auf die Aufnahme.");
        return false;
    }

    private bool AcceptLastCapture()
    {
        var gameWindowOnly = GameCaptureService.GrabbedGameWindowOnly;
        var frameAt = GameCaptureService.GrabbedFrameAt;
        return AcceptCaptureContext(gameWindowOnly,
            !gameWindowOnly && string.Equals(UiResponsiveness.ForegroundProcess(), "AION2", StringComparison.OrdinalIgnoreCase),
            frameAt == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(frameAt));
    }
    private readonly object _flowGate = new();
    // A capture begun before a load or stop must never republish its old map as fresh evidence.
    private long _mapContextGeneration;

    private bool InvalidateMapContext()
    {
        lock (_flowGate)
        {
            Interlocked.Increment(ref _mapContextGeneration);
            var hadContext = DetectedMap is not null;
            DetectedMap = null;
            return hadContext;
        }
    }

    private bool IsCurrentMapContext(long generation) => IsCurrentVisualContext(generation);
    private FrameShift _drift = FrameShift.Identity;

    internal bool TryApplyFlowShift(FrameShift shift, long frameAt, long contextGeneration)
    {
        lock (_flowGate)
        {
            if (!IsCurrentMapContext(contextGeneration) || EscMenuOpen) return false;
            _drift = _drift.Then(shift);
            if (Placement is { } placement && placement.ContextGeneration == contextGeneration)
                Placement = placement with { Fix = shift.Apply(placement.Fix), CapturedAt = frameAt };
            _flowFrameAt = frameAt;
            return true;
        }
    }

    public bool Running { get; private set; }
    public string Status { get; private set; } = "Kartenbereich markieren, dann Tracking starten.";
    public bool StatusIsError { get; private set; }
    public PlayerPosition? Position { get; private set; }

    /// <summary>
    /// Last confirmed map identity for passive consumers (user request 2026-10-11). A briefly held
    /// match keeps its original timestamp; consumers decide freshness without starting tracking.
    /// Null after a loss, load, menu, instance or stop. LastMap is only a search preference.
    /// </summary>
    public DetectedMapContext? DetectedMap { get; private set; }

    /// <summary>Current placement of the map on screen; null while the map is not found.</summary>
    public MapPlacement? Placement { get; private set; }

    /// <summary>The map is currently found in the marked area.</summary>
    public bool Found { get; private set; }

    /// <summary>
    /// Network-suspended tracking waits only for source/settings events. The explicitly selected
    /// visual fallback retains the previous low-frequency recovery probes.
    /// </summary>
    public bool Paused { get; private set; }

    private volatile bool _inInstance;

    /// <summary>
    /// The network owns instance classification while enabled. With the saved switch off, prolonged
    /// minimap loss may supply the existing unconfirmed visual heuristic instead.
    /// </summary>
    public bool InInstance => UsesVisualFallback ? _inInstance : NetworkInInstance == true;

    /// <summary>No map this long after a loading screen: an instance. Without a loading screen seen (loot
    /// tracking off, or a long cutscene in the world) the tracking waits longer.</summary>
    internal static readonly TimeSpan PauseAfterLoadingScreen = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan PauseWithoutLoadingScreen = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Resting still avoids continuous capture/flow, but checks at least every 2 s even after long
    /// instances (user request 2026-10-19). Each look searches several ready maps, not just one.
    /// </summary>
    internal static int PausedProbeMs(TimeSpan pausedFor) => 2000;
    private long _pausedAt;
    private long _lostSince;        // Stopwatch timestamp of the first second without a map (0: found)
    private long _lastOwnEnteredAt; // accepted Own hint used only by the explicit visual fallback
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the tracking should rest: no map for a while, the world map closed.</summary>
    internal static bool ShouldPause(bool enabled, bool found, bool worldMap, TimeSpan lostFor, TimeSpan? sinceLoadingScreen)
    {
        if (!enabled || found || worldMap)
            return false;
        // The loading screen belongs to this loss when it came shortly before it began (the minimap is
        // gone during the load already).
        var afterLoading = sinceLoadingScreen is { } since && since <= lostFor + TimeSpan.FromSeconds(30);
        return lostFor >= (afterLoading ? PauseAfterLoadingScreen : PauseWithoutLoadingScreen);
    }

    internal void UpdateInstanceContext(bool found, bool worldMap, TimeSpan lostFor, TimeSpan? sinceLoadingScreen)
    {
        if (!UsesVisualFallback) return;
        // Keep the inferred context through a loading-screen wake-up until a map is confirmed.
        // Otherwise the overlays briefly reappear during every paused probe or return load.
        var inInstance = !found && !worldMap &&
            (_inInstance || ShouldPause(true, found, worldMap, lostFor, sinceLoadingScreen));
        if (_cancel?.IsCancellationRequested == true)
            inInstance = false;
        if (_inInstance == inInstance)
            return;
        _inInstance = inInstance;
        if (inInstance) DetectedMap = null;
        Changed?.Invoke();
    }

    private void Pause()
    {
        if (!Paused)
            _pausedAt = Stopwatch.GetTimestamp();
        Paused = true;
        Found = false;
        DetectedMap = null;
        Placement = null;
        Fps = 0;
        SetStatus(NetworkSuspended
            ? "Netzwerk-Tracking wartet auf ein unterstütztes Gebiet – Kartenprüfung ruht."
            : "Visueller Rückfall: keine Karte erkannt – prüft alle paar Sekunden.");
    }

    private void Resume(string? status)
    {
        Paused = false;
        _lostSince = Found ? 0 : Stopwatch.GetTimestamp(); // a fresh window before it rests again
        if (status is not null)
            SetStatus(status);
    }
    public int Inliers { get; private set; }
    public long LocateMs { get; private set; }

    /// <summary>Last captured map area as JPEG data URL (for setting the player anchor).</summary>
    public string? PreviewDataUrl { get; private set; }

    public Rectangle? Region => settings.Current.MapRegion;

    public void SelectRegion()
    {
        var main = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (main is { InvokeRequired: true })
        {
            main.Invoke(SelectRegion);
            return;
        }
        var previous = main?.WindowState ?? FormWindowState.Normal;
        if (main is not null)
        {
            main.WindowState = FormWindowState.Minimized;
            Thread.Sleep(350);
        }
        try
        {
            if (RegionSelectorForm.Select("Rahmen um die Karte im Spiel ziehen (Minimap oder geöffnete Karte, nur der Kartenausschnitt)") is { } region)
            {
                settings.Update(s => s.MapRegion = region);
                DetectedMap = null;
                lock (_gate)
                {
                    foreach (var locator in _coarse.Values.Concat(_fine.Values))
                        locator.Reset();
                }
                SetStatus($"Kartenbereich: {region.Width} × {region.Height} px.");
            }
        }
        finally
        {
            if (main is not null)
            {
                main.WindowState = previous == FormWindowState.Minimized ? FormWindowState.Normal : previous;
                main.Activate();
            }
        }
    }

    /// <summary>Sets where the player marker sits in the marked area (fractions of width and height).</summary>
    public void SetAnchor(double fx, double fy)
    {
        settings.Update(s =>
        {
            s.PlayerAnchorX = Math.Clamp(fx, 0, 1);
            s.PlayerAnchorY = Math.Clamp(fy, 0, 1);
        });
        Changed?.Invoke();
    }

    public void Start()
    {
        if (Running)
            return;
        if (Region is null)
        {
            SetStatus("Zuerst den Kartenbereich markieren.", error: true);
            return;
        }
        if (progress.MapDataDirectory is null)
        {
            SetStatus("Kein Kartendatenpaket gefunden.", error: true);
            return;
        }
        InvalidateMapContext();
        Running = true;
        // OpenCV spread the 60 fps flow over all 16 threads: 2–3 ms instead of 3.5 ms per frame, but three to
        // four times the CPU for their coordination (measured 2026-10-07, TrackingFrameCostExploration). Two
        // threads halve the flow's CPU; a detection takes ~190 instead of ~160 ms. Holds for the process.
        if (OpenCvThreads > 0)
            Cv2.SetNumThreads(OpenCvThreads);
        if (!settings.Current.MapTrackingActive)
            settings.Update(s => s.MapTrackingActive = true);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        SetStatus("Tracking läuft – Karte wird gesucht …");
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        Shutdown();
        if (settings.Current.MapTrackingActive)
            settings.Update(s => s.MapTrackingActive = false);
    }

    /// <summary>Stops without forgetting that tracking was on (app closing): it resumes on the next start.</summary>
    public void Shutdown()
    {
        _cancel?.Cancel();
        lock (_flowGate) _preparationCancel?.Cancel();
        InvalidateMapContext();
        Running = false;
        _escMenuOpen = false;
        Paused = false;
        _inInstance = false;
        _lostSince = 0;
        Found = false;
        DetectedMap = null;
        _worldMapOpen = false;
        Placement = null;
        SetStatus("Tracking gestoppt.");
    }

    /// <summary>
    /// Fast loop (at most 60 frames per second): capture the map area, carry the placement along by
    /// optical flow (a few ms), publish it for the in-game overlay. Every 250 ms one frame goes to the
    /// detection in the background (~100 ms); its result is moved on by the flow measured since that
    /// frame (drift), as in the Map Overlay.
    /// </summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private async Task WaitForContextChangeAsync(CancellationToken token, bool suspended)
    {
        Task wake;
        lock (_flowGate)
        {
            var pending = _appliedNetworkEnabled != settings.Current.LootTrackingEnabled
                || Interlocked.Read(ref _appliedNetworkRevision) != settings.NetworkTrackingRevision
                || settings.Current.LootTrackingEnabled && !ReferenceEquals(_appliedWorldContext, _worldContext?.Snapshot);
            if (suspended ? !NetworkSuspended : !pending) return;
            if (_wake.Task.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            wake = _wake.Task;
        }
        if (suspended) NetworkWaitStartedForTests?.Invoke();
        await wake.WaitAsync(token);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        await _runGate.WaitAsync(token);
        Task? detecting = null;
        using var flow = new MapFlow();
        var highResolutionTimer = false;
        void SetTimerResolution(bool active)
        {
            if (active == highResolutionTimer) return;
            if (active) _ = TimeBeginPeriod(1); else _ = TimeEndPeriod(1);
            highResolutionTimer = active;
        }
        try
        {
            var maps = LoadMaps();
            _lastZoom ??= settings.Current.MiniMapZoom;
            _currentMap ??= maps.Any(m => m.Id == settings.Current.LastMap) ? settings.Current.LastMap : maps.FirstOrDefault()?.Id;
            token.ThrowIfCancellationRequested();
            var preparedGeneration = -1L;
            var lastSubmit = 0L;
            var lastEmblemCheck = 0L;
            var emblemMisses = 0;
            double motion = 0;      // flow movement since the last detection (frame pixels)
            var flowLost = true;    // the flow could not follow since the last detection
            var stillRuns = 0;      // detections in a row without movement
            var frames = new Queue<long>();
            Rectangle? lastRegion = null;
            CaptureTiming previousMainCapture = default;
            long previousMainContextGeneration = -1;
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (NetworkSuspended)
                {
                    SetTimerResolution(false);
                    flow.Reset();
                    frames.Clear();
                    lastRegion = null;
                    previousMainCapture = default;
                    Pause();
                    await WaitForContextChangeAsync(token, suspended: true);
                    continue;
                }
                var visualGeneration = ContextGeneration;
                if (!IsCurrentVisualContext(visualGeneration))
                {
                    await WaitForContextChangeAsync(token, suspended: false);
                    continue;
                }
                if (preparedGeneration != visualGeneration && (_preparing is null || _preparing.IsCompleted))
                {
                    lock (_flowGate)
                    {
                        if (!IsCurrentVisualContext(visualGeneration)) continue;
                        _preparationCancel?.Dispose();
                        _preparationCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var preparationToken = _preparationCancel.Token;
                        var preparingMaps = Mode == MapTrackingMode.NetworkMap
                            ? maps.Where(m => m.Id == _worldContext?.Snapshot.SoulcrestMapId).ToList() : maps;
                        _preparing = Task.Run(() => Prepare(preparingMaps, preparationToken, visualGeneration), CancellationToken.None);
                        preparedGeneration = visualGeneration;
                    }
                }
                if (!UsesVisualFallback && Paused) Resume(null);
                if (Region is not { } region)
                {
                    await Task.Delay(IntervalMs, token);
                    continue;
                }
                if (UsesVisualFallback && Paused)
                {
                    SetTimerResolution(false);
                    // Resting in an instance: no 60 fps capture and flow, one look every few seconds.
                    flow.Reset();
                    frames.Clear();
                    lastRegion = null;
                    previousMainCapture = default;
                    if (!Found)
                    {
                        var wake = _wake.Task;
                        var woke = await Task.WhenAny(wake, Task.Delay(PausedProbeMs(Stopwatch.GetElapsedTime(_pausedAt)), token)) == wake;
                        token.ThrowIfCancellationRequested();
                        if (woke)
                        {
                            _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            Resume("Kartenkontext geändert – Kartenerkennung wird fortgesetzt.");
                            continue;
                        }
                        var screen = capture.GameBounds ?? Screen.FromRectangle(region).Bounds;
                        var pausedContextGeneration = Interlocked.Read(ref _mapContextGeneration);
                        using var full = GrabCurrent(screen, 1, pausedContextGeneration);
                        if (full is not null)
                        {
                            if (!AcceptLastCapture()) continue;
                            await Task.Run(() => ProcessMiniMapFrame(region, screen, full, token, pausedContextGeneration), token);
                        }
                    }
                    if (Found)
                        Resume(null);
                    else
                        Pause(); // the probe wrote its own status
                    continue;
                }
                SetTimerResolution(true);
                if (_wake.Task.IsCompleted)
                    _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var worldMap = _worldMapOpen;
                var area = worldMap ? _worldScreen : region;
                if (area != lastRegion)
                {
                    flow.Reset();
                    lastRegion = area;
                    previousMainCapture = default;
                }
                // The world map comes in at a quarter of its size already.
                var contextGeneration = Interlocked.Read(ref _mapContextGeneration);
                if (worldMap && previousMainCapture.Valid && previousMainContextGeneration == contextGeneration)
                {
                    var interval = DetectionIntervalMs(Found, true, _worldMisses, motion, flowLost, stillRuns);
                    var waitMs = WorldMapFrameWaitMs(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(lastEmblemCheck).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(lastSubmit).TotalMilliseconds, interval);
                    if (waitMs > 0)
                        await capture.WaitForFrameAfterAsync(previousMainCapture, TimeSpan.FromMilliseconds(waitMs), token);
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentMapContext(contextGeneration) || !_worldMapOpen || _worldScreen != area ||
                        Region != region || Paused)
                        continue;
                }
                var frame = GrabCurrent(area, worldMap ? WorldMapScale : 1, contextGeneration);
                var frameAt = GameCaptureService.GrabbedFrameAt;
                var captureTiming = GameCaptureService.GrabbedTiming;
                if (frame is null)
                {
                    if (!IsCurrentVisualContext(contextGeneration)) continue;
                    _cost.AddCaptureUnavailable();
                    previousMainCapture = default;
                    token.ThrowIfCancellationRequested();
                    flow.Reset();
                    frames.Clear();
                    ReportBusyFrame();
                    await Task.Delay(IntervalMs, token);
                    continue;
                }
                if (!IsCurrentVisualContext(contextGeneration)) { frame.Dispose(); continue; }
                if (!AcceptLastCapture())
                {
                    _cost.AddCaptureRejected();
                    previousMainCapture = default;
                    frame.Dispose();
                    flow.Reset();
                    frames.Clear();
                    await Task.Delay(IntervalMs, token);
                    continue;
                }
                _cost.AddCapture(captureTiming);
                // Keep the accepted main picture's cursor; the emblem read below has its own metadata.
                previousMainCapture = worldMap ? captureTiming : default;
                previousMainContextGeneration = contextGeneration;
                var flowStarted = Stopwatch.GetTimestamp();
                using (var gray = new Mat())
                {
                    if (!IsCurrentVisualContext(contextGeneration)) { frame.Dispose(); continue; }
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    // World map: points only on the map, its standing panels held the overlay still.
                    flow.SeedArea = worldMap ? MapFlow.WorldMapArea(gray.Width, gray.Height) : null;
                    var step = flow.Step(gray);
                    _cost.AddFlow(Stopwatch.GetElapsedTime(flowStarted), step is null, worldMap);
                    if (frameAt != 0)
                        _cost.AddFlowLag(Stopwatch.GetElapsedTime(frameAt));
                    if (step is null)
                    {
                        flowLost = true;
                        _flowBroken = true;
                    }
                    else
                        motion += Math.Sqrt(step.Value.Tx * step.Value.Tx + step.Value.Ty * step.Value.Ty)
                            + 100 * (Math.Abs(step.Value.A - 1) + Math.Abs(step.Value.B)); // zoom or turn counts as movement
                    if (step is { } shift)
                    {
                        if (TryApplyFlowShift(shift, frameAt, contextGeneration) && Placement is not null)
                            PlacementMoved?.Invoke();
                    }
                }

                // World map open: its header symbol, checked ten times a second, tells at once when it was
                // closed. Waiting for two failed world-map detections (0.5-1 s each) kept the world map's
                // overlay up for 2-3 s (user report 2026-10-07).
                if (worldMap && Stopwatch.GetElapsedTime(lastEmblemCheck).TotalMilliseconds >= EmblemCheckMs)
                {
                    lastEmblemCheck = Stopwatch.GetTimestamp();
                    var screen = _worldScreen;
                    using var corner = GrabCurrent(MapWindowEmblem.CornerOf(screen), 1, contextGeneration);
                    if (corner is not null)
                    {
                        emblemMisses = MapWindowEmblem.ShowsInCorner(corner, screen.Height) ? 0 : emblemMisses + 1;
                        if (emblemMisses >= 2)
                        {
                            emblemMisses = 0;
                            CloseWorldMap("Symbol weg");
                        }
                    }
                }
                else if (!worldMap)
                    emblemMisses = 0;

                // Flow moves only the visual overlay. It cannot establish that the minimap is still
                // visible: opening/panning the world map can also yield a perfectly good flow fit.
                // Publish player positions only from detection after checking the entire same frame.

                if (detecting is null || detecting.IsCompleted)
                {
                    if (detecting is { IsFaulted: true })
                        throw detecting.Exception!.InnerException ?? detecting.Exception;
                    var interval = DetectionIntervalMs(Found, worldMap, worldMap ? _worldMisses : _misses, motion, flowLost, stillRuns);
                    DetectionInterval = interval;
                    if (Stopwatch.GetElapsedTime(lastSubmit).TotalMilliseconds >= interval)
                    {
                        stillRuns = Found && !flowLost && motion < StillPixels ? stillRuns + 1 : 0;
                        motion = 0;
                        flowLost = false;
                        lastSubmit = Stopwatch.GetTimestamp();
                        lock (_flowGate)
                            _drift = FrameShift.Identity;
                        var f = frame;
                        Action detection = () =>
                        {
                            var detectionStarted = Stopwatch.GetTimestamp();
                            using var timed = new CostScope(() => _cost.AddDetection(Stopwatch.GetElapsedTime(detectionStarted)));
                            using (f)
                            {
                                token.ThrowIfCancellationRequested();
                                if (!IsCurrentVisualContext(contextGeneration)) return;
                                if (worldMap)
                                    StepWorldMap(region, maps, f, token, contextGeneration);
                                else
                                {
                                    var screen = capture.GameBounds ?? Screen.FromRectangle(region).Bounds;
                                    using var full = GrabCurrent(screen, 1, contextGeneration);
                                    if (full is null)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        if (!IsCurrentVisualContext(contextGeneration)) return;
                                        ReportBusyFrame();
                                        return;
                                    }
                                    if (!AcceptLastCapture()) return;
                                    lock (_flowGate)
                                        _drift = FrameShift.Identity;
                                    ProcessMiniMapFrame(region, screen, full, token, contextGeneration);
                                }
                            }
                        };
                        detecting = _detecting = ScheduleDetectionForTests?.Invoke(detection, token)
                            ?? Task.Run(detection, CancellationToken.None);
                        frame = null;
                    }
                }
                frame?.Dispose();

                var now = Stopwatch.GetTimestamp();
                if (UsesVisualFallback && (Found || worldMap))
                    _lostSince = 0;
                else if (UsesVisualFallback && _lostSince == 0 && detecting is { IsCompleted: true })
                    _lostSince = now; // from a finished search on: building a map reference does not count
                if (UsesVisualFallback) UpdateInstanceContext(Found, worldMap, _lostSince == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lostSince, now),
                    _lastOwnEnteredAt == 0 ? null : Stopwatch.GetElapsedTime(_lastOwnEnteredAt, now));
                if (UsesVisualFallback && _lostSince != 0 && ShouldPause(settings.Current.PauseTrackingInInstances, Found, worldMap,
                        Stopwatch.GetElapsedTime(_lostSince, now), _lastOwnEnteredAt == 0 ? null : Stopwatch.GetElapsedTime(_lastOwnEnteredAt, now)))
                {
                    Pause();
                    continue;
                }
                frames.Enqueue(now);
                while (frames.Count > 0 && Stopwatch.GetElapsedTime(frames.Peek(), now).TotalSeconds > 1)
                    frames.Dequeue();
                Fps = frames.Count;
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (spent < FrameMs)
                    await Task.Delay(TimeSpan.FromMilliseconds(FrameMs - spent), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Running = false;
            _escMenuOpen = false;
            _inInstance = false;
            Found = false;
            DetectedMap = null;
            SetStatus("Tracking abgebrochen: " + exception.Message, error: true);
        }
        finally
        {
            if (detecting is not null)
            {
                try { await detecting; }
                catch (OperationCanceledException) { }
                catch (Exception) { /* Detection failures were reported by the loop, or it was stopped. */ }
            }
            SetTimerResolution(false);
            _runGate.Release();
        }
    }

    private void ReportBusyFrame()
    {
        // Retain the last position but stop moving the overlay from an unvalidated frame.
        Found = false;
        DetectedMap = null;
        Placement = null;
        Fps = 0;
        SetStatus(CaptureReadback.Waiting);
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>
    /// Checks the small-map crop and world-map extent from one capture. Never combine a crop taken
    /// before opening/closing the world map with a screen taken after that transition.
    /// </summary>
    internal void ProcessMiniMapFrame(Rectangle region, Rectangle screen, Mat fullFrame, CancellationToken token, long? contextGeneration = null)
    {
        token.ThrowIfCancellationRequested();
        var generation = contextGeneration ?? Interlocked.Read(ref _mapContextGeneration);
        if (!IsCurrentMapContext(generation))
            return;
        if (!screen.Contains(region) || fullFrame.Width != screen.Width || fullFrame.Height != screen.Height)
        {
            Found = false;
            DetectedMap = null;
            Placement = null;
            SetStatus("Kartenbereich liegt außerhalb der Aufnahme – Position bleibt unverändert.");
            return;
        }
        var escMenuOpen = _escMenu.Detect(fullFrame);
        UpdateEscMenuContext(escMenuOpen, generation);
        if (escMenuOpen)
        {
            SetStatus("ESC-Menü offen – Overlays ausgeblendet.");
            return; // never publish player positions from a menu's background map
        }
        if (!IsCurrentMapContext(generation)) return;
        var maps = LoadMaps();
        _lastZoom ??= settings.Current.MiniMapZoom;
        _currentMap ??= maps.Any(m => m.Id == settings.Current.LastMap) ? settings.Current.LastMap : maps.FirstOrDefault()?.Id;
        using var crop = new Mat(fullFrame, new OpenCvSharp.Rect(region.X - screen.X, region.Y - screen.Y, region.Width, region.Height));
        using var small = new Mat();
        Cv2.Resize(fullFrame, small, new OpenCvSharp.Size(), WorldMapScale, WorldMapScale, InterpolationFlags.Area);
        if (_worldMapOpen)
        {
            StepWorldMap(region, maps, small, token, generation);
            return;
        }
        var header = MapWindowEmblem.Shows(fullFrame);
        var searchStarted = Stopwatch.GetTimestamp();
        if (Mode == MapTrackingMode.NetworkMap)
        {
            var ownMap = _worldContext?.Snapshot.SoulcrestMapId;
            if (maps.FirstOrDefault(m => m.Id == ownMap) is not { } ownDefinition) return;
            // The viewed world map has independent authority. A foreign displayed map is never a
            // minimap candidate and must not change the physical own-map selection.
            if (header)
                foreach (var viewed in maps.OrderBy(m => m.Id == ownMap ? 0 : 1))
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentMapContext(generation)) return;
                    if (Stopwatch.GetElapsedTime(searchStarted) >= RecoverySearchBudget) return;
                    if (Ready(_coarse, viewed.Id) is null)
                    {
                        PrepareWorldReference(viewed, token, generation);
                        continue;
                    }
                    if (ProbeWorldMap(region, viewed, screen, small, token, generation)) return;
                }
            if (!IsCurrentMapContext(generation)) return;
            _currentMap = ownDefinition.Id;
            Step(region, maps, crop, screen, small, header, token, generation, ownDefinition.Id, searchStarted);
            return;
        }
        if (Paused || InInstance || _misses >= MissesBeforeOtherMaps)
        {
            // user request 2026-10-19: one paused look must not mean one map followed by
            // another five-second sleep. Search ready references in one bounded pass.
            var ready = maps.Where(m => Ready(_coarse, m.Id) is not null).Select(m => m.Id).ToList();
            var confirmation = Interlocked.Exchange(ref _confirmationMap, null);
            var candidates = RecoveryCandidates(_currentMap, ready, _recentMaps, _recoverySearchTick);
            if (confirmation is not null && ready.Contains(confirmation) && Ready(_fine, confirmation) is not null)
                candidates = new[] { confirmation }.Concat(candidates.Where(id => id != confirmation)).ToArray();
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentMapContext(generation) || Stopwatch.GetElapsedTime(searchStarted) >= RecoverySearchBudget)
                    break;
                _recoverySearchTick++;
                var previousFix = Interlocked.Read(ref _lastFixAt);
                Step(region, maps, crop, screen, small, header, token, generation, candidate, searchStarted);
                if ((Found && Interlocked.Read(ref _lastFixAt) != previousFix) || _worldMapOpen) break;
            }
            return;
        }
        Step(region, maps, crop, screen, small, header, token, generation, frameStarted: searchStarted);
    }

    internal static readonly TimeSpan RecoverySearchBudget = TimeSpan.FromMilliseconds(1200);

    internal static string[] RecoveryCandidates(string? current, IReadOnlyList<string> ready,
        IReadOnlyList<string> recent, int tick)
    {
        var ordered = recent.Where(id => id != current && ready.Contains(id))
            .Concat(ready.Where(id => id != current)).Concat(ready.Where(id => id == current)).Distinct().ToArray();
        if (ordered.Length == 0) return [];
        var start = (int)((uint)tick % (uint)ordered.Length);
        return ordered.Skip(start).Concat(ordered.Take(start)).ToArray();
    }

    private void Step(Rectangle region, List<MapDefinition> maps, Mat frame, Rectangle screen, Mat screenFrame, bool windowHeader, CancellationToken token, long contextGeneration,
        string? searchMap = null, long? frameStarted = null)
    {
        if (!IsCurrentMapContext(contextGeneration)) return;
        var anchor = new Point2d(region.Width * settings.Current.PlayerAnchorX, region.Height * settings.Current.PlayerAnchorY);

        // Recently found maps first: after a look at the full-screen map of another zone the player is
        // usually back on the previous map (live log 2026-10-03: Verteron map opened, back in Altgard).
        var others = _misses >= MissesBeforeOtherMaps
            ? maps.Where(m => m.Id != _currentMap && Ready(_coarse, m.Id) is not null)
                .OrderBy(m => _recentMaps.IndexOf(m.Id) is var i and >= 0 ? i : int.MaxValue)
                .Select(m => m.Id).ToList()
            : [];
        var tick = _searchTick++;
        var candidate = Mode == MapTrackingMode.NetworkMap ? _worldContext?.Snapshot.SoulcrestMapId
            : searchMap ?? NextCandidate(_currentMap, _misses, others, tick);
        if (candidate is null || maps.FirstOrDefault(m => m.Id == candidate) is not { } definition)
            return;

        var watch = Stopwatch.StartNew();
        // While lost, the current map's turns alternate between the fine and the coarse reference: the
        // coarse one copes better when the in-game map was zoomed far out.
        var useFine = candidate == _currentMap && (_misses < MissesBeforeOtherMaps || _searchTick / 2 % 2 == 0);
        var fineLocator = useFine ? Ready(_fine, candidate) : null;
        var locator = fineLocator
            ?? Ready(_coarse, candidate)
            ?? Load(definition, definition.CoarseZoom, _coarse, token);
        // The fine reference follows the player with a 0.6× picture: same position (±0.1 map units) at
        // half the cost (114 → 57 ms, live captures 2026-10-03). Each locator always gets the same size,
        // so its local search stays consistent; the coarse search for a lost position keeps full size.
        if (!IsCurrentMapContext(contextGeneration)) return;
        SearchStartedForTests?.Invoke(candidate, false);
        var fix = LocateAt(locator, frame, locator == fineLocator ? FineFrameScaleFor(region.Size) : 1.0);
        if (fix is not null && candidate != _currentMap && fix.Inliers < SwitchMinInliers)
        {
            // A detailed minimap can be too weak against the coarse reference to confirm a switch
            // (Altgard after Abyss: 26 coarse pairs, 73 fine pairs; diagnosis 2026-10-13). Verify the
            // same candidate and capture at the finer zoom before rejecting it. Keep the 30-pair
            // switch requirement: a weak coarse hint alone must never publish a position or wake
            // an inferred instance. Missing detail references are prepared off the detection path.
            if (definition.FineZoom > definition.CoarseZoom)
            {
                var prepared = Ready(_fine, candidate);
                if (prepared is null)
                {
                    PrepareConfirmation(definition, token);
                    return; // no blocking reference build and no publication of the weak hint
                }
                locator = prepared;
                fix = LocateAt(locator, frame, FineFrameScaleFor(region.Size));
            }
            if (fix is not null && fix.Inliers < SwitchMinInliers)
                fix = null;
        }
        // A narrow fix (few pairs) must fit the zoom of the last good one: while the in-game map fades,
        // 14–15 pairs at scale 0.55 instead of 0.89 came up (live log 2026-10-03) and would make the
        // position jump. Zoom is compared in screen pixels per map unit (fine and coarse alike).
        if (fix is not null && !PlausibleZoom(fix.Inliers, fix.Scale / locator.Reference.WorldPerPixel, _lastZoom))
            fix = null;
        // A world-map crop can match with the SAME zoom and many inliers. Check every candidate,
        // including apparent successes, before changing the position, active map or saved zoom.
        // The world-map check costs as much as the search itself (184 ms on the quarter-size screen). It
        // runs only when the header symbol of a game window shows top left (world map; the pet window has
        // it too), and as a safety net on every 4th miss (user idea 2026-10-05: "nur die obere linke Ecke").
        if ((windowHeader || (fix is null && _misses % 4 == 3)) && ProbeWorldMap(region, definition, screen, screenFrame, token, contextGeneration))
            return;
        token.ThrowIfCancellationRequested();
        if (!IsCurrentMapContext(contextGeneration))
            return;
        if (frameStarted is { } captured && Stopwatch.GetElapsedTime(captured) > MaximumFrameAge)
            return;
        LocateMs = watch.ElapsedMilliseconds;
        Inliers = locator.LastInfo.Inliers;
        PreviewDataUrl = PreviewWithAnchor(frame, anchor);

        Trace(definition.Id, candidate == _currentMap, fix, locator);
        if (fix is null)
        {
            if (candidate == _currentMap)
                _misses++;
            // Single misses happen while the in-game map zooms, scrolls or fades (live log 2026-10-03:
            // lost and found again within a second). Keep the last placement for a moment so the
            // marker and the route lines do not flicker (Map Overlay holds 8 frames).
            if (Placement is not null && _misses <= HoldFrames)
            {
                SetStatus($"Karte kurz nicht erkannt – halte letzte Position ({locator.LastInfo.Matches} Treffer).");
                return;
            }
            if (Placement is not null && HoldsByFlow(_flowBroken, Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFixAt))))
            {
                SetStatus($"Karte gerade schwer erkennbar ({locator.LastInfo.Keypoints} Merkmale) – der Bildfluss hält die Position.");
                return;
            }
            Found = false;
            DetectedMap = null;
            Placement = null;
            SetStatus(_misses >= MissesBeforeOtherMaps
                ? $"Karte nicht erkannt – probiere {definition.Label} … ({locator.LastInfo.Keypoints} Merkmale im Bild)"
                : $"Karte kurz nicht erkannt ({locator.LastInfo.Matches} Treffer).");
            return;
        }

        _lastZoom = fix.Scale / locator.Reference.WorldPerPixel;
        if (settings.Current.MiniMapZoom is not { } saved || Math.Abs(_lastZoom.Value / saved - 1) > 0.05)
            settings.Update(s => s.MiniMapZoom = _lastZoom);
        _recentMaps.Remove(definition.Id);
        _recentMaps.Insert(0, definition.Id);
        if (_currentMap != definition.Id)
        {
            _currentMap = definition.Id;
            // A weak coarse candidate may already have needed its fine reference for confirmation.
            if (Ready(_fine, definition.Id) is null)
                _ = Task.Run(() => Load(definition, definition.FineZoom, _fine, token), token);
        }
        lock (_flowGate)
        {
            // Settings notifications and network loading events can invalidate a completed SIFT
            // result while it is being processed. Serialize this publication with invalidation.
            if (!IsCurrentMapContext(contextGeneration))
                return;
            _misses = 0;
            Found = true;
            _inInstance = false;
            _flowBroken = false;
            Interlocked.Exchange(ref _lastFixAt, Stopwatch.GetTimestamp());
            var reference = fix.FrameToReference(anchor);
            var world = locator.Reference.WorldPerPixel;
            if (!EffectiveFeaturePolicy.AllowsLiveMap(NetworkContextEnabled, _worldContext?.Snapshot, definition.Id))
            {
                Found = false;
                DetectedMap = null;
                Placement = null;
                return;
            }
            Position = new PlayerPosition(definition.Id, reference.X * world, reference.Y * world, DateTimeOffset.UtcNow)
                { ContextGeneration = contextGeneration };
            DetectedMap = new DetectedMapContext(definition.Id, false, Position.At) { ContextGeneration = contextGeneration };
            // The map moved on while this frame was being matched: carry the result by the flow since then.
            Placement = new MapPlacement(definition.Id, _drift.Apply(fix), world, region, anchor) { CapturedAt = _flowFrameAt, ContextGeneration = contextGeneration };
        }
        if (Paused) Resume(null);
        PlacementMoved?.Invoke();
        SetStatus($"{definition.Label}: Position {Position.X:0}, {Position.Y:0} · {fix.Inliers} Treffer · Erkennung {LocateMs} ms · Overlay {Fps} fps · {CaptureMethod}");
    }

    /// <summary>
    /// Looks for the map on the whole screen (quarter size). It counts as the open world map when it is found
    /// clearly and the agreeing keypoints spread well beyond the marked small-map area.
    /// </summary>
    internal const double FineFrameScale = 0.6;

    /// <summary>
    /// The fine search shrinks large map areas to 0.6 (half the cost at the same position), small ones not:
    /// a 514×292 minimap kept too few keypoints at 0.6 and was not found, at full size it was (colleague's
    /// diagnosis 2026-10-07; the user's 518×469 area stays at 0.6).
    /// </summary>
    internal static double FineFrameScaleFor(System.Drawing.Size region) => region.Width * region.Height >= 200_000 ? FineFrameScale : 1.0;

    /// <summary>Holds a lost position while the flow followed the minimap without a break since it was found.</summary>
    internal static bool HoldsByFlow(bool flowBroken, TimeSpan sinceFix) => !flowBroken && sinceFix < FlowHoldLimit;

    /// <summary>Locates a picture shrunk by <paramref name="scale"/> and returns the fix for the full-size picture.</summary>
    internal static MapFix? LocateAt(MapLocator locator, Mat frame, double scale)
    {
        if (scale >= 1)
            return locator.Locate(frame);
        using var scaled = new Mat();
        Cv2.Resize(frame, scaled, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        if (locator.Locate(scaled) is not { } fix)
            return null;
        return fix with
        {
            A = fix.A / scale, B = fix.B / scale, Tx = fix.Tx / scale,
            C = fix.C / scale, D = fix.D / scale, Ty = fix.Ty / scale,
            Error = fix.Error / scale,
            InlierBounds = fix.InlierBounds is { } b ? new Rect2d(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale) : null,
        };
    }

    private bool ProbeWorldMap(Rectangle region, MapDefinition definition, Rectangle screen, Mat small, CancellationToken token, long contextGeneration)
    {
        if (!IsCurrentMapContext(contextGeneration)) return false;
        // Same capture as the crop being considered for a player position.
        var locator = WorldLocator(definition, token);
        locator.Reset();
        if (!IsCurrentMapContext(contextGeneration)) return false;
        SearchStartedForTests?.Invoke(definition.Id, true);
        var fix = locator.Locate(small);
        token.ThrowIfCancellationRequested();
        if (fix is null || fix.Inliers < SwitchMinInliers || !SpreadsBeyond(fix, region, screen))
            return false;
        lock (_flowGate)
        {
            if (!IsCurrentMapContext(contextGeneration))
                return false;
            _worldMapOpen = true;
            _worldScreen = screen;
            _worldMapId = definition.Id;
            _worldMisses = 0;
            SetWorldPlacement(definition, fix, screen, locator.Reference.WorldPerPixel);
        }
        Trace(definition.Id + " (Weltkarte)", true, fix, locator);
        return true;
    }

    /// <summary>Detection while the world map is open; closes the mode when the world map is gone.</summary>
    private void StepWorldMap(Rectangle region, List<MapDefinition> maps, Mat frame, CancellationToken token, long contextGeneration)
    {
        if (!_worldMapOpen || !IsCurrentMapContext(contextGeneration))
            return;
        if (maps.FirstOrDefault(m => m.Id == _worldMapId) is not { } definition)
        {
            _worldMapOpen = false;
            DetectedMap = null;
            return;
        }
        // The fast loop captured the world map at a quarter of its size already.
        var locator = WorldLocator(definition, token);
        if (!IsCurrentMapContext(contextGeneration)) return;
        SearchStartedForTests?.Invoke(definition.Id, true);
        var fix = locator.Locate(frame);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentMapContext(contextGeneration))
            return;
        if (fix is not null && fix.Inliers >= MinWorldInliers && SpreadsBeyond(fix, region, _worldScreen))
        {
            _worldMisses = 0;
            lock (_flowGate)
            {
                if (!_worldMapOpen || !IsCurrentMapContext(contextGeneration))
                    return; // closed, loaded or stopped meanwhile
                SetWorldPlacement(definition, _drift.Apply(fix), _worldScreen, locator.Reference.WorldPerPixel);
            }
            _cost.AddWorldDetection(true);
            Trace(definition.Id + " (Weltkarte)", true, fix, locator);
            return;
        }
        // One miss is held (panning, zooming); the second one means the world map was closed.
        _cost.AddWorldDetection(false);
        if (++_worldMisses < 2)
            return;
        Trace(definition.Id + " (Weltkarte zu)", true, null, locator);
        CloseWorldMap(null);
    }

    private const int EmblemCheckMs = 100;

    /// <summary>Leaves the world-map mode; the small map is searched again from the next frame on.</summary>
    private void CloseWorldMap(string? reason)
    {
        lock (_flowGate)
        {
            if (!_worldMapOpen)
                return;
            _worldMapOpen = false;
            _misses = 0;
            Placement = null;
            Found = false;
            DetectedMap = null;
        }
        if (reason is not null)
            LogFile.Append("map-tracking.log", $"{DateTime.Now:HH:mm:ss} Weltkarte zu ({reason}){Environment.NewLine}");
        PlacementMoved?.Invoke();
        SetStatus("Weltkarte geschlossen – suche die kleine Karte …");
    }

    private const int MinWorldInliers = 20;

    private void SetWorldPlacement(MapDefinition definition, MapFix fix, Rectangle screen, double worldPerPixel)
    {
        // The player stays where the small map last saw them, if the world map shows that zone.
        Point2d? player = Position is { } p && p.MapId == definition.Id && p.ContextGeneration == ContextGeneration
            && EffectiveFeaturePolicy.AllowsLiveMap(NetworkContextEnabled, _worldContext?.Snapshot, p.MapId)
            ? new Point2d(p.X, p.Y) : null;
        Placement = new MapPlacement(definition.Id, fix, worldPerPixel, screen, default, 1 / WorldMapScale, true, player) { CapturedAt = _flowFrameAt, ContextGeneration = ContextGeneration };
        Found = true;
        _inInstance = false;
        DetectedMap = new DetectedMapContext(definition.Id, true, DateTimeOffset.UtcNow) { ContextGeneration = ContextGeneration };
        if (Paused) Resume(null);
        PlacementMoved?.Invoke();
        SetStatus($"Weltkarte {definition.Label} offen – Overlay folgt ihr, Position eingefroren · {fix.Inliers} Treffer · Overlay {Fps} fps");
    }

    /// <summary>The matched keypoints cover clearly more than the marked small-map area (frame at world-map scale).</summary>
    public static bool SpreadsBeyond(MapFix fix, Rectangle region, Rectangle screen)
    {
        if (fix.InlierBounds is not { } b)
            return false;
        var bounds = new RectangleF((float)(b.X / WorldMapScale), (float)(b.Y / WorldMapScale), (float)(b.Width / WorldMapScale), (float)(b.Height / WorldMapScale));
        var small = new RectangleF(region.X - screen.X, region.Y - screen.Y, region.Width, region.Height);
        small.Inflate(region.Width * 0.1f, region.Height * 0.1f);
        return !small.Contains(bounds) && (bounds.Width > region.Width * 1.3f || bounds.Height > region.Height * 1.3f);
    }

    /// <summary>Own locator per map for the world map (search state apart from the small map's).</summary>
    private MapLocator WorldLocator(MapDefinition definition, CancellationToken token)
    {
        lock (_gate)
        {
            if (_world.TryGetValue(definition.Id, out var existing))
                return existing;
        }
        var coarse = Ready(_coarse, definition.Id) ?? Load(definition, definition.CoarseZoom, _coarse, token);
        var locator = new MapLocator(coarse.Reference);
        lock (_gate)
            _world[definition.Id] = locator;
        return locator;
    }

    /// <summary>
    /// Which map to try on this frame. The current map first (fine reference once ready). After a few
    /// misses the search alternates: every other frame the current map, in between one other map
    /// (coarse reference, round robin). The current map must keep its turns: it used to be skipped once
    /// the search went on to the other maps, so a lost position was never found again (user report
    /// 2026-10-03).
    /// </summary>
    public static string? NextCandidate(string? current, int misses, IReadOnlyList<string> readyOthers, int tick)
    {
        if (current is null)
            return readyOthers.Count > 0 ? readyOthers[tick % readyOthers.Count] : null;
        if (misses < MissesBeforeOtherMaps || readyOthers.Count == 0 || tick % 2 == 0)
            return current;
        // misses grows by one per turn of the current map, so each other map gets one turn in order,
        // starting with the most recently found one when the search begins.
        return readyOthers[Math.Max(0, misses - MissesBeforeOtherMaps - 1) % readyOthers.Count];
    }

    /// <summary>Fixes with at least 30 pairs always count; narrower ones only within ±25 % of the last zoom.</summary>
    public static bool PlausibleZoom(int inliers, double zoom, double? lastZoom) =>
        inliers >= SwitchMinInliers || lastZoom is not { } last || Math.Abs(zoom / last - 1) <= 0.25;

    private MapLocator? Ready(Dictionary<string, MapLocator> locators, string mapId)
    {
        lock (_gate)
            return locators.GetValueOrDefault(mapId);
    }

    /// <summary>
    /// Builds the references in the background so that the map search never waits for one: the coarse
    /// ones of all maps (current map first), then the fine one of the current map. Each is built once
    /// (~4 s coarse, ~15 s fine) and then read from the cache.
    /// </summary>
    private void Prepare(List<MapDefinition> maps, CancellationToken token, long contextGeneration)
    {
        try
        {
            foreach (var map in maps.OrderBy(m => m.Id == _currentMap ? 0 : 1))
            {
                if (!IsCurrentMapContext(contextGeneration)) return;
                Load(map, map.CoarseZoom, _coarse, token);
            }
            if (!IsCurrentMapContext(contextGeneration)) return;
            if (maps.FirstOrDefault(m => m.Id == _currentMap) is { } current)
                Load(current, current.FineZoom, _fine, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PrepareConfirmation(MapDefinition definition, CancellationToken token)
    {
        var generation = ContextGeneration;
        if (!IsCurrentVisualContext(generation)) return;
        lock (_flowGate) token = _preparationCancel?.Token ?? token;
        lock (_gate)
        {
            if (_confirmationPreparations.TryGetValue(definition.Id, out var previous))
            {
                if (!previous.IsCompleted) return;
                _ = previous.Exception; // observe a failed attempt before allowing a retry
            }
            _confirmationPreparations[definition.Id] = Task.Run(() =>
            {
                if (!IsCurrentVisualContext(generation)) return;
                Load(definition, definition.FineZoom, _fine, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentVisualContext(generation)) return;
                Interlocked.Exchange(ref _confirmationMap, definition.Id);
                _wake.TrySetResult(); // retry on a new capture, even while resting in an instance
            }, token);
        }
    }

    private void PrepareWorldReference(MapDefinition definition, CancellationToken token, long generation)
    {
        if (!IsCurrentVisualContext(generation)) return;
        lock (_flowGate) token = _preparationCancel?.Token ?? token;
        lock (_gate)
        {
            // The generic window header also occurs on pet windows: it must not synchronously
            // build all map references, or launch several SIFT builds in parallel.
            if (_worldPreparations.Values.Any(task => !task.IsCompleted)) return;
            if (_worldPreparations.Remove(definition.Id, out var previous)) _ = previous.Exception;
            _worldPreparations[definition.Id] = Task.Run(() =>
            {
                try
                {
                    if (!IsCurrentVisualContext(generation)) return;
                    Load(definition, definition.CoarseZoom, _coarse, token);
                    token.ThrowIfCancellationRequested();
                    if (IsCurrentVisualContext(generation)) _wake.TrySetResult();
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), object> _loadGates = new();

    private MapLocator Load(MapDefinition definition, int zoom, Dictionary<string, MapLocator> into, CancellationToken token)
    {
        lock (_loadGates.GetOrAdd((definition.Id, zoom), _ => new object()))
        {
            token.ThrowIfCancellationRequested();
            return LoadOnce(definition, zoom, into, token);
        }
    }

    private MapLocator LoadOnce(MapDefinition definition, int zoom, Dictionary<string, MapLocator> into, CancellationToken token)
    {
        lock (_gate)
        {
            if (into.TryGetValue(definition.Id, out var existing))
                return existing;
        }
        var worldPerPixel = definition.Size / (256.0 * (1 << zoom));
        var version = DataVersion();
        var cache = Path.Combine(AppPaths.MapReferenceDirectory, $"{definition.Id}-z{zoom}-{version}.bin");
        PruneReferences(AppPaths.MapReferenceDirectory, version);
        var reference = MapReference.Load(cache);
        var expectedDimension = 256 * (1 << zoom);
        if (reference is not null && (reference.MapId != definition.Id || reference.WorldPerPixel != worldPerPixel
            || reference.Width != expectedDimension || reference.Height != expectedDimension))
        {
            reference.Dispose();
            reference = null; // A valid cache for another map/scale is still a miss for this reference.
        }
        if (reference is null)
        {
            if (!Found)
                SetStatus($"{definition.Label} wird für das Tracking vorbereitet (einmalig, einige Sekunden) …");
            var pattern = Path.Combine(progress.MapDataDirectory!, definition.Tiles.Replace('/', Path.DirectorySeparatorChar));
            using var gray = MapReference.ComposeTiles(pattern, zoom, definition.TileSize);
            reference = MapReference.Build(definition.Id, gray, worldPerPixel, cancellationToken: token);
            reference.Save(cache);
        }
        var locator = new MapLocator(reference);
        lock (_gate)
        {
            if (into.TryGetValue(definition.Id, out var raced))
            {
                locator.Dispose();
                reference.Dispose();
                return raced;
            }
            into[definition.Id] = locator;
        }
        return locator;
    }

    // SOULCREST_OPENCV_THREADS overrides it for comparisons (0 = OpenCV's own choice, all threads).
    internal static readonly int OpenCvThreads =
        int.TryParse(Environment.GetEnvironmentVariable("SOULCREST_OPENCV_THREADS"), out var threads) && threads >= 0 ? threads : 2;
    private readonly TrackingCost _cost = new();
    private long _flowFrameAt; // screen picture of the newest flow step; under _flowGate

    /// <summary>Records one composed image actually handed to Windows, including its source timestamp.</summary>
    internal void ReportOverlayShown(OverlayTiming timing) => _cost.AddOverlayPresentation(timing);
    internal void ReportOverlayComposed(TimeSpan spent) => _cost.AddComposition(spent);
    internal void ResetOverlayPresentation() => _cost.ResetOverlayPresentation();

    private sealed class CostScope(Action done) : IDisposable
    {
        public void Dispose() => done();
    }

    private bool _tracedFound;
    private DateTime _lastTrace = DateTime.MinValue;

    /// <summary>
    /// Short log for live diagnosis (%LOCALAPPDATA%\Soulcrest\logs\map-tracking.log): every change between
    /// found and lost, plus one line per 10 s.
    /// </summary>
    private void Trace(string mapId, bool current, MapFix? fix, MapLocator locator)
    {
        var found = fix is not null;
        var now = DateTime.Now;
        if (found == _tracedFound && now - _lastTrace < TimeSpan.FromSeconds(10))
            return;
        _tracedFound = found;
        _lastTrace = now;
        var info = locator.LastInfo;
        LogFile.Append("map-tracking.log", // never throws, also not when the rotation fails
            $"{now:HH:mm:ss} {(found ? "gefunden" : "verloren")} {mapId}{(current ? "" : " (Suche)")} ref={locator.Reference.Width}px " +
            $"merkmale={info.Keypoints} treffer={info.Matches} stimmig={info.Inliers} lokal={info.Local} fehlschläge={_misses} weltfehlschläge={_worldMisses} suche={DetectionInterval}ms overlay={Fps}fps aufnahme={CaptureMethod}" +
            (fix is null ? "" : $" maßstab={fix.Scale:0.00} fehler={fix.Error:0.0}") + " " + _cost.TakeSummary() + Environment.NewLine);
    }

    /// <summary>
    /// References of older map data versions are never read again; they piled up to 100 files and 2 GB in
    /// four days of map data updates (2026-10-07). Kept: the current version and the newest other one, as
    /// the desktop tester and an installed Soulcrest may use different map data side by side.
    /// </summary>
    internal static void PruneReferences(string directory, string version)
    {
        if (!Directory.Exists(directory))
            return;
        static string VersionOf(FileInfo file) => Path.GetFileNameWithoutExtension(file.Name).Split('-')[^1];
        var others = new DirectoryInfo(directory).GetFiles("*.bin").Where(f => VersionOf(f) != version).ToList();
        var keep = others.OrderByDescending(f => f.LastWriteTimeUtc).Select(VersionOf).FirstOrDefault();
        foreach (var file in others.Where(f => VersionOf(f) != keep))
        {
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // in use: next time
        }
    }

    private string DataVersion()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(progress.MapDataDirectory!, "manifest.json")));
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "0" : "0";
    }

    private List<MapDefinition> LoadMaps()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(progress.MapDataDirectory!, "manifest.json")));
        return document.RootElement.GetProperty("maps").EnumerateArray()
            .Select(m => new MapDefinition(
                m.GetProperty("id").GetString()!,
                m.GetProperty("label").GetString()!,
                m.GetProperty("tiles").GetString()!,
                m.GetProperty("refZoom").GetInt32(),
                m.GetProperty("size").GetInt32(),
                m.GetProperty("maxNativeZoom").GetInt32(),
                m.TryGetProperty("tileSize", out var tileSize) ? tileSize.GetInt32() : 256))
            .ToList();
    }

    /// <summary>Small JPEG of the marked area with a red cross on the player anchor (Optionen tab).</summary>
    private static string PreviewWithAnchor(Mat frame, Point2d anchor)
    {
        using var copy = frame.Clone();
        var size = (int)Math.Max(8, frame.Width / 25.0);
        var thickness = (int)Math.Max(2, frame.Width / 160.0);
        var centre = new OpenCvSharp.Point((int)anchor.X, (int)anchor.Y);
        Cv2.Line(copy, centre - new OpenCvSharp.Point(size, 0), centre + new OpenCvSharp.Point(size, 0), new Scalar(0, 0, 255), thickness);
        Cv2.Line(copy, centre - new OpenCvSharp.Point(0, size), centre + new OpenCvSharp.Point(0, size), new Scalar(0, 0, 255), thickness);
        var scale = Math.Min(1.0, 360.0 / copy.Width);
        using var small = new Mat();
        Cv2.Resize(copy, small, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        Cv2.ImEncode(".jpg", small, out var jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
        SafeEvent.Raise(Changed, "Spieler-Tracking"); // subscribers save (arrival), a failure must not end tracking
    }

    private static bool WaitUntilFinished(Task[] tasks)
    {
        try { return Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { return tasks.All(task => task.IsCompleted); }
    }

    public void Dispose()
    {
        settings.Changed -= SettingsChanged;
        if (_worldContext is not null) _worldContext.Changed -= WorldContextChanged;
        _cancel?.Cancel();
        lock (_flowGate) _preparationCancel?.Cancel();
        // Wait for the loop and a detection in flight: freeing the references under a running knnMatch
        // crashed the app on close (0xc0000005, live 2026-10-03). If they do not end in time, the native
        // memory is left to the exiting process instead.
        var running = new[] { _loop, _detecting, _preparing }.OfType<Task>().ToArray();
        if (!WaitUntilFinished(running)) return;
        // Snapshot after detection stopped: it may have enqueued work during shutdown.
        Task[] preparations;
        lock (_gate) preparations = _confirmationPreparations.Values.Concat(_worldPreparations.Values).ToArray();
        if (!WaitUntilFinished(preparations)) return;
        lock (_gate)
        {
            foreach (var locator in _coarse.Values.Concat(_fine.Values))
            {
                locator.Reference.Dispose();
                locator.Dispose();
            }
            foreach (var locator in _world.Values)
                locator.Dispose();
            _coarse.Clear();
            _fine.Clear();
            _world.Clear();
        }
    }
}
