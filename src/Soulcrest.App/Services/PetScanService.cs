using System.Drawing;
using System.Diagnostics;
using Soulcrest.App.Capture;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr;
using Soulcrest.Ocr.PetWindow;

namespace Soulcrest.App.Services;

public enum ScanConfidence { Unknown, Uncertain, Recognised, Confirmed }

/// <summary>Every visible card, in capture-region pixels, including successful and conflicting reads.</summary>
public sealed record ScanMark(Rectangle Bounds, bool ValueMissing, string Value, string Source,
    bool Conflict, bool PetUnknown, string CurrentValue)
{
    public string EntryKey { get; init; } = "";
    public string PetName { get; init; } = "";
    public Rectangle? ProgressBounds { get; init; }
    /// <summary>Matched only uncertainly at the portrait; the list asks to check it, so the overlay does too.</summary>
    public bool PetUncertain { get; init; }
    public string Label => UiText.F("{0}: {1}", UiText.T(Source), Value);
    public string Detail => Conflict ? UiText.F("Auch: {0} · prüfen", CurrentValue)
        : ValueMissing ? UiText.T("Wert? anklicken") : PetUnknown ? UiText.T("Pet? anklicken")
        : PetUncertain ? UiText.T("Pet unsicher? anklicken")
        : Source == "Panel" && CurrentValue != "?" && CurrentValue != Value ? UiText.F("Karte: {0}", CurrentValue) : "";
}

/// <summary>Ready when every visible card has a current value or panel confirmation and the grid stands still.</summary>
public sealed record ScanPageStatus(int Cards, int Read, IReadOnlyList<string> Missing, bool Stable, int TotalPets, (int Owned, int Total)? Collection,
    IReadOnlyList<ScanMark> Marks)
{
    public bool Ready => Cards > 0 && Read == Cards && Stable;
}

/// <summary>One pet seen in the in-game pet window, merged over all frames of a scan.</summary>
public sealed class ScanEntry
{
    private static long _nextManualRevision;
    private long _manualRevision;
    public required string Key { get; init; }
    public string? PetId { get; set; }
    public string? SuggestedName { get; set; }
    public ScanConfidence Confidence { get; set; }
    public int MatchScore { get; set; }
    public required string ThumbnailDataUrl { get; set; }
    public required byte[] PortraitPng { get; set; }
    public ulong PortraitHash { get; init; }
    public Dictionary<(int Level, int InLevel), int> Votes { get; } = [];
    private readonly HashSet<string> _evidence = [];
    public (int Level, int InLevel)? PanelReading { get; private set; }
    public (int Level, int InLevel)? ManualReading { get; private set; }
    public int Seen { get; set; }
    public bool Apply { get; set; } = true;

    /// <summary>
    /// From an earlier scan run: its values count until the card is seen again on a still page, then they
    /// are replaced by fresh evidence. Resetting every entry at the start lost the values of pages not
    /// scanned again, and "Übernehmen" skipped those pets silently (review 2026-10-08).
    /// </summary>
    internal bool Stale { get; set; }
    public string LastText { get; set; } = "";

    public ScanEntry Snapshot()
    {
        var copy = new ScanEntry { Key = Key, PetId = PetId, SuggestedName = SuggestedName,
            Confidence = Confidence, MatchScore = MatchScore, ThumbnailDataUrl = ThumbnailDataUrl,
            PortraitPng = (byte[])PortraitPng.Clone(), PortraitHash = PortraitHash,
            Seen = Seen, Apply = Apply, LastText = LastText, PanelReading = PanelReading, ManualReading = ManualReading,
            _manualRevision = _manualRevision };
        foreach (var vote in Votes) copy.Votes.Add(vote.Key, vote.Value);
        return copy;
    }

    public bool HasValueConflict => ManualReading is null && PanelReading is null && Votes.Count > 1;
    public (int Level, int InLevel)? CandidateReading => ManualReading ?? PanelReading ??
        (Votes.Count == 0 ? null : Votes.OrderByDescending(v => v.Value).First().Key);
    public (int Level, int InLevel)? Reading => HasValueConflict ? null : CandidateReading;

    internal void Observe(PetCardScan card)
    {
        if (card.Progress is not { } p)
            return;
        var key = (p.Level, p.SoulsInLevel);
        // Returning the cached reading of the same image is not another independent vote.
        if (_evidence.Add($"{card.Evidence}:{card.Selected}:{key}"))
            Votes[key] = Votes.GetValueOrDefault(key) + (card.Selected ? 1 : 2);
        if (PanelReading is null && ManualReading is null)
            LastText = card.Source == "balken" ? $"Balken {card.BarFill:P0}" : card.ProgressText;
    }

    internal void ConfirmValue(PetCardProgress value)
    {
        PanelReading = (value.Level, value.SoulsInLevel);
        if (ManualReading is null) LastText = $"Panel {PetScanService.ValueText(PanelReading)}";
    }

    internal void CorrectValue(PetCardProgress value)
    {
        ManualReading = (value.Level, value.SoulsInLevel);
        _manualRevision = Interlocked.Increment(ref _nextManualRevision);
        LastText = $"Manuell {PetScanService.ValueText(ManualReading)}";
    }

    internal void ResetValues()
    {
        Votes.Clear();
        _evidence.Clear();
        PanelReading = null;
        LastText = ManualReading is null ? "" : $"Manuell {PetScanService.ValueText(ManualReading)}";
    }

    internal void MergeValues(ScanEntry other)
    {
        foreach (var evidence in other._evidence) _evidence.Add(evidence);
        foreach (var vote in other.Votes)
            Votes[vote.Key] = Math.Max(Votes.GetValueOrDefault(vote.Key), vote.Value);
        PanelReading ??= other.PanelReading;
        if (other._manualRevision > _manualRevision)
        {
            ManualReading = other.ManualReading;
            _manualRevision = other._manualRevision;
        }
        if (ManualReading is not null) LastText = $"Manuell {PetScanService.ValueText(ManualReading)}";
    }
}

/// <summary>
/// Reads the in-game pet window while the user scrolls through it (docs/PET_WINDOW.md).
/// Merges frames per pet, votes on the progress text, learns unknown pets from the info panel of
/// the selected card and applies the result to the progress profile on request.
/// </summary>
public sealed class PetScanService(SettingsService settings, ProgressService progress, GameCaptureService? capture = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ScanEntry> _entries = [];
    private readonly Dictionary<string, string> _mergedKeys = [];
    private long _nextEntryId;
    private (string Key, string Name)? _pendingPanelName;
    private CancellationTokenSource? _cancel;
    private PortraitMatcher? _matcher;
    private PetWindowScanner? _scanner;
    private string? _scannerLanguage;
    private long _startedAt;
    private bool _waitingForFirstFrame;

    public bool Running { get; private set; }
    public string Status { get; private set; } = "Bereit. Im Spiel das Pet-Fenster öffnen (Tab „ALL“), dann „Scan starten“.";
    public bool StatusIsError { get; private set; }
    public int Frames { get; private set; }
    public (int Owned, int Total)? Collection { get; private set; }
    public string? PanelText { get; private set; }
    public string? PreviewDataUrl { get; private set; }

    // The last scanned picture at full size for the diagnosis package: the 420 px preview is too small to
    // see why a portrait or a value was not recognised (colleague's scan 2026-10-07).
    private readonly object _lastFrameGate = new();
    private Bitmap? _lastPicture;

    /// <summary>The last scanned picture as JPEG (full size); null before the first scan picture.</summary>
    public byte[]? LastFrameJpeg()
    {
        lock (_lastFrameGate)
        {
            if (_lastPicture is null)
                return null;
            using var stream = new MemoryStream();
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            _lastPicture.Save(stream, codec, parameters);
            return stream.ToArray();
        }
    }

    /// <summary>Visible page of the last frame (null before the first frame).</summary>
    public ScanPageStatus? Page { get; private set; }

    private readonly GridMotion _gridMotion = new();
    private readonly GridMotion _overlayMotion = new();

    public event Action? Changed;

    public IReadOnlyList<ScanEntry> Entries
    {
        get { lock (_gate) return _entries.Select(e => e.Snapshot()).ToList(); }
    }

    public Rectangle CaptureRegion => settings.Current.PetWindowRegion ?? Screen.PrimaryScreen!.Bounds;

    public void SelectRegion()
    {
        var main = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        var previous = main?.WindowState ?? FormWindowState.Normal;
        if (main is not null)
        {
            main.WindowState = FormWindowState.Minimized;
            Thread.Sleep(350);
        }
        try
        {
            if (RegionSelectorForm.Select("Rahmen um das ganze Spielfenster ziehen (mit geöffnetem Pet-Fenster)") is { } region)
                settings.Update(s => s.PetWindowRegion = region);
        }
        finally
        {
            if (main is not null)
            {
                main.WindowState = previous == FormWindowState.Minimized ? FormWindowState.Normal : previous;
                main.Activate();
            }
        }
        SetStatus($"Bereich: {CaptureRegion.Width} × {CaptureRegion.Height} px.");
    }

    public void UsePrimaryScreen()
    {
        settings.Update(s => s.PetWindowRegion = null);
        SetStatus("Bereich: ganzer Hauptbildschirm.");
    }

    /// <summary>While the reference portraits load (several seconds on the first start): the button shows it.</summary>
    public bool Starting { get; private set; }

    public async Task StartAsync()
    {
        if (Running || Starting)
            return;
        // Loading the portraits ran on the UI thread: the button stayed unchanged for seconds (user report
        // 2026-10-03). Now it says "Scan startet …" right away.
        Starting = true;
        _startedAt = Stopwatch.GetTimestamp();
        _waitingForFirstFrame = true;
        StartupTrace("requested");
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        SetStatus("Scan startet – Porträts werden geladen …");
        try
        {
            if (_loop is { } previous)
            {
                try { await previous; } catch (OperationCanceledException) { }
            }
            token.ThrowIfCancellationRequested();
            _initializing = Task.Run(() => EnsureScanner(token), token);
            await _initializing;
            token.ThrowIfCancellationRequested();
            _gridMotion.Reset();
            _overlayMotion.Reset();
            BeginNewRun();
        }
        catch (OperationCanceledException)
        {
            Starting = false;
            _waitingForFirstFrame = false;
            return;
        }
        catch (Exception exception)
        {
            Starting = false;
            _waitingForFirstFrame = false;
            SetStatus(exception.Message, error: true);
            return;
        }
        Starting = false;
        Running = true;
        StartupTrace("capture-ready");
        SetStatus("Scan läuft – langsam durch die Pet-Liste scrollen. Unbekannte Pets einmal anklicken, dann liest Soulcrest den Namen rechts.");
        _loop = Task.Run(() => LoopAsync(token));
    }

    /// <summary>A new scan run: earlier values stay until their card is seen again (see <see cref="ScanEntry.Stale"/>).</summary>
    internal void BeginNewRun()
    {
        lock (_gate)
        {
            foreach (var entry in _entries) entry.Stale = true;
            _lastFrame = [];
            _pendingPanelName = null;
            Page = null;
        }
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _waitingForFirstFrame = false;
        Running = false;
        Page = null;
        SetStatus($"Scan beendet: {Entries.Count} Pets gelesen. Ergebnisse prüfen und übernehmen.");
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _mergedKeys.Clear();
            _pendingPanelName = null;
            _lastFrame = [];
            _gridMotion.Reset();
            _overlayMotion.Reset();
            Page = null;
        }
        Frames = 0;
        Collection = null;
        Changed?.Invoke();
    }

    /// <summary>Scans a single image file (e.g. a screenshot of the pet window).</summary>
    public async Task ScanFileAsync(string path)
    {
        if (Running || Starting) return;
        Starting = true;
        _startedAt = 0;
        _waitingForFirstFrame = false;
        try
        {
            if (_loop is { } previous)
            {
                try { await previous; } catch (OperationCanceledException) { }
            }
            _initializing = Task.Run(() => EnsureScanner());
            await _initializing;
            using var bitmap = new Bitmap(path);
            await ProcessAsync(bitmap, CancellationToken.None, singlePicture: true);
            SetStatus($"Bild gelesen: {Entries.Count} Pets.");
        }
        finally { Starting = false; }
    }

    private void EnsureScanner(CancellationToken token = default)
    {
        var language = settings.Current.EffectiveOcrLanguage;
        if (_scanner is not null && _scannerLanguage == language) return;
        _scanner = null;
        _matcher?.Dispose();
        _matcher = null;
        var matcher = new PortraitMatcher();
        try
        {
            var references = progress.MapDataDirectory is { } mapdata
                ? progress.Catalog.Pets.Where(p => p.Icon is not null)
                    .Select(p => (PetId: p.Id, Path: Path.Combine(mapdata, p.Icon!))).ToArray()
                : [];
            foreach (var (petId, path) in references)
            {
                token.ThrowIfCancellationRequested();
                matcher.AddReference(petId, path);
            }
            StartupTrace($"icons-ready references={matcher.ReferenceCount}");
            var context = PortraitValidationCache.ContextFor(references, "v1:" + typeof(PortraitMatcher).Module.ModuleVersionId);
            var cache = new PortraitValidationCache(Path.Combine(AppPaths.DataDirectory, "cache", "pet-portrait-validation.json"), context);
            RepairLearnedPortraits(matcher, cache, token);
            cache.Save();
            StartupTrace($"validation-ready hits={cache.Hits} misses={cache.Misses}");
            foreach (var (petId, path) in ProgressService.LearnedPortraits())
            {
                token.ThrowIfCancellationRequested();
                matcher.AddReference(petId, path);
            }
            var scanner = new PetWindowScanner(WindowsOcrLineReader.Create(language, scale: 4), WindowsOcrLineReader.Create(language), matcher,
                WindowsOcrLineReader.Create(language, scale: 6));
            _matcher = matcher;
            _scanner = scanner;
            _scannerLanguage = language;
            StartupTrace("scanner-ready");
        }
        catch { matcher.Dispose(); throw; }
    }

    /// <summary>
    /// Checks learned portraits against the map-data icons (the matcher holds only those at this point):
    /// a portrait of pet X that clearly shows pet Y was bound wrongly and is set aside; a learned pet
    /// whose portrait clearly is a placeholder pet ("Crestlich 01") becomes that pet's real name.
    /// </summary>
    internal void RepairLearnedPortraits(PortraitMatcher iconsOnly, PortraitValidationCache cache, CancellationToken token)
    {
        // The same picture learned for two pets (Kailin / Young Kailin, 2026-10-03): at most one of them is
        // right, and as references they make both pets indistinguishable. Both are set aside and learned
        // again on the next click.
        var portraits = ProgressService.LearnedPortraits()
            .Select(p => (p.PetId, p.Path, Hash: PortraitValidationCache.ImageHash(p.Path))).ToList();
        var duplicates = portraits
            .GroupBy(p => p.Hash)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();
        foreach (var (petId, path, _) in duplicates)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(AppPaths.RejectedPortraitsDirectory);
            File.Move(path, Path.Combine(AppPaths.RejectedPortraitsDirectory, $"{petId}-doppelt.png"), overwrite: true);
        }
        var remaining = portraits.Except(duplicates).ToArray();
        var matches = new PortraitMatch?[remaining.Length];
        Parallel.For(0, remaining.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, i =>
        {
            var (petId, path, hash) = remaining[i];
            matches[i] = cache.GetOrMatch(petId, hash, () =>
            {
                using var image = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
                return image.Empty() ? new PortraitMatch(null, 0, null, 0) : iconsOnly.Match(image);
            });
        });
        // Apply repairs serially against the current catalog, including matches loaded from disk.
        for (var i = 0; i < remaining.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var (petId, path, _) = remaining[i];
            var match = matches[i]!;
            if (!match.IsConfident || match.PetId is not { } shown || shown == petId)
                continue;
            if (progress.IsLearned(petId) && progress.IsPlaceholder(shown))
            {
                progress.MergeLearnedIntoPlaceholder(petId, shown);
                continue;
            }
            if (progress.IsLearned(petId))
                continue; // a learned pet resembling a named pet: leave it, the user decides in the table
            Directory.CreateDirectory(AppPaths.RejectedPortraitsDirectory);
            File.Move(path, Path.Combine(AppPaths.RejectedPortraitsDirectory, $"{petId}-zeigt-{shown}.png"), overwrite: true);
        }
    }

    /// <summary>
    /// The pet window without its middle (animated pet model): grid and collection on the left, info panel
    /// on the right; the middle stays black (user request 2026-10-03). Only the game window is captured.
    /// </summary>
    private Bitmap GrabWithoutMiddle(Rectangle region)
    {
        var from = (int)(region.Width * PetWindowScanner.MiddleFrom);
        var to = (int)(region.Width * PetWindowScanner.MiddleTo);
        var left = new Rectangle(region.X, region.Y, from, region.Height);
        var right = new Rectangle(region.X + to, region.Y, region.Width - to, region.Height);
        using var picture = new OpenCvSharp.Mat(region.Height, region.Width, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(0));
        foreach (var (part, x) in new[] { (left, 0), (right, to) })
        {
            using var grabbed = GrabPart(part);
            using var target = new OpenCvSharp.Mat(picture, new OpenCvSharp.Rect(x, 0, grabbed.Width, grabbed.Height));
            grabbed.CopyTo(target);
        }
        return BitmapMat.ToBitmapFast(picture);
    }

    private OpenCvSharp.Mat GrabPart(Rectangle region)
    {
        if (capture is not null) return capture.Grab(region);
        using var bitmap = ScreenCapture.Capture(region);
        return BitmapMat.ToBgr(bitmap);
    }

    private Bitmap GrabGrid(Rectangle region)
    {
        using var image = GrabPart(new Rectangle(region.X, region.Y, (int)(region.Width * PetWindowScanner.MiddleFrom), region.Height));
        return BitmapMat.ToBitmapFast(image);
    }

    private void InvalidateMovingPage()
    {
        if (Page is { } page) Page = page with { Stable = false };
        _gridMotion.Reset();
        _overlayMotion.Reset();
        lock (_gate) _pendingPanelName = null;
        Changed?.Invoke();
    }

    private void StartupTrace(string stage)
    {
        if (_startedAt == 0) return;
        try
        {
            LogFile.Append("pet-scan.log", $"{DateTimeOffset.Now:O} START {stage} elapsedMs={Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds:0}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var region = CaptureRegion;
                await ReadFrameAsync(() => GrabWithoutMiddle(region), token, () => GrabGrid(region));
                await Task.Delay(300, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Running = false;
            try
            {
                LogFile.Append("pet-scan.log",
                    $"{DateTimeOffset.Now:O} SCAN ERROR 0x{exception.HResult:X8}: {exception}{Environment.NewLine}");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            SetStatus("Scan abgebrochen: " + exception.Message, error: true);
        }
    }

    // Keep an incomplete composite out of OCR: if either side is busy, dispose the
    // partial image and retry the whole frame on the next cancellable loop tick.
    internal async Task<bool> ReadFrameAsync(Func<Bitmap> readFrame, CancellationToken token, Func<Bitmap>? readGrid = null)
    {
        token.ThrowIfCancellationRequested();
        using var bitmap = CaptureReadback.TryCapture(readFrame);
        token.ThrowIfCancellationRequested();
        if (bitmap is null)
        {
            Page = null;
            SetStatus(CaptureReadback.Waiting);
            return false;
        }
        var recovering = Status == CaptureReadback.Waiting;
        await ProcessAsync(bitmap, token, readGrid: readGrid);
        if (recovering && !StatusIsError)
            SetStatus("Scan läuft – langsam durch die Pet-Liste scrollen. Unbekannte Pets einmal anklicken, dann liest Soulcrest den Namen rechts.");
        return true;
    }

    // Card values count only from pictures of a list standing still: mid-scroll frames gave Sylphen
    // (12/25) a vote for 11 and Rafflesia (4/25) one for MAX (live log 2026-10-03).
    private bool _voting = true;

    private async Task ProcessAsync(Bitmap bitmap, CancellationToken token, bool singlePicture = false, Func<Bitmap>? readGrid = null)
    {
        // Hide old labels as soon as a captured grid moves, before OCR of a new page takes time.
        if (Page is { Stable: true } displayedPage && !_overlayMotion.IsStill(bitmap, displayedPage.Marks.Select(m => m.Bounds).ToList()))
        {
            Page = displayedPage with { Stable = false };
            Changed?.Invoke();
        }
        var bounds = Page?.Marks.Select(m => m.Bounds).ToArray();
        if (bounds is not { Length: > 0 })
            bounds = [new Rectangle(0, (int)(bitmap.Height * .11), (int)(bitmap.Width * .34), (int)(bitmap.Height * .82))];
        var scan = readGrid is null ? await _scanner!.ScanAsync(bitmap, token)
            : await ScanMotionGuard.ReadAsync(bitmap, bounds, readGrid, t => _scanner!.ScanAsync(bitmap, t), InvalidateMovingPage, token);
        token.ThrowIfCancellationRequested();
        if (scan is null) return;
        Frames++;
        if (_waitingForFirstFrame)
        {
            _waitingForFirstFrame = false;
            StartupTrace("first-frame");
        }
        PreviewDataUrl = ScreenCapture.ToDataUrl(bitmap, 420);
        lock (_lastFrameGate)
        {
            _lastPicture?.Dispose();
            _lastPicture = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
        }
        if (scan.Collection is { } collection)
            Collection = collection;
        PanelText = scan.Panel is { } panel ? $"{panel.Name} · Stufe {panel.Level?.ToString() ?? "?"}" : null;
        if (scan.Problem is not null)
        {
            Page = null;
            SetStatus(scan.Problem, error: true);
            return;
        }

        var stable = _gridMotion.IsStill(bitmap, scan.Cards.Select(c => c.Bounds).ToList());
        _overlayMotion.IsStill(bitmap, scan.Cards.Select(c => c.Bounds).ToList());
        if (!stable && !singlePicture)
        {
            lock (_gate)
            {
                // A moving frame must not create unknown rows, change identities or replace the last
                // stable page bindings. Previously only its votes were blocked (Nachtest 3, two empty rows).
                _pendingPanelName = null;
                _voting = false;
                Page = new ScanPageStatus(scan.Cards.Count, 0, [], false, _entries.Count, Collection, []);
                Trace(scan, [], stable: false);
            }
            StatusIsError = false;
            Changed?.Invoke();
            return;
        }
        settings.RememberGameLanguage(scan.Language ?? scan.Panel?.Language);
        lock (_gate)
        {
            _voting = true;
            var frame = new List<(PetCardScan Card, ScanEntry Entry)>();
            var used = new HashSet<ScanEntry>();
            // Same page as the previous frame (the portraits at the same places are the same): a card keeps
            // the pet the info panel confirmed for it. Near-identical portraits (Kailin / Young Kailin) are
            // told apart only by their place: unselected, both fell back to "unknown" (live log 2026-10-03).
            var samePage = SamePage(scan.Cards);
            var bound = new HashSet<PetCardScan>();
            foreach (var card in samePage ? scan.Cards : [])
            {
                if (PreviousAt(card) is { Confidence: ScanConfidence.Confirmed } previous && !used.Contains(previous))
                {
                    frame.Add((card, MergeInto(previous, card)));
                    used.Add(previous);
                    bound.Add(card);
                }
            }
            // Each pet goes to the card that matches it best; the others are unknown in this frame
            // (two visible cards are never the same pet).
            var winners = scan.Cards.Where(c => !c.Selected && !bound.Contains(c) && c.Match.IsPlausible)
                .GroupBy(c => c.Match.PetId!)
                .ToDictionary(g => g.Key, g => g.MaxBy(c => c.Match.Score)!);
            foreach (var card in scan.Cards.Where(c => !c.Selected && !bound.Contains(c)).OrderByDescending(c => c.Match.Score))
            {
                var wins = card.Match.IsPlausible && winners[card.Match.PetId!] == card;
                frame.Add((card, Merge(card, used, wins)));
                used.Add(frame[^1].Entry);
            }
            // The selected card glows, which changes its portrait hash and hides its text: if the
            // previous frame had an unselected card at the same position, it is the same pet - but only
            // while the list stands still. Scrolled by exactly one row, another pet sits there (learned
            // portraits of "Airon" and "Kalgolem" showed Papis and Ashen Spider, 2026-10-03).
            foreach (var card in scan.Cards.Where(c => c.Selected && !bound.Contains(c)))
            {
                // Also the previous frame's selected card: it stays selected for many frames (live log
                // 2026-10-03: from the second frame on it fell back to a new unknown entry).
                var previous = samePage ? PreviousAt(card) : null;
                frame.Add((card, previous is not null && !used.Contains(previous) ? MergeInto(previous, card) : Merge(card, used)));
                used.Add(frame[^1].Entry);
            }
            if (_voting)
                LearnFromPanel(scan, frame, singlePicture);
            else
                _pendingPanelName = null;
            _lastFrame = frame;
            Page = DescribePage(frame, stable);
            Trace(scan, frame, stable);
        }
        StatusIsError = false;
        Changed?.Invoke();
    }

    private List<(PetCardScan Card, ScanEntry Entry)> _lastFrame = [];

    /// <summary>Entry of the previous frame's card at the same place.</summary>
    private ScanEntry? PreviousAt(PetCardScan card)
    {
        var before = _lastFrame.FirstOrDefault(f => f.Card.Column == card.Column
            && Math.Abs(f.Card.Bounds.Y - card.Bounds.Y) < card.Bounds.Height / 3);
        return before.Entry is { } entry && _entries.Contains(entry) ? entry : null;
    }

    /// <summary>
    /// Whether the previous frame showed the same page: most cards that are unselected in both frames show
    /// the same portrait at the same place. Selecting a card changes two cards (glow), scrolling by a row
    /// changes all of them; the idle animation in the middle is not part of the picture.
    /// </summary>
    private bool SamePage(IReadOnlyList<PetCardScan> cards)
    {
        int compared = 0, same = 0;
        foreach (var card in cards.Where(c => !c.Selected))
        {
            var before = _lastFrame.FirstOrDefault(f => f.Card.Column == card.Column && !f.Card.Selected
                && Math.Abs(f.Card.Bounds.Y - card.Bounds.Y) < card.Bounds.Height / 3);
            if (before.Card is null)
                continue;
            compared++;
            if (PetWindowScanner.HashDistance(before.Card.PortraitHash, card.PortraitHash) <= 8)
                same++;
        }
        return compared >= 3 && same >= compared * 3 / 4;
    }

    internal ScanPageStatus DescribePage(List<(PetCardScan Card, ScanEntry Entry)> frame, bool stable)
    {
        // Rows of the visible page, top to bottom; columns left/middle/right.
        var rowTops = frame.Select(f => f.Card.Bounds.Y).Distinct().Order().ToList();
        // A card is done when its value is read and the pet is known; otherwise the user should click it
        // (the info panel then gives name and exact progress). Unknown and uncertain pets are marked like
        // in the result list, locked ones without souls (0/5) too: the overlay used to skip them and showed
        // uncertain matches as read while the list asked to check them (user report 2026-10-10).
        var marks = frame.Select(f => DescribeCard(f.Card, f.Entry) with
        {
            PetName = f.Entry.PetId is { } id ? progress.Catalog.Find(id)?.DisplayName(settings.Current.NameLanguage) ?? id
                : f.Entry.SuggestedName is { } guess ? $"? {guess}" : UiText.T("Pet unbekannt"),
        }).ToList();
        var open = frame.Where((f, i) => marks[i].ValueMissing || marks[i].Conflict || marks[i].PetUnknown || marks[i].PetUncertain)
            .OrderBy(f => f.Card.Bounds.Y).ThenBy(f => f.Card.Column)
            .ToList();
        var missing = open
            .Select(f =>
            {
                var row = rowTops.FindIndex(y => Math.Abs(y - f.Card.Bounds.Y) < f.Card.Bounds.Height / 3) + 1;
                var column = f.Card.Column switch { 0 => "links", 1 => "Mitte", _ => "rechts" };
                var mark = DescribeCard(f.Card, f.Entry);
                var reason = mark.Conflict ? "Wert widersprüchlich" : mark.ValueMissing ? "Wert unlesbar"
                    : mark.PetUnknown ? "Pet unbekannt" : "Pet unsicher";
                return $"Reihe {row} {column} ({reason})";
            })
            .ToList();
        return new ScanPageStatus(frame.Count, frame.Count - missing.Count, missing, stable, _entries.Count, Collection, marks);
    }

    internal static string ValueText((int Level, int InLevel)? value) => value is { } v
        ? v.Level == 3 ? "MAX" : $"{v.InLevel}/{(v.Level switch { 0 => 5, 1 => 25, _ => 75 })}" : "?";

    internal static ScanMark DescribeCard(PetCardScan card, ScanEntry entry)
    {
        var current = card.Progress is { } p ? (p.Level, p.SoulsInLevel) : ((int Level, int InLevel)?)null;
        var confirmed = entry.ManualReading ?? entry.PanelReading;
        var value = confirmed ?? current ?? entry.CandidateReading;
        var conflict = entry.HasValueConflict || confirmed is null && current is not null
            && entry.CandidateReading is { } previous && current != previous;
        var missing = confirmed is null && (current is null || entry.Reading is null);
        var unknown = entry.PetId is null;
        var uncertain = !unknown && entry.Confidence == ScanConfidence.Uncertain;
        var source = entry.ManualReading is not null ? "Manuell" : entry.PanelReading is not null ? "Panel" : current is not null ? "Scan" : "Bisher";
        var alternative = conflict ? string.Join(", ", entry.Votes.Keys.Where(v => v != value).Select(v => ValueText(v))) : ValueText(current);
        return new ScanMark(card.Bounds, missing, ValueText(value), source, conflict, unknown, alternative)
            { EntryKey = entry.Key, ProgressBounds = card.ProgressBounds, PetUncertain = uncertain };
    }

    /// <summary>Corrects the entry captured when the editor opened, even if the user scrolls meanwhile.</summary>
    public bool CorrectValue(string key, string text)
    {
        if (ParseCorrection(text) is not { } value) return false;
        lock (_gate)
        {
            while (_mergedKeys.TryGetValue(key, out var survivor)) key = survivor;
            if (_entries.FirstOrDefault(e => e.Key == key) is not { } entry) return false;
            entry.CorrectValue(value);
            if (_lastFrame.Count > 0 && Page is { } page) Page = DescribePage(_lastFrame, page.Stable);
        }
        Changed?.Invoke();
        return true;
    }

    internal static PetCardProgress? ParseCorrection(string text)
    {
        var compact = text.Trim().ToUpperInvariant().Replace(" ", "");
        var parsed = PetWindowText.ParseCard(compact);
        return parsed is not null && compact == (parsed.IsMax ? "MAX" : $"{parsed.SoulsInLevel}/{parsed.Needed}") ? parsed : null;
    }

    /// <summary>
    /// Entry for a card. Two cards visible at the same time are never the same pet (<paramref name="used"/>):
    /// near-identical portraits (Kailin / Young Kailin) shared one unknown entry, and clicking one renamed
    /// it away from the other (user report 2026-10-03).
    /// </summary>
    internal ScanEntry Merge(PetCardScan card, HashSet<ScanEntry>? used = null, bool wins = true)
    {
        var match = card.Match;
        string? petId = match.IsPlausible && wins ? match.PetId : null;
        var entry = petId is not null
            ? _entries.FirstOrDefault(e => e.PetId == petId && used?.Contains(e) != true && !Contradicts(e, card))
            : null;
        if (entry is null && petId is not null && _entries.Any(e => e.PetId == petId && (used?.Contains(e) == true || Contradicts(e, card))))
            petId = null; // the pet is already on another card of this frame, or is owned where this card is locked
        if (entry is null && petId is not null && !card.Selected)
        {
            // The same card can first be unknown, then match after scrolling back (Kuru Worker,
            // German retest: hashes differ by one bit). Promote its original row, preserving its
            // key and manual corrections. Ambiguous portraits and simultaneously visible cards
            // must remain separate; a loose hash match is not proof of identity.
            var unknown = _entries.Where(e => e.PetId is null && used?.Contains(e) != true
                && !Contradicts(e, card) && !e.HasValueConflict && card.Progress is { } value
                && e.Votes.Count == 1 && e.Votes.ContainsKey((value.Level, value.SoulsInLevel))
                && PetWindowScanner.HashDistance(e.PortraitHash, card.PortraitHash) <= 2).ToList();
            if (unknown.Count == 1) entry = unknown[0];
        }
        entry ??= petId is null
            ? _entries.FirstOrDefault(e => used?.Contains(e) != true && !Contradicts(e, card) && !OtherValue(e, card) && PetWindowScanner.HashDistance(e.PortraitHash, card.PortraitHash) <= 8)
            : null;
        if (entry is null)
        {
            entry = new ScanEntry
            {
                Key = UniqueKey(petId ?? $"unknown-{card.PortraitHash:x16}"),
                PortraitHash = card.PortraitHash,
                PortraitPng = card.PortraitPng,
                ThumbnailDataUrl = "data:image/png;base64," + Convert.ToBase64String(card.PortraitPng),
            };
            _entries.Add(entry);
        }
        return MergeInto(entry, card, allowPet: petId is not null);
    }

    /// <summary>
    /// A pet is either locked (x/5, no level badge) or owned (level 1 and up), never both: a locked card
    /// whose portrait resembles an owned pet is another pet (live log 2026-10-03: "2/5" merged into Kuru
    /// Overseer 0/25, "0/5" into Klaw Scout 2/75).
    /// </summary>
    private static bool Contradicts(ScanEntry entry, PetCardScan card)
    {
        if (entry.Reading is not { } reading || card.Progress is not { } progress)
            return false;
        return (reading.Level == 0) != (progress.Level == 0);
    }

    /// <summary>
    /// An unknown entry already read with another value is another pet: values do not change during a
    /// scan. Mudthorn (2/25) went into an unknown entry of 1/12 by a similar portrait and showed 1/12 until
    /// its own votes won (colleague's scan log 2026-10-07). Only pictures of a list standing still count.
    /// </summary>
    private bool OtherValue(ScanEntry entry, PetCardScan card) =>
        _voting && entry.Reading is { } reading && card.Progress is { } progress
        && (reading.Level, reading.InLevel) != (progress.Level, progress.SoulsInLevel);

    private string UniqueKey(string key)
    {
        // Never reuse a target while this service lives, including after Clear or a panel merge.
        // An editor opened before Clear must not correct a later, unrelated card with the same hash.
        return $"{key}-{++_nextEntryId}";
    }

    private ScanEntry MergeInto(ScanEntry entry, PetCardScan card, bool allowPet = true)
    {
        var match = card.Match;
        string? petId = match.IsPlausible && allowPet ? match.PetId : null;
        entry.Seen++;
        if (_voting && entry.Stale)
        {
            entry.ResetValues();
            entry.Stale = false;
        }
        if (_voting)
            entry.Observe(card);
        if (entry.Confidence == ScanConfidence.Confirmed)
            return entry;
        if (petId is not null && entry.PetId != petId && _entries.Any(e => e != entry && e.PetId == petId))
            petId = null; // another entry holds this pet already
        if (petId is not null && match.Score > entry.MatchScore)
        {
            entry.PetId = petId;
            entry.MatchScore = match.Score;
            entry.Confidence = match.IsConfident ? ScanConfidence.Recognised : ScanConfidence.Uncertain;
            entry.PortraitPng = card.PortraitPng;
            entry.ThumbnailDataUrl = "data:image/png;base64," + Convert.ToBase64String(card.PortraitPng);
        }
        else if (petId is null && entry.PetId is null)
        {
            entry.Confidence = ScanConfidence.Unknown;
            entry.SuggestedName = match.PetId is { } guess ? progress.Catalog.Find(guess)?.En : null;
            entry.Apply = false;
        }
        return entry;
    }

    /// <summary>The selected card's name and level are shown in the info panel: confirm or learn that pet.</summary>
    private string? _lastTrace;

    /// <summary>
    /// Log for live diagnosis (%LOCALAPPDATA%\Soulcrest\logs\pet-scan.log): one line whenever the visible
    /// page, the selection or the panel changes.
    /// </summary>
    private void Trace(PetWindowScan scan, List<(PetCardScan Card, ScanEntry Entry)> frame, bool stable)
    {
        var rows = frame.Select(f => f.Card.Bounds.Y).Distinct().Order().ToList();
        string Position(PetCardScan c) => $"r{rows.FindIndex(y => Math.Abs(y - c.Bounds.Y) < c.Bounds.Height / 3) + 1}c{c.Column + 1}";
        var cards = string.Join(" ", frame.OrderBy(f => f.Card.Bounds.Y).ThenBy(f => f.Card.Column).Select(f =>
            $"{Position(f.Card)}{(f.Card.Selected ? "*" : "")}={(f.Card.Progress is { } p ? (p.IsMax ? "MAX" : $"{p.SoulsInLevel}/{p.Needed}") : "?")}" +
            $"→{f.Entry.PetId ?? "unbekannt"}:{(f.Entry.Reading is { } r ? $"{r.Level}/{r.InLevel}" : "-")}" +
            $"[key={f.Entry.Key};hash={f.Card.PortraitHash:x16};src={f.Card.Source};bar={f.Card.BarFill:0.000};conflict={f.Entry.HasValueConflict};raw={f.Card.ProgressText.Replace('\r', ' ').Replace('\n', ' ')}]"));
        var panel = scan.Panel is { } pn ? $"{pn.Name} {(pn.Progress is { } pp ? (pp.IsMax ? "MAX" : $"{pp.SoulsInLevel}/{pp.Needed}") : "")}" : "-";
        var line = $"{Page?.Read}/{Page?.Cards} still={stable} entries={_entries.Count} owned={_entries.Count(e => e.Reading?.Level >= 1)} collection={Collection} panel=[{panel}] {cards}";
        if (line == _lastTrace)
            return;
        _lastTrace = line;
        try
        {
            LogFile.Append("pet-scan.log", $"{DateTime.Now:HH:mm:ss.f} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    internal void LearnFromPanel(PetWindowScan scan, List<(PetCardScan Card, ScanEntry Entry)> frame, bool singlePicture)
    {
        if (scan.Panel?.Name is not { Length: > 1 } name || frame.Count(f => f.Card.Selected) != 1)
        {
            _pendingPanelName = null;
            return;
        }
        var (card, entry) = frame.Single(f => f.Card.Selected);
        if (!_entries.Contains(entry))
            return;
        var named = progress.FindByName(name);
        // A tab heading is not a pet name, even when old user data already contains that OCR mistake.
        if (name.Contains("Owned", StringComparison.OrdinalIgnoreCase) || name.Contains("Effect", StringComparison.OrdinalIgnoreCase)
            || PetWindowText.IsInsightCaption(name) || name.Contains("Sammlungsfortschritt", StringComparison.OrdinalIgnoreCase))
        {
            _pendingPanelName = null;
            return;
        }
        var observation = (entry.Key, name);
        var repeated = _pendingPanelName == observation;
        _pendingPanelName = observation;
        // Learning a new name is persistent: require two consecutive stable frames in a live scan.
        if (named is null && !singlePicture && !repeated) return;
        // The panel still shows the previously selected pet (it follows the click a frame later): that pet
        // is visible on another card, or this card was already confirmed as another pet.
        if (named is not null && (frame.Any(f => f.Entry != entry && f.Entry.PetId == named.Id)
            || entry.Confidence == ScanConfidence.Confirmed && entry.PetId != named.Id))
            return;
        // Learn the clean (unselected) portrait when there is one; the glowing one matches worse later.
        var portraitPng = entry.PortraitPng;
        var pet = progress.FindByName(name);
        var language = scan.Panel.Language ?? (settings.Current.EffectiveOcrLanguage == "de-DE" ? "de" : "en");
        var recognised = (entry.Confidence == ScanConfidence.Recognised
            || language == "de" && entry.Confidence == ScanConfidence.Confirmed) ? entry.PetId : null;
        if (pet is not null && recognised is not null && recognised != pet.Id)
        {
            // The portrait clearly shows another pet than the panel names: the card and the panel do not
            // belong together (yet). Learn nothing rather than something wrong.
            SetStatus($"Panel „{name}“ passt nicht zum Porträt ({progress.Catalog.Find(recognised)?.En}) – nichts gelernt.");
            return;
        }
        if (pet is null && recognised is not null && language == "de")
        {
            // A German panel must not create a second ID for a pet already learned in English.
            if (progress.IsLearned(recognised)) progress.NameLearnedGerman(recognised, name);
            pet = progress.Catalog.Find(recognised);
        }
        if (pet is null && recognised is not null && progress.IsPlaceholder(recognised))
        {
            // Map-data pet known only by its icon name ("KrallWar 01 V01"): the panel gives its real name.
            progress.NamePlaceholder(recognised, name);
            pet = progress.Catalog.Find(recognised);
        }
        if (pet is null)
        {
            pet = progress.LearnPet(name, portraitPng, language);
            _matcher?.AddReference(pet.Id, Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{pet.Id}.png"));
        }
        else if ((entry.PetId != pet.Id || entry.Confidence != ScanConfidence.Confirmed)
            && ProgressService.SaveLearnedPortrait(pet.Id, portraitPng) is { } learnedPath)
        {
            _matcher?.AddReference(pet.Id, learnedPath);
        }
        // Another entry may already hold this pet (seen before it was selected): merge into it - unless that
        // entry contradicts this card (owned there, locked here): then it was a wrong portrait match of
        // another card and goes back to "unknown" (live 2026-10-03: a 0/25 card was taken for Kuru Overseer,
        // whose card is locked 2/5).
        var existing = _entries.FirstOrDefault(e => e != entry && e.PetId == pet.Id);
        if (existing is not null && Contradicts(existing, card))
        {
            existing.PetId = null;
            existing.Confidence = ScanConfidence.Unknown;
            existing.MatchScore = 0;
            existing.Apply = false;
            existing = null;
        }
        if (existing is not null)
        {
            existing.MergeValues(entry);
            _mergedKeys[entry.Key] = existing.Key;
            _entries.Remove(entry);
            // The frame must point at the surviving entry (page status, next frame's binding).
            var index = frame.FindIndex(f => f.Entry == entry);
            if (index >= 0)
                frame[index] = (frame[index].Card, existing);
            entry = existing;
        }
        entry.PetId = pet.Id;
        entry.Confidence = ScanConfidence.Confirmed;
        entry.Apply = true;
        // The exact panel value is authoritative; repeated card reads cannot outvote it.
        if (scan.Panel.Progress is { } exact)
        {
            entry.ConfirmValue(exact);
        }
        else if (scan.Panel.Level is 3)
        {
            entry.ConfirmValue(new PetCardProgress(3, 0, 0, true));
        }
    }

    public void SetApply(string key, bool apply)
    {
        lock (_gate)
            if (_entries.FirstOrDefault(e => e.Key == key) is { } entry) entry.Apply = apply;
        Changed?.Invoke();
    }

    public void Assign(ScanEntry entry, string? petId)
    {
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Key == entry.Key)!;
            if (entry is null) return;
            entry.PetId = string.IsNullOrEmpty(petId) ? null : petId;
            entry.Confidence = entry.PetId is null ? ScanConfidence.Unknown : ScanConfidence.Confirmed;
            entry.Apply = entry.PetId is not null;
            if (entry.PetId is not null)
                ProgressService.SaveLearnedPortrait(entry.PetId, entry.PortraitPng);
        }
        Changed?.Invoke();
    }

    /// <summary>Writes level and souls of all checked entries into the progress profile.</summary>
    public int ApplySelected()
    {
        var values = Entries.Where(e => e.Apply && e.PetId is not null && e.Reading is not null)
            .Select(e => (e.PetId!, e.Reading!.Value.Level, e.Reading.Value.InLevel)).ToList();
        progress.SetLevels(values);
        var applied = values.Count;
        SetStatus($"{applied} Pets übernommen.");
        return applied;
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
        Changed?.Invoke();
    }

    private Task? _loop;
    private Task? _initializing;

    public void Dispose()
    {
        _cancel?.Cancel();
        lock (_lastFrameGate)
        {
            _lastPicture?.Dispose();
            _lastPicture = null;
        }
        // Not under a running portrait comparison (it reads the matcher's native descriptors): wait for
        // the scan to end, or leave the memory to the exiting process.
        try
        {
            if (!Task.WaitAll(new[] { _loop, _initializing }.OfType<Task>().ToArray(), TimeSpan.FromSeconds(3)))
                return;
        }
        catch (AggregateException)
        {
            // cancelled or failed: finished either way
        }
        _matcher?.Dispose();
    }
}
