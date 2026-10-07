using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.Ocr;
using Soulcrest.Ocr.MapTracking;

namespace Soulcrest.App.Services;

/// <summary>Player position in map coordinates (the marker coordinates of the data package).</summary>
public sealed record PlayerPosition(string MapId, double X, double Y, DateTimeOffset At);

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
/// The map is detected automatically: while the current map is not found, one other map is tried per
/// frame.
/// </summary>
public sealed class MapTrackingService(SettingsService settings, ProgressService progress, GameCaptureService capture) : IDisposable
{
    private const int IntervalMs = 250;
    // Standing still, the expensive detection (130-270 ms) runs less often: each quiet detection doubles
    // the gap up to 2 s. Any movement of the map picture, a lost flow, the world map or an unsure
    // position bring back the 250 ms (user request 2026-10-05; the network has no own position).
    private const int MaxIdleIntervalMs = 2000;
    private const double StillPixels = 1.5;

    /// <summary>Gap until the next detection: 250 ms while anything moves or is unsure, up to 2 s standing still.</summary>
    internal static int DetectionIntervalMs(bool found, bool worldMap, int misses, double motionPixels, bool flowLost, int stillRuns) =>
        found && !worldMap && misses == 0 && !flowLost && motionPixels < StillPixels
            ? Math.Min(MaxIdleIntervalMs, IntervalMs << Math.Clamp(stillRuns, 0, 3))
            : IntervalMs;

    /// <summary>Current gap between two detections (diagnosis).</summary>
    public int DetectionInterval { get; private set; } = IntervalMs;
    private const int ReferenceZoomWidth = 4096; // reference image size, as in Map Overlay
    public const int MissesBeforeOtherMaps = 3;
    private const int HoldFrames = 4; // ~1 s at 4 frames per second
    // Switching to another map needs clearly more evidence than staying: maps share decorations
    // (compass rose, emblems), a cut-out of one Abyss map reached 20 pairs on another one.
    private const int SwitchMinInliers = 30;

    private sealed record MapDefinition(string Id, string Label, string Tiles, int RefZoom, int Size, int MaxNativeZoom)
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
    private Task? _detecting;
    private CancellationTokenSource? _cancel;
    private Task? _loop;
    private string? _currentMap;
    private int _misses;
    private int _searchTick;
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

    public event Action? Changed;

    /// <summary>The placement moved (optical flow, up to 60 times per second): redraw the in-game overlay.</summary>
    public event Action? PlacementMoved;

    /// <summary>Frames per second of the fast loop (capture + flow), capped at 60.</summary>
    public int Fps { get; private set; }

    private const double FrameMs = 1000.0 / 60;

    // Screen capture via Windows Graphics Capture of the game window (small map 0.5 ms, quarter-size
    // world map 5.6 ms; GDI: ~85 ms with the game running), shared with the other trackers.
    /// <summary>"Spielfenster (WGC)", "Monitor (WGC)" or "GDI": which capture the tracking uses.</summary>
    public string CaptureMethod => capture.MethodText;

    private Mat Grab(Rectangle area, double scale) => capture.Grab(area, scale);
    private readonly object _flowGate = new();
    private FrameShift _drift = FrameShift.Identity;

    public bool Running { get; private set; }
    public string Status { get; private set; } = "Kartenbereich markieren, dann Tracking starten.";
    public bool StatusIsError { get; private set; }
    public PlayerPosition? Position { get; private set; }

    /// <summary>Current placement of the map on screen; null while the map is not found.</summary>
    public MapPlacement? Placement { get; private set; }

    /// <summary>The map is currently found in the marked area.</summary>
    public bool Found { get; private set; }

    /// <summary>
    /// Instance without a world map (dungeon inside, story instance): tracking rests to save power and
    /// only looks every few seconds; a loading screen or a found map brings it back at once (user
    /// request 2026-10-06).
    /// </summary>
    public bool Paused { get; private set; }

    /// <summary>No map this long after a loading screen: an instance. Without a loading screen seen (loot
    /// tracking off, or a long cutscene in the world) the tracking waits longer.</summary>
    internal static readonly TimeSpan PauseAfterLoadingScreen = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan PauseWithoutLoadingScreen = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Gap between two looks while resting: every 2 s in the first 20 s (a short loss after a teleport in
    /// the world), then every 5 s. Replayed on the speedrun video (2026-10-06): same rest in instances,
    /// half the lost world positions (112 instead of 203 s in 3 h).
    /// </summary>
    internal static int PausedProbeMs(TimeSpan pausedFor) => pausedFor < TimeSpan.FromSeconds(20) ? 2000 : 5000;
    private long _pausedAt;
    private long _lostSince;        // Stopwatch timestamp of the first second without a map (0: found)
    private long _loadingScreenAt;  // last loading screen reported by the network (0: none)
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

    /// <summary>A loading screen (network, message of the own character): a map may come back.</summary>
    public void NoteLoadingScreen()
    {
        _loadingScreenAt = Stopwatch.GetTimestamp();
        _wake.TrySetResult();
    }

    private void Pause()
    {
        if (!Paused)
            _pausedAt = Stopwatch.GetTimestamp();
        Paused = true;
        Found = false;
        Placement = null;
        Fps = 0;
        SetStatus("Keine Karte erkannt (Instanz) – Tracking ruht und prüft alle paar Sekunden.");
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
        _loop = Task.Run(() => LoopAsync(token), token);
        SetStatus("Tracking läuft – Karte wird gesucht …");
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
        Running = false;
        Paused = false;
        _lostSince = 0;
        Found = false;
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

    private async Task LoopAsync(CancellationToken token)
    {
        await _runGate.WaitAsync(token);
        Task? detecting = null;
        using var flow = new MapFlow();
        // 1 ms timer resolution while tracking runs; otherwise Windows waits in 15.6 ms steps and the
        // 60 fps cap becomes ~40 fps.
        _ = TimeBeginPeriod(1);
        try
        {
            var maps = LoadMaps();
            _lastZoom ??= settings.Current.MiniMapZoom;
            _currentMap ??= maps.Any(m => m.Id == settings.Current.LastMap) ? settings.Current.LastMap : maps.FirstOrDefault()?.Id;
            if (_preparing is { } previousPreparation)
            {
                try { await previousPreparation; } catch (OperationCanceledException) { }
            }
            token.ThrowIfCancellationRequested();
            _preparing = Task.Run(() => Prepare(maps, token), token);
            var lastSubmit = 0L;
            double motion = 0;      // flow movement since the last detection (frame pixels)
            var flowLost = true;    // the flow could not follow since the last detection
            var stillRuns = 0;      // detections in a row without movement
            var frames = new Queue<long>();
            Rectangle? lastRegion = null;
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (Region is not { } region)
                {
                    await Task.Delay(IntervalMs, token);
                    continue;
                }
                if (Paused)
                {
                    // Resting in an instance: no 60 fps capture and flow, one look every few seconds.
                    flow.Reset();
                    frames.Clear();
                    lastRegion = null;
                    if (!Found)
                    {
                        var wake = _wake.Task;
                        var woke = await Task.WhenAny(wake, Task.Delay(PausedProbeMs(Stopwatch.GetElapsedTime(_pausedAt)), token)) == wake;
                        token.ThrowIfCancellationRequested();
                        if (woke)
                        {
                            _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            Resume("Ladebildschirm – Tracking läuft wieder, Karte wird gesucht …");
                            continue;
                        }
                        var screen = capture.GameBounds ?? Screen.FromRectangle(region).Bounds;
                        using var full = CaptureReadback.TryCapture(() => Grab(screen, 1));
                        if (full is not null)
                            await Task.Run(() => ProcessMiniMapFrame(region, screen, full, token), token);
                    }
                    if (Found)
                        Resume(null);
                    else
                        Pause(); // the probe wrote its own status
                    continue;
                }
                if (_wake.Task.IsCompleted)
                    _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var worldMap = _worldMapOpen;
                var area = worldMap ? _worldScreen : region;
                if (area != lastRegion)
                {
                    flow.Reset();
                    lastRegion = area;
                }
                // The world map comes in at a quarter of its size already.
                var frame = CaptureReadback.TryCapture(() => Grab(area, worldMap ? WorldMapScale : 1));
                if (frame is null)
                {
                    token.ThrowIfCancellationRequested();
                    flow.Reset();
                    frames.Clear();
                    ReportBusyFrame();
                    await Task.Delay(IntervalMs, token);
                    continue;
                }
                var flowStarted = Stopwatch.GetTimestamp();
                using (var gray = new Mat())
                {
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    var step = flow.Step(gray);
                    _cost.AddFlow(Stopwatch.GetElapsedTime(flowStarted));
                    if (step is null)
                        flowLost = true;
                    else
                        motion += Math.Sqrt(step.Value.Tx * step.Value.Tx + step.Value.Ty * step.Value.Ty)
                            + 100 * (Math.Abs(step.Value.A - 1) + Math.Abs(step.Value.B)); // zoom or turn counts as movement
                    if (step is { } shift)
                    {
                        lock (_flowGate)
                        {
                            _drift = _drift.Then(shift);
                            if (Placement is { } placement)
                                Placement = placement with { Fix = shift.Apply(placement.Fix) };
                        }
                        if (Placement is not null)
                            PlacementMoved?.Invoke();
                    }
                }

                // Flow moves only the visual overlay. It cannot establish that the minimap is still
                // visible: opening/panning the world map can also yield a perfectly good flow fit.
                // Publish player positions only from detection after checking the entire same frame.

                if (detecting is null || detecting.IsCompleted)
                {
                    if (detecting is { IsFaulted: true })
                        throw detecting.Exception!.InnerException ?? detecting.Exception;
                    var interval = DetectionIntervalMs(Found, worldMap, _misses, motion, flowLost, stillRuns);
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
                        detecting = _detecting = Task.Run(() =>
                        {
                            var detectionStarted = Stopwatch.GetTimestamp();
                            using var timed = new CostScope(() => _cost.AddDetection(Stopwatch.GetElapsedTime(detectionStarted)));
                            using (f)
                            {
                                if (worldMap)
                                    StepWorldMap(region, maps, f, token);
                                else
                                {
                                    var screen = capture.GameBounds ?? Screen.FromRectangle(region).Bounds;
                                    using var full = CaptureReadback.TryCapture(() => Grab(screen, 1));
                                    if (full is null)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        ReportBusyFrame();
                                        return;
                                    }
                                    lock (_flowGate)
                                        _drift = FrameShift.Identity;
                                    ProcessMiniMapFrame(region, screen, full, token);
                                }
                            }
                        }, token);
                        frame = null;
                    }
                }
                frame?.Dispose();

                var now = Stopwatch.GetTimestamp();
                if (Found || worldMap)
                    _lostSince = 0;
                else if (_lostSince == 0 && detecting is { IsCompleted: true })
                    _lostSince = now; // from a finished search on: building a map reference does not count
                if (_lostSince != 0 && ShouldPause(settings.Current.PauseTrackingInInstances, Found, worldMap,
                        Stopwatch.GetElapsedTime(_lostSince, now), _loadingScreenAt == 0 ? null : Stopwatch.GetElapsedTime(_loadingScreenAt, now)))
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
            Found = false;
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
            _ = TimeEndPeriod(1);
            _runGate.Release();
        }
    }

    private void ReportBusyFrame()
    {
        // Retain the last position but stop moving the overlay from an unvalidated frame.
        Found = false;
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
    internal void ProcessMiniMapFrame(Rectangle region, Rectangle screen, Mat fullFrame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!screen.Contains(region) || fullFrame.Width != screen.Width || fullFrame.Height != screen.Height)
        {
            Found = false;
            Placement = null;
            SetStatus("Kartenbereich liegt außerhalb der Aufnahme – Position bleibt unverändert.");
            return;
        }
        var maps = LoadMaps();
        _lastZoom ??= settings.Current.MiniMapZoom;
        _currentMap ??= maps.Any(m => m.Id == settings.Current.LastMap) ? settings.Current.LastMap : maps.FirstOrDefault()?.Id;
        using var crop = new Mat(fullFrame, new OpenCvSharp.Rect(region.X - screen.X, region.Y - screen.Y, region.Width, region.Height));
        using var small = new Mat();
        Cv2.Resize(fullFrame, small, new OpenCvSharp.Size(), WorldMapScale, WorldMapScale, InterpolationFlags.Area);
        if (_worldMapOpen)
        {
            StepWorldMap(region, maps, small, token);
            return;
        }
        Step(region, maps, crop, screen, small, MapWindowEmblem.Shows(fullFrame), token);
    }

    private void Step(Rectangle region, List<MapDefinition> maps, Mat frame, Rectangle screen, Mat screenFrame, bool windowHeader, CancellationToken token)
    {
        var anchor = new Point2d(region.Width * settings.Current.PlayerAnchorX, region.Height * settings.Current.PlayerAnchorY);

        // Recently found maps first: after a look at the full-screen map of another zone the player is
        // usually back on the previous map (live log 2026-10-03: Verteron map opened, back in Altgard).
        var others = _misses >= MissesBeforeOtherMaps
            ? maps.Where(m => m.Id != _currentMap && Ready(_coarse, m.Id) is not null)
                .OrderBy(m => _recentMaps.IndexOf(m.Id) is var i and >= 0 ? i : int.MaxValue)
                .Select(m => m.Id).ToList()
            : [];
        var candidate = NextCandidate(_currentMap, _misses, others, _searchTick++);
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
        var fix = LocateAt(locator, frame, locator == fineLocator ? FineFrameScale : 1.0);
        if (fix is not null && candidate != _currentMap && fix.Inliers < SwitchMinInliers)
            fix = null;
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
        if ((windowHeader || (fix is null && _misses % 4 == 3)) && ProbeWorldMap(region, definition, screen, screenFrame, token))
            return;
        token.ThrowIfCancellationRequested();
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
            if (_misses <= HoldFrames && Placement is not null)
            {
                SetStatus($"Karte kurz nicht erkannt – halte letzte Position ({locator.LastInfo.Matches} Treffer).");
                return;
            }
            Found = false;
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
            // Prepare the fine reference of the new map in the background.
            _ = Task.Run(() => Load(definition, definition.FineZoom, _fine, token), token);
        }
        _misses = 0;
        Found = true;
        var reference = fix.FrameToReference(anchor);
        var world = locator.Reference.WorldPerPixel;
        Position = new PlayerPosition(definition.Id, reference.X * world, reference.Y * world, DateTimeOffset.Now);
        // The map moved on while this frame was being matched: carry the result by the flow since then.
        lock (_flowGate)
            Placement = new MapPlacement(definition.Id, _drift.Apply(fix), world, region, anchor);
        PlacementMoved?.Invoke();
        SetStatus($"{definition.Label}: Position {Position.X:0}, {Position.Y:0} · {fix.Inliers} Treffer · Erkennung {LocateMs} ms · Overlay {Fps} fps · {CaptureMethod}");
    }

    /// <summary>
    /// Looks for the map on the whole screen (quarter size). It counts as the open world map when it is found
    /// clearly and the agreeing keypoints spread well beyond the marked small-map area.
    /// </summary>
    internal const double FineFrameScale = 0.6;

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

    private bool ProbeWorldMap(Rectangle region, MapDefinition definition, Rectangle screen, Mat small, CancellationToken token)
    {
        // Same capture as the crop being considered for a player position.
        var locator = WorldLocator(definition, token);
        locator.Reset();
        var fix = locator.Locate(small);
        token.ThrowIfCancellationRequested();
        if (fix is null || fix.Inliers < SwitchMinInliers || !SpreadsBeyond(fix, region, screen))
            return false;
        _worldMapOpen = true;
        _worldScreen = screen;
        _worldMapId = definition.Id;
        _worldMisses = 0;
        SetWorldPlacement(definition, fix, screen, locator.Reference.WorldPerPixel);
        Trace(definition.Id + " (Weltkarte)", true, fix, locator);
        return true;
    }

    /// <summary>Detection while the world map is open; closes the mode when the world map is gone.</summary>
    private void StepWorldMap(Rectangle region, List<MapDefinition> maps, Mat frame, CancellationToken token)
    {
        if (maps.FirstOrDefault(m => m.Id == _worldMapId) is not { } definition)
        {
            _worldMapOpen = false;
            return;
        }
        // The fast loop captured the world map at a quarter of its size already.
        var locator = WorldLocator(definition, token);
        var fix = locator.Locate(frame);
        token.ThrowIfCancellationRequested();
        if (fix is not null && fix.Inliers >= MinWorldInliers && SpreadsBeyond(fix, region, _worldScreen))
        {
            _worldMisses = 0;
            lock (_flowGate)
                SetWorldPlacement(definition, _drift.Apply(fix), _worldScreen, locator.Reference.WorldPerPixel);
            return;
        }
        // One miss is held (panning, zooming); the second one means the world map was closed.
        if (++_worldMisses < 2)
            return;
        Trace(definition.Id + " (Weltkarte zu)", true, null, locator);
        _worldMapOpen = false;
        _misses = 0;
        Placement = null;
        Found = false;
        PlacementMoved?.Invoke();
        SetStatus("Weltkarte geschlossen – suche die kleine Karte …");
    }

    private const int MinWorldInliers = 20;

    private void SetWorldPlacement(MapDefinition definition, MapFix fix, Rectangle screen, double worldPerPixel)
    {
        // The player stays where the small map last saw them, if the world map shows that zone.
        Point2d? player = Position is { } p && p.MapId == definition.Id ? new Point2d(p.X, p.Y) : null;
        Placement = new MapPlacement(definition.Id, fix, worldPerPixel, screen, default, 1 / WorldMapScale, true, player);
        Found = true;
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
    private void Prepare(List<MapDefinition> maps, CancellationToken token)
    {
        try
        {
            foreach (var map in maps.OrderBy(m => m.Id == _currentMap ? 0 : 1))
                Load(map, map.CoarseZoom, _coarse, token);
            if (maps.FirstOrDefault(m => m.Id == _currentMap) is { } current)
                Load(current, current.FineZoom, _fine, token);
        }
        catch (OperationCanceledException)
        {
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
        if (reference is null)
        {
            if (!Found)
                SetStatus($"{definition.Label} wird für das Tracking vorbereitet (einmalig, einige Sekunden) …");
            var pattern = Path.Combine(progress.MapDataDirectory!, definition.Tiles.Replace('/', Path.DirectorySeparatorChar));
            using var gray = MapReference.ComposeTiles(pattern, zoom);
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
            $"merkmale={info.Keypoints} treffer={info.Matches} stimmig={info.Inliers} lokal={info.Local} fehlschläge={_misses} suche={DetectionInterval}ms overlay={Fps}fps aufnahme={CaptureMethod}" +
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
                m.GetProperty("maxNativeZoom").GetInt32()))
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

    public void Dispose()
    {
        _cancel?.Cancel();
        // Wait for the loop and a detection in flight: freeing the references under a running knnMatch
        // crashed the app on close (0xc0000005, live 2026-10-03). If they do not end in time, the native
        // memory is left to the exiting process instead.
        var running = new[] { _loop, _detecting, _preparing }.OfType<Task>().ToArray();
        try
        {
            if (!Task.WaitAll(running, TimeSpan.FromSeconds(3)))
                return;
        }
        catch (AggregateException)
        {
            // cancelled or failed: finished either way
        }
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
