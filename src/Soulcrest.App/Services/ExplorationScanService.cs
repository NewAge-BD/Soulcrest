using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Capture;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;

namespace Soulcrest.App.Services;

/// <summary>Passive OCR of the fixed exploration list mask; never scrolls or clicks the game.</summary>
public sealed class ExplorationScanService(ExplorationService exploration, GameCaptureService capture, SettingsService? settings = null) : IDisposable
{
    private readonly object _lifetime = new();
    private CancellationTokenSource? _cancel;
    private int _running;
    public bool Running => Volatile.Read(ref _running) != 0;
    public string? ScanMap { get; private set; }
    public string? ScanKind { get; private set; }
    public string Status { get; private set; } = "Bereit.";
    public string? Preview { get; private set; }
    public string[] Unmatched { get; private set; } = [];
    public int Recognized { get; private set; }
    public ExplorationScanFeedback? Feedback { get; private set; }
    public event Action? Changed;
    /// <summary>
    /// Scans the list open in game. Kind "all" (tab "Scan Exploration Progress", user request
    /// 2026-10-06) reads whichever list is open, picture by picture: the Kibelisk list (numbered rows
    /// with a status) or the exploration list (sealed dungeons and strongholds), and keeps running
    /// until all lists are finished or the user stops it. A confirmed 100% completes its category.
    /// </summary>
    public void Start(string map, string kind)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        var all = kind == "all";
        var candidates = exploration.Places.Where(p => p.Map == map && (all ? p.Kind is "dungeon" or "stronghold" : p.Kind == kind)).ToArray();
        var kibeliskPlaces = exploration.Places.Where(p => p.Map == map && p.Kind == "kibelisk").ToArray();
        var character = exploration.ActiveId;
        var mapTitles = exploration.MapTitles(map);
        var cancel = new CancellationTokenSource();
        lock (_lifetime) _cancel = cancel;
        ScanMap = map; ScanKind = kind;
        Recognized = 0; Unmatched = []; Feedback = null; Preview = null; Status = "Warte auf den passenden Exploration-Tab …";
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = WindowsOcrLineReader.Create(settings?.Current.EffectiveOcrLanguage ?? "auto", 1);
                var confirmation = new Confirmation(); var seen = new HashSet<string>();
                // Kibelisks: numbered rows with their own status, see KibeliskList (user request 2026-10-06).
                var kibelisks = all || kind == "kibelisk" ? new KibeliskSession(kibeliskPlaces) : null;
                var kibeliskSettled = 0;
                var finishedKinds = new HashSet<string>();
                var fullKinds = new HashSet<string>();
                var nextPreview = DateTime.MinValue;
                while (!cancel.IsCancellationRequested)
                {
                    if (Feedback is { } previous) Feedback = previous with { Marks = [] };
                    using var masked = await GrabAsync(cancel.Token);
                    var window = _frameWindow;
                    var layout = ExplorationLayout.ForWindow(window.Size);
                    var detected = await reader.ReadAsync(masked, cancel.Token);
                    var lines = detected.Select(l => l with { X = l.X + layout.CaptureBounds.X, Y = l.Y + layout.CaptureBounds.Y }).ToArray();
                    cancel.Token.ThrowIfCancellationRequested();
                    // OCR can finish after the user has already scrolled: never restore stale boxes.
                    using (var latest = await ReadMaskAsync(window, layout.CaptureBounds, cancel.Token))
                    {
                        if (latest is null || !SameList(masked, latest))
                        {
                            HideRowMarks(); confirmation = new Confirmation();
                            await Task.Delay(250, cancel.Token); continue;
                        }
                    }
                    var rows = layout.Rows(lines);
                    settings?.RememberGameLanguage(reader.DetectedLanguage);
                    if (exploration.ActiveId != character)
                    {
                        Status = "Charakter gewechselt. Scan erneut starten."; Feedback = null; break;
                    }
                    if (!layout.ShowsMap(lines, mapTitles))
                    {
                        confirmation = new Confirmation();
                        Status = "Passende Weltkarte nicht erkannt – gewählte Karte im Spiel öffnen.";
                        Unmatched = [];
                        Feedback = new(window, layout.Bounds, Status, 0, 0, [], false);
                        UpdatePreview(masked, ref nextPreview); Changed?.Invoke();
                        await Task.Delay(250, cancel.Token); continue;
                    }
                    var listPage = kibelisks is null ? null : KibeliskList.Read(rows, layout.Bounds.Bottom);
                    if (kibelisks is not null && listPage is not null && (!all || listPage.Rows.Count > 0))
                    {
                        var settledBefore = kibelisks.Confirmed.Count;
                        kibelisks.Observe(listPage);
                        var (bound, unbound, unknownRows, open) = kibelisks.Result();
                        Unmatched = unknownRows.Concat(open).Take(100).ToArray();
                        if (kibelisks.Confirmed.Count != settledBefore || kibelisks.Ended)
                        {
                            exploration.SetDone(character, bound);
                            exploration.SetDone(character, unbound, false); // the list is the truth: unbound unchecks
                            kibeliskSettled = bound.Count + unbound.Count;
                            Recognized = seen.Count + kibeliskSettled;
                        }
                        var listed = listPage.Rows.Select(r => r.Number).ToHashSet();
                        Status = kibelisks.Complete && all ? "Kibelisk-Liste fertig – nächste Liste öffnen oder Scan stoppen."
                            : kibelisks.Complete ? "Ende der Kibelisk-Liste erkannt. Scan abgeschlossen."
                            : listPage.Rows.Count == 0 ? "Kibelisk-Liste nicht erkannt – auf der Weltkarte den Reiter Kibelisk öffnen."
                            : listed.All(kibelisks.Confirmed.ContainsKey) ? "Alle sichtbaren Einträge erkannt – weiterscrollen."
                            : "Liste wird gelesen – kurz stillhalten.";
                        // Boxes on the numbered names: green once that row is confirmed.
                        var kibeliskMarks = rows.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"^\s*\d{1,3}\s*[.,]"))
                            .Select(l => new ExplorationRowMark(new Rectangle((int)l.X, (int)l.Y, (int)l.Width, (int)l.Height),
                                int.TryParse(System.Text.RegularExpressions.Regex.Match(l.Text, @"\d+").Value, out var n) && kibelisks.Confirmed.ContainsKey(n)))
                            .ToArray();
                        Feedback = new(window, layout.Bounds, Status, listed.Count(kibelisks.Confirmed.ContainsKey), listed.Count, kibeliskMarks,
                            listed.Count > 0 && listed.All(kibelisks.Confirmed.ContainsKey));
                        UpdatePreview(masked, ref nextPreview);
                        if (kibelisks.Complete && (!all || finishedKinds.IsSupersetOf(["dungeon", "stronghold"])))
                        {
                            if (all) Status = "Alle Erkundungslisten abgeschlossen.";
                            break;
                        }
                        Changed?.Invoke();
                        confirmation = new Confirmation();
                        if (await ListMovedAsync(window, layout.CaptureBounds, masked, cancel.Token)) HideRowMarks();
                        continue;
                    }
                    var page = ExplorationList.Read(lines, candidates, masked, layout, map, mapTitles);
                    var alreadyFull = page.Kind is not null && fullKinds.Contains(page.Kind);
                    if (alreadyFull) confirmation = new Confirmation();
                    var confirmed = alreadyFull ? [] : confirmation.Observe(page);
                    exploration.SetDone(character, confirmed);
                    seen.UnionWith(confirmed); Recognized = seen.Count + kibeliskSettled;
                    Unmatched = alreadyFull ? [] : page.Unmatched.Order().Take(100).ToArray();
                    var full = alreadyFull || confirmation.FullConfirmed;
                    if (page.Kind is not null && (alreadyFull || confirmation.Finished)) finishedKinds.Add(page.Kind);
                    if (page.Kind is not null && full) fullKinds.Add(page.Kind);
                    var visible = page.Rows ?? [];
                    var ready = full || visible.Count > 0 && page.Unmatched.Length == 0 && visible.All(r => r.Id is not null && confirmation.Read.Contains(r.Id));
                    Status = full && all ? "100 % bestätigt – Kategorie fertig. Nächste offene Liste öffnen."
                        : full ? "100 % bestätigt. Kategorie vollständig abgeschlossen."
                        : page.Full ? "100 % erkannt – Abschluss wird bestätigt."
                        : page.Kind is null ? "Passende Erkundungsliste nicht erkannt – Dungeon oder Garnison öffnen."
                        : confirmation.Finished && all ? "Ende der entdeckten Orte erkannt – nächste Liste öffnen oder Scan stoppen."
                        : confirmation.Finished ? "Ende der entdeckten Orte erkannt. Scan abgeschlossen."
                        : ready ? "Sichtbare Zeilen geprüft – weiterscrollen. Nur Haken zählen als fertig."
                        : page.Unmatched.Length > 0 ? "Unklare Zeilen – kurz stillhalten oder später manuell prüfen."
                        : "Liste wird gelesen – kurz stillhalten.";
                    var marks = visible.Select(r => new ExplorationRowMark(new Rectangle((int)r.Line.X, (int)r.Line.Y, (int)r.Line.Width, (int)r.Line.Height),
                        r.Id is not null && confirmed.Contains(r.Id))).ToArray();
                    var categoryTotal = candidates.Where(p => p.Kind == page.Kind).Select(p => p.Id).Distinct().Count();
                    Feedback = new(window, layout.Bounds, Status, full ? categoryTotal : confirmation.Read.Count,
                        full || page.Full ? categoryTotal : visible.Count, marks, ready);
                    UpdatePreview(masked, ref nextPreview);
                    if ((full || confirmation.Finished) && !all) break;
                    if (all && kibelisks?.Complete == true && finishedKinds.IsSupersetOf(["dungeon", "stronghold"]))
                    {
                        Status = "Alle Erkundungslisten abgeschlossen."; break;
                    }
                    Changed?.Invoke();
                    if (full && all)
                    {
                        // This category is done. Wait for a changed screen instead of OCRing it again.
                        while (exploration.ActiveId == character && !await ListMovedAsync(window, layout.CaptureBounds, masked, cancel.Token)) { }
                        confirmation = new Confirmation();
                        continue;
                    }
                    // Watch pixels between OCR passes; a scroll must not wait for the next OCR.
                    for (var tick = 0; tick < 3; tick++)
                    {
                        await Task.Delay(250, cancel.Token);
                        using var latest = await ReadMaskAsync(window, layout.CaptureBounds, cancel.Token);
                        if (latest is null || !SameList(masked, latest))
                        {
                            HideRowMarks(); confirmation = new Confirmation(); break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { Status = "Scan gestoppt."; }
            catch (Exception e) { Status = e.Message; }
            finally
            {
                if (cancel.IsCancellationRequested) Status = "Scan gestoppt.";
                lock (_lifetime) { _cancel = null; cancel.Dispose(); }
                Interlocked.Exchange(ref _running, 0); Changed?.Invoke();
            }
        });
    }
    private void UpdatePreview(Bitmap masked, ref DateTime nextPreview)
    {
        if (DateTime.UtcNow < nextPreview) return;
        nextPreview = DateTime.UtcNow.AddSeconds(2);
        var scale = Math.Min(1d, Math.Min(320d / masked.Width, 480d / masked.Height));
        using var preview = new Bitmap(masked, new Size(Math.Max(1, (int)(masked.Width * scale)), Math.Max(1, (int)(masked.Height * scale))));
        using var png = new MemoryStream(); preview.Save(png, ImageFormat.Png);
        Preview = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
    }

    /// <summary>Watches the list between OCR passes (3 × 250 ms); true as soon as it scrolled.</summary>
    private async Task<bool> ListMovedAsync(Rectangle window, Rectangle mask, Bitmap masked, CancellationToken token)
    {
        for (var tick = 0; tick < 3; tick++)
        {
            await Task.Delay(250, token);
            using var latest = await ReadMaskAsync(window, mask, token);
            if (latest is null || !SameList(masked, latest)) return true;
        }
        return false;
    }

    private void HideRowMarks()
    {
        Status = "Liste bewegt sich – kurz stillhalten.";
        if (Feedback is { } previous) Feedback = previous with { Marks = [], Ready = false, Read = 0, Message = Status };
        Changed?.Invoke();
    }

    private async Task<Bitmap?> ReadMaskAsync(Rectangle window, Rectangle mask, CancellationToken token)
    {
        await PaceCaptureAsync(token);
        if (MonitorCapture.GameWindowBounds() != window) return null;
        mask.Offset(window.Location);
        try
        {
            var fallbacks = capture.Diagnostics.GdiFallbacks;
            var frame = capture.GrabBitmap(mask);
            if (capture.Method == "Spielfenster (WGC)" && capture.Diagnostics.GdiFallbacks == fallbacks) return frame;
            frame.Dispose(); return null;
        }
        catch (Exception error) when (CaptureReadback.IsBusy(error)) { return null; }
    }

    internal static bool SameList(Bitmap reference, Bitmap current)
    {
        if (reference.Size != current.Size) return false;
        var motion = new GridMotion(.8);
        Rectangle[] area = [new(Point.Empty, reference.Size)];
        motion.IsStill(reference, area);
        return motion.IsStill(current, area);
    }

    // Shared budget for OCR, validation and motion sampling: never burst GPU readbacks.
    private long _lastCaptureAt;
    internal async Task PaceCaptureAsync(CancellationToken token)
    {
        var remaining = 250 - (Environment.TickCount64 - _lastCaptureAt);
        if (remaining > 0) await Task.Delay((int)remaining, token);
        token.ThrowIfCancellationRequested();
        _lastCaptureAt = Environment.TickCount64;
    }

    private Rectangle _frameWindow;
    private async Task<Bitmap> GrabAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (MonitorCapture.GameWindowBounds() is not { } area)
                {
                    Status = "Warte auf das Spielfenster …";
                    Feedback = null; Changed?.Invoke();
                    await Task.Delay(750, token); continue;
                }
                await PaceCaptureAsync(token);
                var mask = ExplorationLayout.ForWindow(area.Size).CaptureBounds;
                mask.Offset(area.Location);
                var fallbacks = capture.Diagnostics.GdiFallbacks;
                var bitmap = capture.GrabBitmap(mask);
                // Never scan Soulcrest's own list after falling back to a desktop screenshot.
                if (capture.Method == "Spielfenster (WGC)" && capture.Diagnostics.GdiFallbacks == fallbacks)
                { _frameWindow = area; return bitmap; }
                bitmap.Dispose();
                Status = "Warte auf Aufnahme des Spielfensters. Listenbereich prüfen.";
            }
            catch (Exception e) when (CaptureReadback.IsBusy(e))
            {
                Status = "Aufnahme beschäftigt. Scan wird fortgesetzt …";
            }
            Feedback = null;
            Changed?.Invoke();
            await Task.Delay(750, token);
        }
    }

    internal sealed class Confirmation
    {
        private HashSet<string> _prior = [];
        private HashSet<string> _priorNames = [];
        private string? _kind;
        private bool _priorFull;
        private int _endFrames;
        private int _fullFrames;
        public bool FullConfirmed => _fullFrames >= 2;
        public bool Finished => _endFrames >= 2 || FullConfirmed;
        public HashSet<string> Read { get; private set; } = [];
        public string[] Observe(Page page)
        {
            if (_kind != page.Kind) { _prior.Clear(); _priorNames.Clear(); _endFrames = 0; _fullFrames = 0; }
            _kind = page.Kind;
            _fullFrames = page.Full ? _fullFrames + 1 : 0;
            var confirmed = page.Full ? FullConfirmed ? page.Ids.ToArray() : []
                : _priorFull ? [] : page.Ids.Where(_prior.Contains).ToArray();
            var names = page.Rows?.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet() ?? page.Ids;
            Read = names.Where(_priorNames.Contains).ToHashSet();
            _priorNames = new(names); _priorFull = page.Full;
            _prior = new(page.Ids);
            _endFrames = page.End ? _endFrames + 1 : 0;
            return confirmed;
        }
    }

    internal sealed record Page(HashSet<string> Ids, string[] Unmatched, bool End, string? Kind = null, bool Full = false,
        IReadOnlyList<ExplorationList.Row>? Rows = null);
    /// <summary>Name-parser diagnostic only; live completion additionally requires the category and its check symbols.</summary>
    internal static Page ReadNames(IReadOnlyList<OcrLine> lines, IReadOnlyList<ExplorationPlace> candidates)
    {
        var stop = lines.Where(l => ExplorationService.Normalize(l.Text) is "undiscovered" or "unentdeckt").Select(l => l.Y).DefaultIfEmpty(double.PositiveInfinity).Min();
        var ids = new HashSet<string>(); var unknown = new List<string>();
        foreach (var line in lines.Where(l => l.Y < stop).OrderBy(l => l.Y))
        {
            if (ExplorationService.Match(line.Text, candidates) is { } place) ids.Add(place.Id);
            else if (ExplorationService.Normalize(line.Text).Length >= 5) unknown.Add(line.Text);
        }
        return new(ids, unknown.Distinct().ToArray(), double.IsFinite(stop));
    }
    public void Stop() { lock (_lifetime) _cancel?.Cancel(); }
    public void Dispose() => Stop();
}
