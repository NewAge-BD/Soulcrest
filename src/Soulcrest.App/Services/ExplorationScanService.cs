using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Capture;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;

namespace Soulcrest.App.Services;

/// <summary>Passive OCR of the fixed exploration list mask; never scrolls or clicks the game.</summary>
public sealed class ExplorationScanService(ExplorationService exploration, GameCaptureService capture) : IDisposable
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
    /// until stopped, so all lists can be scanned one after the other.
    /// </summary>
    public void Start(string map, string kind)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        var all = kind == "all";
        var candidates = exploration.Places.Where(p => p.Map == map && (all ? p.Kind is "dungeon" or "stronghold" : p.Kind == kind)).ToArray();
        var kibeliskPlaces = exploration.Places.Where(p => p.Map == map && p.Kind == "kibelisk").ToArray();
        var character = exploration.ActiveId;
        var cancel = new CancellationTokenSource();
        lock (_lifetime) _cancel = cancel;
        ScanMap = map; ScanKind = kind;
        Recognized = 0; Unmatched = []; Feedback = null; Preview = null; Status = "Warte auf den passenden Exploration-Tab …";
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = WindowsOcrLineReader.Create("auto", 1);
                var confirmation = new Confirmation(); var seen = new HashSet<string>();
                // Kibelisks: numbered rows with their own status, see KibeliskList (user request 2026-10-06).
                var kibelisks = all || kind == "kibelisk" ? new KibeliskSession(kibeliskPlaces) : null;
                var kibeliskSettled = 0;
                var unknown = new HashSet<string>();
                var nextPreview = DateTime.MinValue;
                while (!cancel.IsCancellationRequested)
                {
                    if (Feedback is { } previous) Feedback = previous with { Marks = [] };
                    using var masked = await GrabAsync(cancel.Token);
                    var window = _frameWindow;
                    var layout = ExplorationLayout.ForWindow(window.Size);
                    var detected = await reader.ReadAsync(masked, cancel.Token);
                    var lines = detected.Select(l => l with { X = l.X + layout.Bounds.X, Y = l.Y + layout.Bounds.Y }).ToArray();
                    cancel.Token.ThrowIfCancellationRequested();
                    // OCR can finish after the user has already scrolled: never restore stale boxes.
                    using (var latest = await ReadMaskAsync(window, layout.Bounds, cancel.Token))
                    {
                        if (latest is null || !SameList(masked, latest))
                        {
                            HideRowMarks(); confirmation = new Confirmation();
                            await Task.Delay(250, cancel.Token); continue;
                        }
                    }
                    var rows = layout.Rows(lines);
                    var listPage = kibelisks is null ? null : KibeliskList.Read(rows, layout.Bounds.Bottom);
                    if (kibelisks is not null && listPage is not null && (!all || listPage.Rows.Count > 0))
                    {
                        var settledBefore = kibelisks.Confirmed.Count;
                        kibelisks.Observe(listPage);
                        if (kibelisks.Confirmed.Count != settledBefore || kibelisks.Ended)
                        {
                            var (bound, unbound, unknownRows, open) = kibelisks.Result();
                            exploration.SetDone(character, bound);
                            exploration.SetDone(character, unbound, false); // the list is the truth: unbound unchecks
                            kibeliskSettled = bound.Count + unbound.Count;
                            Recognized = seen.Count + kibeliskSettled;
                            Unmatched = unknown.Order().Concat(unknownRows).Concat(open).Take(100).ToArray();
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
                        if (kibelisks.Complete && !all) break;
                        Changed?.Invoke();
                        if (await ListMovedAsync(window, layout.Bounds, masked, cancel.Token)) HideRowMarks();
                        continue;
                    }
                    var page = ReadPage(rows, candidates);
                    var confirmed = confirmation.Observe(page);
                    exploration.SetDone(character, confirmed);
                    seen.UnionWith(confirmed); Recognized = seen.Count + kibeliskSettled;
                    unknown.UnionWith(page.Unmatched); Unmatched = unknown.Order().Take(100).ToArray();
                    var ready = page.Ids.Count > 0 && page.Unmatched.Length == 0 && page.Ids.All(confirmed.Contains);
                    Status = confirmation.Finished && all ? "Undiscovered erkannt – nächste Liste öffnen oder Scan stoppen."
                        : confirmation.Finished ? "Undiscovered erkannt. Scan abgeschlossen."
                        : ready ? "Alle sichtbaren Namen erkannt – weiterscrollen."
                        : page.Unmatched.Length > 0 ? "Unklare Zeilen – kurz stillhalten oder später manuell prüfen."
                        : "Liste wird gelesen – kurz stillhalten.";
                    var marks = rows.Where(l => ExplorationService.Normalize(l.Text).Length >= 5 && ExplorationService.Normalize(l.Text) is not ("undiscovered" or "unentdeckt"))
                        .Select(l => new ExplorationRowMark(new Rectangle((int)l.X, (int)l.Y, (int)l.Width, (int)l.Height),
                            ExplorationService.Match(l.Text, candidates) is { } p && confirmed.Contains(p.Id))).ToArray();
                    Feedback = new(window, layout.Bounds, Status, confirmed.Length, page.Ids.Count + page.Unmatched.Length, marks, ready);
                    UpdatePreview(masked, ref nextPreview);
                    if (confirmation.Finished && !all) break;
                    Changed?.Invoke();
                    // Watch pixels between OCR passes; a scroll must not wait for the next OCR.
                    for (var tick = 0; tick < 3; tick++)
                    {
                        await Task.Delay(250, cancel.Token);
                        using var latest = await ReadMaskAsync(window, layout.Bounds, cancel.Token);
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
                var mask = ExplorationLayout.ForWindow(area.Size).Bounds;
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
        private int _endFrames;
        public bool Finished => _endFrames >= 2;
        public string[] Observe(Page page)
        {
            var confirmed = page.Ids.Where(_prior.Contains).ToArray();
            _prior = new(page.Ids);
            _endFrames = page.End ? _endFrames + 1 : 0;
            return confirmed;
        }
    }

    internal sealed record Page(HashSet<string> Ids, string[] Unmatched, bool End);
    internal static Page ReadPage(IReadOnlyList<OcrLine> lines, IReadOnlyList<ExplorationPlace> candidates)
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
