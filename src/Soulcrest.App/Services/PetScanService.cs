using System.Drawing;
using Soulcrest.App.Capture;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr;
using Soulcrest.Ocr.PetWindow;

namespace Soulcrest.App.Services;

public enum ScanConfidence { Unknown, Uncertain, Recognised, Confirmed }

/// <summary>
/// State of the currently visible page of the pet list, for the overlay's scan mode:
/// Ready = every visible card has a value and the picture stood still for two frames, so the user can scroll on.
/// </summary>
/// <summary>A card still to click, in capture-region pixels: value unreadable or pet unknown.</summary>
public sealed record ScanMark(Rectangle Bounds, bool ValueMissing);

public sealed record ScanPageStatus(int Cards, int Read, IReadOnlyList<string> Missing, bool Stable, int TotalPets, (int Owned, int Total)? Collection,
    IReadOnlyList<ScanMark> Marks)
{
    public bool Ready => Cards > 0 && Read == Cards && Stable;
}

/// <summary>One pet seen in the in-game pet window, merged over all frames of a scan.</summary>
public sealed class ScanEntry
{
    public required string Key { get; init; }
    public string? PetId { get; set; }
    public string? SuggestedName { get; set; }
    public ScanConfidence Confidence { get; set; }
    public int MatchScore { get; set; }
    public required string ThumbnailDataUrl { get; set; }
    public required byte[] PortraitPng { get; set; }
    public ulong PortraitHash { get; init; }
    public Dictionary<(int Level, int InLevel), int> Votes { get; } = [];
    public int Seen { get; set; }
    public bool Apply { get; set; } = true;
    public string LastText { get; set; } = "";

    public ScanEntry Snapshot()
    {
        var copy = new ScanEntry { Key = Key, PetId = PetId, SuggestedName = SuggestedName,
            Confidence = Confidence, MatchScore = MatchScore, ThumbnailDataUrl = ThumbnailDataUrl,
            PortraitPng = (byte[])PortraitPng.Clone(), PortraitHash = PortraitHash,
            Seen = Seen, Apply = Apply, LastText = LastText };
        foreach (var vote in Votes) copy.Votes.Add(vote.Key, vote.Value);
        return copy;
    }

    public (int Level, int InLevel)? Reading =>
        Votes.Count == 0 ? null : Votes.OrderByDescending(v => v.Value).ThenByDescending(v => v.Key.Level).First().Key;
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
    private CancellationTokenSource? _cancel;
    private PortraitMatcher? _matcher;
    private PetWindowScanner? _scanner;

    public bool Running { get; private set; }
    public string Status { get; private set; } = "Bereit. Im Spiel das Pet-Fenster öffnen (Tab „ALL“), dann „Scan starten“.";
    public bool StatusIsError { get; private set; }
    public int Frames { get; private set; }
    public (int Owned, int Total)? Collection { get; private set; }
    public string? PanelText { get; private set; }
    public string? PreviewDataUrl { get; private set; }

    /// <summary>Visible page of the last frame (null before the first frame).</summary>
    public ScanPageStatus? Page { get; private set; }

    private readonly GridMotion _gridMotion = new();

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
            _initializing = Task.Run(EnsureScanner, token);
            await _initializing;
            token.ThrowIfCancellationRequested();
            _gridMotion.Reset();
        }
        catch (OperationCanceledException)
        {
            Starting = false;
            return;
        }
        catch (Exception exception)
        {
            Starting = false;
            SetStatus(exception.Message, error: true);
            return;
        }
        Starting = false;
        Running = true;
        SetStatus("Scan läuft – langsam durch die Pet-Liste scrollen. Unbekannte Pets einmal anklicken, dann liest Soulcrest den Namen rechts.");
        _loop = Task.Run(() => LoopAsync(token));
    }

    public void Stop()
    {
        _cancel?.Cancel();
        Running = false;
        Page = null;
        SetStatus($"Scan beendet: {Entries.Count} Pets gelesen. Ergebnisse prüfen und übernehmen.");
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Frames = 0;
        Collection = null;
        Changed?.Invoke();
    }

    /// <summary>Scans a single image file (e.g. a screenshot of the pet window).</summary>
    public async Task ScanFileAsync(string path)
    {
        if (Running || Starting) return;
        Starting = true;
        try
        {
            if (_loop is { } previous)
            {
                try { await previous; } catch (OperationCanceledException) { }
            }
            _initializing = Task.Run(EnsureScanner);
            await _initializing;
            using var bitmap = new Bitmap(path);
            await ProcessAsync(bitmap, CancellationToken.None, singlePicture: true);
            SetStatus($"Bild gelesen: {Entries.Count} Pets.");
        }
        finally { Starting = false; }
    }

    private void EnsureScanner()
    {
        if (_scanner is not null)
            return;
        var language = settings.Current.EffectiveOcrLanguage;
        var matcher = new PortraitMatcher();
        if (progress.MapDataDirectory is { } mapdata)
        {
            foreach (var pet in progress.Catalog.Pets.Where(p => p.Icon is not null))
                matcher.AddReference(pet.Id, Path.Combine(mapdata, pet.Icon!));
        }
        RepairLearnedPortraits(matcher);
        foreach (var (petId, path) in ProgressService.LearnedPortraits())
            matcher.AddReference(petId, path);
        _matcher = matcher;
        _scanner = new PetWindowScanner(WindowsOcrLineReader.Create(language, scale: 4), WindowsOcrLineReader.Create(language), matcher,
            WindowsOcrLineReader.Create(language, scale: 6));
    }

    /// <summary>
    /// Checks learned portraits against the map-data icons (the matcher holds only those at this point):
    /// a portrait of pet X that clearly shows pet Y was bound wrongly and is set aside; a learned pet
    /// whose portrait clearly is a placeholder pet ("Crestlich 01") becomes that pet's real name.
    /// </summary>
    private void RepairLearnedPortraits(PortraitMatcher iconsOnly)
    {
        // The same picture learned for two pets (Kailin / Young Kailin, 2026-10-03): at most one of them is
        // right, and as references they make both pets indistinguishable. Both are set aside and learned
        // again on the next click.
        var duplicates = ProgressService.LearnedPortraits()
            .GroupBy(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Path))))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();
        foreach (var (petId, path) in duplicates)
        {
            Directory.CreateDirectory(AppPaths.RejectedPortraitsDirectory);
            File.Move(path, Path.Combine(AppPaths.RejectedPortraitsDirectory, $"{petId}-doppelt.png"), overwrite: true);
        }
        foreach (var (petId, path) in ProgressService.LearnedPortraits().ToList())
        {
            using var image = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
            if (image.Empty())
                continue;
            var match = iconsOnly.Match(image);
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
            using var grabbed = capture?.Grab(part) ?? BitmapMat.ToBgr(ScreenCapture.Capture(part));
            using var target = new OpenCvSharp.Mat(picture, new OpenCvSharp.Rect(x, 0, grabbed.Width, grabbed.Height));
            grabbed.CopyTo(target);
        }
        return BitmapMat.ToBitmapFast(picture);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadFrameAsync(() => GrabWithoutMiddle(CaptureRegion), token);
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
    internal async Task<bool> ReadFrameAsync(Func<Bitmap> readFrame, CancellationToken token)
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
        await ProcessAsync(bitmap, token);
        if (recovering && !StatusIsError)
            SetStatus("Scan läuft – langsam durch die Pet-Liste scrollen. Unbekannte Pets einmal anklicken, dann liest Soulcrest den Namen rechts.");
        return true;
    }

    // Card values count only from pictures of a list standing still: mid-scroll frames gave Sylphen
    // (12/25) a vote for 11 and Rafflesia (4/25) one for MAX (live log 2026-10-03).
    private bool _voting = true;

    private async Task ProcessAsync(Bitmap bitmap, CancellationToken token, bool singlePicture = false)
    {
        var scan = await _scanner!.ScanAsync(bitmap, token);
        token.ThrowIfCancellationRequested();
        Frames++;
        PreviewDataUrl = ScreenCapture.ToDataUrl(bitmap, 420);
        if (scan.Collection is { } collection)
            Collection = collection;
        PanelText = scan.Panel is { } panel ? $"{panel.Name} · Stufe {panel.Level?.ToString() ?? "?"}" : null;
        if (scan.Problem is not null)
        {
            SetStatus(scan.Problem, error: true);
            return;
        }

        var stable = _gridMotion.IsStill(bitmap, scan.Cards.Select(c => c.Bounds).ToList());
        lock (_gate)
        {
            _voting = stable || singlePicture;
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
            LearnFromPanel(scan, frame);
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

    private ScanPageStatus DescribePage(List<(PetCardScan Card, ScanEntry Entry)> frame, bool stable)
    {
        // Rows of the visible page, top to bottom; columns left/middle/right.
        var rowTops = frame.Select(f => f.Card.Bounds.Y).Distinct().Order().ToList();
        // A card is done when its value is read and the pet is known; otherwise the user should click it
        // (the info panel then gives name and exact progress).
        // A locked pet without souls (no counter on the card, 0/5) matches the default: nothing to take over,
        // so it does not need to be identified.
        var open = frame.Where(f => f.Entry.Reading is null || (f.Entry.PetId is null && f.Entry.Reading is not (0, 0)))
            .OrderBy(f => f.Card.Bounds.Y).ThenBy(f => f.Card.Column)
            .ToList();
        var missing = open
            .Select(f =>
            {
                var row = rowTops.FindIndex(y => Math.Abs(y - f.Card.Bounds.Y) < f.Card.Bounds.Height / 3) + 1;
                var column = f.Card.Column switch { 0 => "links", 1 => "Mitte", _ => "rechts" };
                var reason = f.Entry.Reading is null ? "Wert unlesbar" : "Pet unbekannt";
                return $"Reihe {row} {column} ({reason})";
            })
            .ToList();
        var marks = open.Select(f => new ScanMark(f.Card.Bounds, f.Entry.Reading is null)).ToList();
        return new ScanPageStatus(frame.Count, frame.Count - missing.Count, missing, stable, _entries.Count, Collection, marks);
    }

    /// <summary>
    /// Entry for a card. Two cards visible at the same time are never the same pet (<paramref name="used"/>):
    /// near-identical portraits (Kailin / Young Kailin) shared one unknown entry, and clicking one renamed
    /// it away from the other (user report 2026-10-03).
    /// </summary>
    private ScanEntry Merge(PetCardScan card, HashSet<ScanEntry>? used = null, bool wins = true)
    {
        var match = card.Match;
        string? petId = match.IsPlausible && wins ? match.PetId : null;
        var entry = petId is not null
            ? _entries.FirstOrDefault(e => e.PetId == petId && used?.Contains(e) != true && !Contradicts(e, card))
            : null;
        if (entry is null && petId is not null && _entries.Any(e => e.PetId == petId && (used?.Contains(e) == true || Contradicts(e, card))))
            petId = null; // the pet is already on another card of this frame, or is owned where this card is locked
        entry ??= petId is null
            ? _entries.FirstOrDefault(e => used?.Contains(e) != true && !Contradicts(e, card) && PetWindowScanner.HashDistance(e.PortraitHash, card.PortraitHash) <= 8)
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

    private string UniqueKey(string key)
    {
        var unique = key;
        for (var i = 2; _entries.Any(e => e.Key == unique); i++)
            unique = $"{key}-{i}";
        return unique;
    }

    private ScanEntry MergeInto(ScanEntry entry, PetCardScan card, bool allowPet = true)
    {
        var match = card.Match;
        string? petId = match.IsPlausible && allowPet ? match.PetId : null;
        entry.Seen++;
        if (_voting && card.Progress is { } p)
        {
            var key = (p.Level, p.SoulsInLevel);
            // A selected card is read through the selection glow: count it less than a clean read.
            entry.Votes[key] = entry.Votes.GetValueOrDefault(key) + (card.Selected ? 1 : 2);
            entry.LastText = card.Source == "balken" ? $"Balken {card.BarFill:P0}" : card.ProgressText;
        }
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
            $"→{f.Entry.PetId ?? "unbekannt"}:{(f.Entry.Reading is { } r ? $"{r.Level}/{r.InLevel}" : "-")}"));
        var panel = scan.Panel is { } pn ? $"{pn.Name} {(pn.Progress is { } pp ? (pp.IsMax ? "MAX" : $"{pp.SoulsInLevel}/{pp.Needed}") : "")}" : "-";
        var line = $"{Page?.Read}/{Page?.Cards} still={stable} panel=[{panel}] {cards}";
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

    private void LearnFromPanel(PetWindowScan scan, List<(PetCardScan Card, ScanEntry Entry)> frame)
    {
        if (scan.Panel?.Name is not { Length: > 1 } name || frame.Count(f => f.Card.Selected) != 1)
            return;
        var (card, entry) = frame.Single(f => f.Card.Selected);
        if (!_entries.Contains(entry))
            return;
        var named = progress.FindByName(name);
        // The panel still shows the previously selected pet (it follows the click a frame later): that pet
        // is visible on another card, or this card was already confirmed as another pet.
        if (named is not null && (frame.Any(f => f.Entry != entry && f.Entry.PetId == named.Id)
            || entry.Confidence == ScanConfidence.Confirmed && entry.PetId != named.Id))
            return;
        // Learn the clean (unselected) portrait when there is one; the glowing one matches worse later.
        var portraitPng = entry.PortraitPng;
        var pet = progress.FindByName(name);
        var recognised = entry.Confidence == ScanConfidence.Recognised ? entry.PetId : null;
        if (pet is not null && recognised is not null && recognised != pet.Id)
        {
            // The portrait clearly shows another pet than the panel names: the card and the panel do not
            // belong together (yet). Learn nothing rather than something wrong.
            SetStatus($"Panel „{name}“ passt nicht zum Porträt ({progress.Catalog.Find(recognised)?.En}) – nichts gelernt.");
            return;
        }
        if (pet is null && recognised is not null && progress.IsPlaceholder(recognised))
        {
            // Map-data pet known only by its icon name ("KrallWar 01 V01"): the panel gives its real name.
            progress.NamePlaceholder(recognised, name);
            pet = progress.Catalog.Find(recognised);
        }
        if (pet is null)
        {
            pet = progress.LearnPet(name, portraitPng);
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
            foreach (var vote in entry.Votes)
                existing.Votes[vote.Key] = existing.Votes.GetValueOrDefault(vote.Key) + vote.Value;
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
        // The panel shows the exact progress of the selected pet ("Lv. 1 (5/25)"): strongest vote.
        if (scan.Panel.Progress is { } exact)
        {
            entry.Votes[(exact.Level, exact.SoulsInLevel)] = entry.Votes.GetValueOrDefault((exact.Level, exact.SoulsInLevel)) + 5;
            entry.LastText = $"Panel {(exact.IsMax ? "MAX" : $"{exact.SoulsInLevel}/{exact.Needed}")}";
        }
        else if (scan.Panel.Level is 3)
        {
            entry.Votes[(3, 0)] = entry.Votes.GetValueOrDefault((3, 0)) + 2;
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
