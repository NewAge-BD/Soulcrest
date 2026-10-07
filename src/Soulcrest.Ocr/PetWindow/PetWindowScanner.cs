using OpenCvSharp;
using Soulcrest.Core.PetWindow;

namespace Soulcrest.Ocr.PetWindow;

public sealed record PetCardScan(
    int Column, Rectangle Bounds, PetCardProgress? Progress, string ProgressText,
    PortraitMatch Match, bool Selected, byte[] PortraitPng, ulong PortraitHash,
    bool Locked = false, double BarFill = 0, string Source = "text");

/// <summary>Info panel of the selected pet: name, "Lv. n" and, when shown, the exact progress "(5/25)" / "(MAX)".</summary>
public sealed record PetPanelScan(string? Name, int? Level, string RawText, PetCardProgress? Progress = null);

public sealed record PetWindowScan(IReadOnlyList<PetCardScan> Cards, PetPanelScan? Panel, (int Owned, int Total)? Collection, string? Problem);

/// <summary>
/// Reads the in-game pet window (docs/PET_WINDOW.md). Everything is located in the image itself, so
/// window size and UI scale do not matter:
/// <list type="bullet">
/// <item>columns and rows from brightness profiles (rows from all columns together, dark portraits do not break them),</item>
/// <item>the selected card from its cyan outline,</item>
/// <item>the progress text as a horizontal group of white (or cyan "MAX") glyphs near the card's bottom right,</item>
/// <item>the progress bar directly below the text, the level badge at the bottom left,</item>
/// <item>name and exact progress of the selected pet from the info panel.</item>
/// </list>
/// </summary>
public sealed class PetWindowScanner(WindowsOcrLineReader smallTextReader, WindowsOcrLineReader panelReader, PortraitMatcher matcher,
    WindowsOcrLineReader? largerTextReader = null)
{
    // Grid search band: the left third (large UI scale puts the third column up to ~29 % of the width).
    private const double GridLeft = 0.0, GridRight = 0.34, GridTop = 0.11, GridBottom = 0.93;

    /// <summary>The middle of the pet window (animated pet model) is not needed: callers may black it out.</summary>
    public const double MiddleFrom = 0.36, MiddleTo = 0.69;

    private readonly Dictionary<ulong, PortraitMatch> _matchCache = [];
    private readonly Dictionary<(string Fingerprint, bool Selected), CardReading> _readCache = [];
    private int _matchCacheReferences = -1;

    public async Task<PetWindowScan> ScanAsync(Bitmap window, CancellationToken cancellationToken = default)
    {
        using var bgr = BitmapMat.ToBgr(window);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        var cards = new List<PetCardScan>();
        var columns = FindColumns(gray);
        if (columns.Count == 0)
            return new PetWindowScan(cards, null, null, "Kein Pet-Raster gefunden – ist das Pet-Fenster geöffnet und der ganze Spielbildschirm im Bereich?");

        var grid = FindCards(gray, columns);
        if (grid.Count == 0)
            return new PetWindowScan(cards, null, null, "Raster gefunden, aber keine vollständigen Karten.");

        var glow = grid.Select(g => SelectionGlow(bgr, g.Card)).ToList();
        // The outline brightness varies (locked2 fixture: ring V ~105, ratio 0.12; others 0.29–0.34), so the
        // selected card is the one whose ring clearly stands out from all others.
        var ranked = glow.OrderDescending().ToList();
        var selectedIndex = ranked.Count > 0 && ranked[0] >= 0.06 && (ranked.Count == 1 || ranked[0] >= 2 * ranked[1])
            ? glow.IndexOf(ranked[0]) : -1;

        // Portraits of all cards in parallel, and an unchanged portrait (same hash, same references) is not
        // matched again: a scan took 4.5 s, 3.8 s of it portrait matching (live 2026-10-03, "es dauert
        // recht lang, bis der Scan registriert, dass ich eine Karte ausgewählt habe").
        var portraits = grid.Select(g => Clamp(bgr, new Rect(g.Card.X, g.Card.Y, g.Card.Width, g.Card.Height * 78 / 100))).ToList();
        var hashes = portraits.Select(r => { using var p = new Mat(bgr, r); return AverageHash(p); }).ToList();
        var matches = new PortraitMatch[grid.Count];
        var references = matcher.ReferenceCount;
        lock (_matchCache)
        {
            if (_matchCacheReferences != references)
            {
                _matchCache.Clear();
                _matchCacheReferences = references;
            }
            for (var i = 0; i < grid.Count; i++)
                matches[i] = _matchCache.GetValueOrDefault(hashes[i])!;
        }
        Parallel.For(0, grid.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            if (matches[i] is not null)
                return;
            using var portrait = new Mat(bgr, portraits[i]);
            matches[i] = matcher.Match(portrait);
        });
        lock (_matchCache)
        {
            if (_matchCache.Count > 2000)
                _matchCache.Clear();
            for (var i = 0; i < grid.Count; i++)
                _matchCache[hashes[i]] = matches[i];
        }

        // Text bands of all cards first: where the text sits relative to its card is the same for every
        // card of one capture, which locates the text on cards whose bright art hides the glyph outline.
        var bands = grid.Select(g => FindTextBand(bgr, g.Card)).ToList();
        var usualText = UsualTextPosition(grid.Select(g => g.Card).ToList(), bands);
        for (var i = 0; i < grid.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (column, card) = grid[i];
            var selected = i == selectedIndex;

            // An unchanged card (same pixels around text, bar and badge) is not read again: OCR of 15 cards
            // took most of a scan once the portraits were cached.
            var key = (CardFingerprint(bgr, card), selected);
            CardReading reading;
            lock (_readCache)
                _readCache.TryGetValue(key, out reading!);
            if (reading is null)
            {
                reading = await ReadCardAsync(bgr, card, bands[i], usualText, cancellationToken);
                lock (_readCache)
                {
                    if (_readCache.Count > 2000)
                        _readCache.Clear();
                    _readCache[key] = reading;
                }
            }
            else
            {
                bands[i]?.Dispose();
            }
            var (progressText, progress, locked, barFill, source) = reading;

            using var portrait = new Mat(bgr, portraits[i]);
            Cv2.ImEncode(".png", portrait, out var png);
            cards.Add(new PetCardScan(column, new Rectangle(card.X, card.Y, card.Width, card.Height), progress, progressText, matches[i], selected, png,
                hashes[i], locked, barFill, source));
        }

        var panel = await ReadPanelAsync(bgr, cancellationToken);
        var collectionRect = Clamp(bgr, new Rect((int)(bgr.Width * 0.08), (int)(bgr.Height * 0.92), (int)(bgr.Width * 0.24), (int)(bgr.Height * 0.06)));
        var collection = PetWindowText.ParseCollection(await ReadTextAsync(bgr, collectionRect, panelReader, cancellationToken));
        return new PetWindowScan(cards, panel, collection, null);
    }

    // ------------------------------------------------------------------ grid

    private static List<(int Left, int Right)> FindColumns(Mat gray)
    {
        int x0 = (int)(gray.Width * GridLeft), x1 = (int)(gray.Width * GridRight);
        int y0 = (int)(gray.Height * GridTop), y1 = (int)(gray.Height * GridBottom);
        using var area = new Mat(gray, new Rect(x0, y0, x1 - x0, y1 - y0));
        using var profile = new Mat();
        Cv2.Reduce(area, profile, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
        var segments = Segments(profile, horizontal: true, threshold: 12, x0);
        int minWidth = (int)(gray.Width * 0.035), maxWidth = (int)(gray.Width * 0.09);
        var candidates = segments.Where(s => s.End - s.Start >= minWidth && s.End - s.Start <= maxWidth).ToList();
        if (candidates.Count < 3)
            return [];
        // Three adjacent columns of similar width (the grid), leftmost such triple: the search band reaches
        // into the text beside the grid for large UI scales (medium scale picked a triple in that text).
        for (var i = 0; i <= candidates.Count - 3; i++)
        {
            var triple = candidates.Skip(i).Take(3).ToList();
            var widths = triple.Select(t => t.End - t.Start).ToList();
            var gaps = new[] { triple[1].Start - triple[0].End, triple[2].Start - triple[1].End };
            if (widths.Max() - widths.Min() <= widths.Min() / 5 && gaps.All(g => g >= 0 && g < widths.Min() / 3))
                return triple.Select(t => (t.Start, t.End)).ToList();
        }
        return [];
    }

    /// <summary>
    /// Rows from the averaged profile of all columns (cards of a row are aligned; one dark portrait
    /// cannot split a row). Every card gets the narrowest column width, centred in its column, so the
    /// selection glow does not widen it.
    /// </summary>
    private static List<(int Column, Rect Card)> FindCards(Mat gray, List<(int Left, int Right)> columns)
    {
        var width = columns.Min(c => c.Right - c.Left);
        int y0 = (int)(gray.Height * GridTop), y1 = (int)(gray.Height * GridBottom);
        using var sum = new Mat(y1 - y0, 1, MatType.CV_32F, Scalar.All(0));
        foreach (var (left, right) in columns)
        {
            var inset = (right - left) / 8;
            using var area = new Mat(gray, new Rect(left + inset, y0, right - left - 2 * inset, y1 - y0));
            using var profile = new Mat();
            Cv2.Reduce(area, profile, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
            Cv2.Add(sum, profile, sum);
        }
        using var average = (sum / columns.Count).ToMat();
        // Locked pets without a counter are dark at the bottom (nocounter fixture: card edges at 8–20), so a
        // threshold of 20 cut them short or dropped a row. The gaps between rows are ~0, but where a glow or
        // bright art bridges a gap, the low threshold merges two rows; then the strict segments are used.
        bool IsCard((int Start, int End) s) => s.End - s.Start >= width * 1.15 && s.End - s.Start <= width * 1.6;
        var strict = Segments(average, horizontal: false, threshold: 20, y0);
        var rows = new List<(int Start, int End)>();
        foreach (var loose in Segments(average, horizontal: false, threshold: 5, y0))
        {
            if (IsCard(loose))
                rows.Add(loose);
            else
                rows.AddRange(strict.Where(s => s.Start >= loose.Start && s.End <= loose.End && IsCard(s)));
        }
        if (rows.Count == 0)
            return [];
        // A row cut off by the list viewport (top or bottom, large UI scale) is lower than the full rows:
        // its badge and text are missing, it read as "locked 0/5".
        // Compared with the median row (the selected row's glow makes it taller than the others).
        var fullHeight = rows.Select(r => r.End - r.Start).Order().ElementAt(rows.Count / 2);
        rows = rows.Where(r => r.End - r.Start >= fullHeight * 0.9).ToList();
        // Typical card height (the glow of a selected card can stretch its row a little).
        var height = rows.Select(r => r.End - r.Start).Order().ElementAt(rows.Count / 2);
        var result = new List<(int, Rect)>();
        foreach (var (start, end) in rows)
        {
            var top = (start + end) / 2 - height / 2;
            for (var c = 0; c < columns.Count; c++)
            {
                var centre = (columns[c].Left + columns[c].Right) / 2;
                result.Add((c, new Rect(centre - width / 2, top, width, height)));
            }
        }
        return result;
    }

    private static List<(int Start, int End)> Segments(Mat profile, bool horizontal, double threshold, int offset)
    {
        var length = horizontal ? profile.Cols : profile.Rows;
        var result = new List<(int, int)>();
        int? start = null;
        for (var i = 0; i < length; i++)
        {
            var value = horizontal ? profile.At<float>(0, i) : profile.At<float>(i, 0);
            if (value > threshold && start is null)
                start = i;
            else if (value <= threshold && start is not null)
            {
                result.Add((start.Value + offset, i + offset));
                start = null;
            }
        }
        if (start is not null)
            result.Add((start.Value + offset, length + offset));
        return result;
    }

    /// <summary>Share of bright cyan pixels in a thin ring around the card (the selection outline).</summary>
    private static double SelectionGlow(Mat bgr, Rect card)
    {
        var pad = Math.Max(3, card.Width / 25);
        var outer = Clamp(bgr, new Rect(card.X - pad, card.Y - pad, card.Width + 2 * pad, card.Height + 2 * pad));
        using var area = new Mat(bgr, outer);
        using var hsv = new Mat();
        using var cyan = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(80, 60, 90), new Scalar(105, 255, 255), cyan);
        // Blank the card interior (2 * pad inside the outline), keep only the ring.
        var innerX = Math.Max(0, card.X + pad - outer.X);
        var innerY = Math.Max(0, card.Y + pad - outer.Y);
        var interior = new Rect(innerX, innerY, Math.Max(1, card.Width - 2 * pad), Math.Max(1, card.Height - 2 * pad));
        cyan[interior].SetTo(Scalar.All(0));
        var ring = outer.Width * outer.Height - interior.Width * interior.Height;
        return ring <= 0 ? 0 : (double)Cv2.CountNonZero(cyan) / ring;
    }

    // ------------------------------------------------------------------ card text, badge, bar

    /// <summary>Text location plus a clean image of only its glyph pixels (black on white, band coordinates).</summary>
    private sealed record TextBand(Rect Rect, bool Cyan, Mat? Glyphs, bool Fallback = false) : IDisposable
    {
        public void Dispose() => Glyphs?.Dispose();
    }

    /// <summary>
    /// The progress text is a horizontal group of white (or cyan "MAX") glyphs of equal height near
    /// the card's bottom right. Found via connected components, so its position needs no layout constants.
    /// </summary>
    private static TextBand? FindTextBand(Mat bgr, Rect card)
    {
        var search = Clamp(bgr, new Rect(card.X + card.Width / 5, card.Y + card.Height * 68 / 100, card.Width * 4 / 5, card.Height * 34 / 100));
        using var area = new Mat(bgr, search);
        using var hsv = new Mat();
        using var white = new Mat();
        using var cyan = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, 0, 170), new Scalar(180, 80, 255), white);
        Cv2.InRange(hsv, new Scalar(75, 90, 120), new Scalar(105, 255, 255), cyan);
        Cv2.BitwiseOr(white, cyan, mask);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
        double minH = card.Height * 0.035, maxH = card.Height * 0.12, maxW = card.Width * 0.16;
        var glyphs = new List<Rect>();
        for (var i = 1; i < count; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), a = stats.At<int>(i, 4);
            if (h >= minH && h <= maxH && w <= maxW && a >= 4)
                glyphs.Add(new Rect(x, y, w, h));
        }
        if (glyphs.Count < 2)
            return null;

        // Group glyphs on one text line: similar vertical centre and height, neighbours close together.
        var tolerance = card.Height * 0.03;
        TextBand? best = null;
        List<Rect>? bestRun = null;
        var bestScore = 0.0;
        foreach (var seed in glyphs)
        {
            var centre = seed.Y + seed.Height / 2.0;
            var line = glyphs.Where(g => Math.Abs(g.Y + g.Height / 2.0 - centre) <= tolerance && Math.Abs(g.Height - seed.Height) <= seed.Height * 0.45)
                .OrderBy(g => g.X).ToList();
            // Keep the run that ends furthest right (the text is right-aligned) without large gaps.
            var run = new List<Rect> { line[^1] };
            for (var j = line.Count - 2; j >= 0; j--)
            {
                if (run[0].X - (line[j].X + line[j].Width) > seed.Height * 1.2)
                    break;
                run.Insert(0, line[j]);
            }
            if (run.Count < 2)
                continue;
            var right = run.Max(g => g.X + g.Width);
            if (search.X + right < card.X + card.Width * 0.72)
                continue; // not right-aligned: portrait detail
            var score = run.Count + (search.Y + run.Max(g => g.Y + g.Height) - card.Y) / (double)card.Height; // prefer lower lines
            if (score <= bestScore)
                continue;
            var left = run.Min(g => g.X);
            var top = run.Min(g => g.Y);
            var bottom = run.Max(g => g.Y + g.Height);
            var cyanPixels = 0;
            var totalPixels = 0;
            foreach (var g in run)
            {
                using var glyphCyan = new Mat(cyan, g);
                using var glyphMask = new Mat(mask, g);
                cyanPixels += Cv2.CountNonZero(glyphCyan);
                totalPixels += Cv2.CountNonZero(glyphMask);
            }
            var pad = Math.Max(2, (bottom - top) / 4);
            best = new TextBand(Clamp(bgr, new Rect(search.X + left - pad, search.Y + top - pad, right - left + 2 * pad, bottom - top + 2 * pad)),
                totalPixels > 0 && cyanPixels * 2 > totalPixels, null);
            bestRun = run;
            bestScore = score;
        }
        if (best is null || bestRun is null)
            return best;

        // Clean glyph image: only the pixels of the chosen components, so portrait detail next to the
        // text (white fur, light skin) cannot confuse the OCR.
        var glyphImage = new Mat(best.Rect.Height, best.Rect.Width, MatType.CV_8UC1, Scalar.All(255));
        var keep = new HashSet<int>();
        for (var i = 1; i < count; i++)
        {
            var r = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            if (bestRun.Contains(r))
                keep.Add(i);
        }
        int offsetX = best.Rect.X - search.X, offsetY = best.Rect.Y - search.Y;
        for (var y = 0; y < best.Rect.Height; y++)
        {
            var sy = y + offsetY;
            if (sy < 0 || sy >= labels.Rows)
                continue;
            for (var x = 0; x < best.Rect.Width; x++)
            {
                var sx = x + offsetX;
                if (sx >= 0 && sx < labels.Cols && keep.Contains(labels.At<int>(sy, sx)))
                    glyphImage.At<byte>(y, x) = 0;
            }
        }
        return best with { Glyphs = glyphImage };
    }

    private sealed record CardReading(string Text, PetCardProgress? Progress, bool Locked, double BarFill, string Source);

    /// <summary>Progress of one card: text (several OCR views, checked against the bar), MAX, bar-only values.</summary>
    private async Task<CardReading> ReadCardAsync(Mat bgr, Rect card, TextBand? foundBand, Rect2d? usualText, CancellationToken cancellationToken)
    {
        using var band = Widened(foundBand, card, usualText) ?? FallbackBand(bgr, card, usualText);
        var barFill = BarFill(bgr, card, band?.Rect);
        var locked = !HasLevelBadge(bgr, card);
        var source = "text";
        string progressText;
        PetCardProgress? progress;
        // "MAX" is the only cyan text and MAX cards have no progress bar. Cyan portrait art next to an
        // empty bar (0/25, locked2 fixture) is not MAX: the badge then shows "1".
        // The selected card's cyan glow can look like "MAX" (live: Faded Floater 3/75); its reading counts
        // little there and the info panel's exact value outweighs it, so MAX stays possible (MAX fixture).
        if (!locked && barFill < 0.03 && BadgeShowsOne(bgr, card) is not true
            && (band is { Cyan: true } || HasCyanText(bgr, card)))
        {
            progressText = "MAX";
            progress = new PetCardProgress(3, 0, 0, true);
        }
        else
        {
            (progressText, progress) = band is { } found ? await ReadTextBandAsync(bgr, found, barFill, cancellationToken) : ("", null);
        }

        // Owned card without readable text: a "1" in the level badge means x/25, and the bar is exact
        // enough for 25ths (live capture: within ±0.01 = half a soul). x/75 stays unread (click it).
        // Only when the bar lies clearly on one value and the OCR text shows that number somewhere
        // (live 2026-10-03: 0.86 = 21.5 became 22 for 21/25).
        if (!locked && progress is null && BadgeShowsOne(bgr, card) is true
            && PetWindowText.SoulsFromBar(barFill, 25) is { } barSouls25
            && Math.Abs(barFill * 25 - barSouls25) <= 0.3
            && PetWindowText.TextShowsSouls(progressText, barSouls25, 25))
        {
            progress = new PetCardProgress(1, barSouls25, 25, false);
            source = "balken";
        }

        // Locked pets have no level badge and always need 5 souls; their bar is exact (fifths).
        if (locked && progress is not { Needed: 5 })
        {
            progress = PetWindowText.SoulsFromBar(barFill, 5) is { } souls ? new PetCardProgress(0, souls, 5, false) : null;
            source = "balken";
        }
        else if (locked && progress is { Needed: 5 } parsed && PetWindowText.SoulsFromBar(barFill, 5) is { } barSouls
            && barSouls != parsed.SoulsInLevel && barFill > 0.05)
        {
            progress = parsed with { SoulsInLevel = barSouls };
            source = "balken";
        }
        return new CardReading(progressText, progress, locked, barFill, source);
    }

    /// <summary>
    /// The lower part of a card (text, bar, level badge) as a coarse fingerprint: 32×8 grey levels in 16
    /// steps. Equal fingerprints mean the same picture there, so the same reading.
    /// </summary>
    private static string CardFingerprint(Mat bgr, Rect card)
    {
        var region = Clamp(bgr, new Rect(card.X - card.Width * 6 / 100, card.Y + card.Height * 72 / 100, card.Width * 112 / 100, card.Height * 32 / 100));
        using var part = new Mat(bgr, region);
        using var gray = new Mat();
        using var small = new Mat();
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(gray, small, new OpenCvSharp.Size(32, 8), 0, 0, InterpolationFlags.Area);
        var bytes = new byte[32 * 8];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 32; x++)
                bytes[y * 32 + x] = (byte)(small.At<byte>(y, x) >> 4);
        }
        return $"{region.Width}x{region.Height}:{Convert.ToHexString(bytes)}";
    }

    /// <summary>Median text rectangle relative to its card (fractions), from the cards whose text was found.</summary>
    private static Rect2d? UsualTextPosition(List<Rect> cards, List<TextBand?> bands)
    {
        var relative = cards.Zip(bands).Where(p => p.Second is { Cyan: false })
            .Select(p => new Rect2d((p.Second!.Rect.X - p.First.X) / (double)p.First.Width, (p.Second.Rect.Y - p.First.Y) / (double)p.First.Height,
                p.Second.Rect.Width / (double)p.First.Width, p.Second.Rect.Height / (double)p.First.Height))
            .ToList();
        if (relative.Count < 2)
            return null;
        static double Median(IEnumerable<double> values) => values.Order().ElementAt(values.Count() / 2);
        // Right edge and height are stable (right-aligned text); widths differ ("2/75" vs "42/75"): take the widest.
        var right = Median(relative.Select(r => r.X + r.Width));
        var width = relative.Max(r => r.Width);
        return new Rect2d(right - width, Median(relative.Select(r => r.Y)), width, Median(relative.Select(r => r.Height)));
    }

    /// <summary>
    /// A found band clearly narrower than the usual text (a glyph not grouped, live: "1/7" for 1/75) gets
    /// the usual width; its glyph image is dropped, the full-band views read the whole text.
    /// </summary>
    private static TextBand? Widened(TextBand? band, Rect card, Rect2d? usual)
    {
        if (band is null || band.Cyan || usual is not { } r)
            return band;
        var usualLeft = card.X + (int)(r.X * card.Width);
        var usualRight = card.X + (int)((r.X + r.Width) * card.Width);
        var glyph = band.Rect.Height * 0.5;
        if (band.Rect.Right >= usualRight - glyph)
            return band;
        var left = Math.Min(band.Rect.X, usualLeft);
        var widened = new Rect(left, band.Rect.Y, usualRight - left, band.Rect.Height);
        band.Dispose();
        return new TextBand(widened, false, null, Fallback: true);
    }

    /// <summary>Text band at the usual position, without a glyph image (only the full-band OCR views).</summary>
    private static TextBand? FallbackBand(Mat bgr, Rect card, Rect2d? usual)
    {
        if (usual is not { } r)
            return null;
        var rect = Clamp(bgr, new Rect(card.X + (int)(r.X * card.Width), card.Y + (int)(r.Y * card.Height),
            (int)Math.Ceiling(r.Width * card.Width), (int)Math.Ceiling(r.Height * card.Height)));
        return rect.Width > 4 && rect.Height > 4 ? new TextBand(rect, false, null, Fallback: true) : null;
    }

    /// <summary>Dark glyph outline as black on white: the white digits have a dark rim that stays visible on bright art.</summary>
    private static Mat OutlineMask(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var mask = new Mat();
        Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        return mask;
    }

    /// <summary>
    /// Reads the progress text with several OCR passes and accepts only a value that agrees with the
    /// progress bar: a wrong value is worse than an unreadable one. Fixtures: every correct reading
    /// matched its bar within 0.04, every wrong one (26/75 for 20/75, 0/75 for 17/75, 6/25 for 0/25) did not.
    /// </summary>
    private async Task<(string Text, PetCardProgress? Progress)> ReadTextBandAsync(Mat bgr, TextBand found, double barFill, CancellationToken cancellationToken)
    {
        var rect = found.Rect;
        var attempts = new List<string>();
        var candidates = new List<(string Text, PetCardProgress Progress)>();
        // 4 = digit colour mask (first: clean also on bright art), 0 = clean glyph image (only the text
        // components), 1 = brightest channel, 2 = white mask, 3 = outline; each at 4× and 6×. Windows OCR
        // is flaky on 3-character tokens; several views help.
        var passes = new List<(int Variant, WindowsOcrLineReader Reader)>();
        foreach (var variant in found.Fallback ? new[] { 4, 1, 2, 3 } : new[] { 4, 0, 1, 2 })
        {
            if (variant == 0 && found.Glyphs is null || variant == 4 && found.Cyan)
                continue;
            passes.Add((variant, smallTextReader));
            if (largerTextReader is not null)
                passes.Add((variant, largerTextReader));
        }
        using var band = new Mat(bgr, rect);
        using var digits = passes.Any(p => p.Variant == 4) ? DigitColourMask(bgr, rect) : null;
        foreach (var (variant, reader) in passes)
        {
            if (variant == 4 && digits is null)
                continue;
            using var prepared = variant switch
            {
                4 => digits!.Clone(),
                0 => found.Glyphs!.Clone(),
                1 => MaxChannelInverted(band),
                2 => WhiteMask(band),
                _ => OutlineMask(band),
            };
            using var padded = new Mat();
            var border = Math.Max(8, rect.Height / 2);
            Cv2.CopyMakeBorder(prepared, padded, border, border, border * 2, border * 2, BorderTypes.Constant, Scalar.White);
            using var color = new Mat();
            Cv2.CvtColor(padded, color, ColorConversionCodes.GRAY2BGR);
            using var bitmap = BitmapMat.ToBitmap(color);
            var lines = await reader.ReadAsync(bitmap, cancellationToken);
            var text = string.Join(" ", lines.Select(l => l.Text)).Trim();
            if (PetWindowText.ParseCardLenient(text) is { } parsed)
            {
                // The bar confirms a value only where it can be seen: small x/75 values lie under the level
                // badge (bar 0), and then any small reading "agreed" (live 2026-10-03: "1/73" taken as 1/75
                // for 2/75). Such readings need agreeing passes instead.
                // Garbled readings ("1/73", "6/2Ä") are completed from the bar only where the bar is
                // precise enough: /25 and /5, or x/75 from 15 % on. A visible stub of 0.05 cannot tell 1 from
                // 2 souls of 75 (live: "1/73" taken as 1/75 for 2/75).
                var exact = PetWindowText.ParseCard(text) is not null;
                var preciseBar = parsed.Needed != 75 || barFill >= 0.15;
                if (AgreesWithBar(parsed, barFill) && BarCanTell(parsed, barFill) && !found.Fallback && (exact || preciseBar))
                    return (text, parsed);
                candidates.Add((text, parsed));
            }
            if (text.Length > 0)
                attempts.Add(text);
        }
        // Several independent passes reading exactly the same value outweigh a bar that light fur
        // made unreadable (live capture: 3× "5/25", bar measured 0.43). Lenient guesses do not count.
        var strict = candidates.Where(c => PetWindowText.ParseCard(c.Text) is not null).ToList();
        var consensus = strict.GroupBy(c => c.Progress).OrderByDescending(g => g.Count()).FirstOrDefault();
        // Agreeing passes outweigh a disturbed bar, but not when they only lack the leading digit of the
        // bar's value: six times "1/25" for 11/25, bar 0.45 (live 2026-10-03).
        if (consensus is not null && DropsLeadingDigit(consensus.Key, barFill))
            consensus = null;
        if (consensus is not null && consensus.Count() >= 3)
            return ($"{consensus.First().Text} ({consensus.Count()}× gleich)", consensus.Key);
        // Without a visible bar: two exact, agreeing readings and no other value, and still not against
        // the bar (a hidden bar only allows small values). A visible /25 bar is precise: then the readings
        // must lie within one soul of it (live 2026-10-03: twice "14/25" for Kerubar 16/25, bar 0.65).
        if (consensus is not null && consensus.Count() >= 2 && strict.All(c => c.Progress == consensus.Key) && AgreesWithBar(consensus.Key, barFill)
            && (consensus.Key.Needed != 25 || barFill < 0.03 || Math.Abs(barFill * 25 - consensus.Key.SoulsInLevel) <= 1.0))
            return ($"{consensus.First().Text} ({consensus.Count()}× gleich, ohne Balken)", consensus.Key);
        var rejected = candidates.Count > 0 ? $" (verworfen, Balken {barFill:0.00}: {string.Join(", ", candidates.Select(c => c.Text))})" : "";
        return (string.Join(" | ", attempts) + rejected, null);
    }

    /// <summary>The reading equals the bar's value without its leading digit ("1" for 11, "4" for 14).</summary>
    internal static bool DropsLeadingDigit(PetCardProgress reading, double barFill)
    {
        if (reading.IsMax || reading.Needed == 0)
            return false;
        var fromBar = (int)Math.Round(barFill * reading.Needed);
        var barText = fromBar.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return fromBar >= 10 && fromBar != reading.SoulsInLevel
            && barText.EndsWith(reading.SoulsInLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the bar can confirm this value: a visible bar always, an empty one only for the values it
    /// excludes. MAX cards have no bar by design.
    /// </summary>
    internal static bool BarCanTell(PetCardProgress progress, double barFill) => progress.IsMax || barFill >= 0.03 || progress.Needed == 5;

    /// <summary>Souls/needed must match the bar fill; tolerance about 3–5 souls depending on the level.</summary>
    internal static bool AgreesWithBar(PetCardProgress progress, double barFill)
    {
        if (progress.IsMax)
            return barFill < 0.03;
        var fraction = (double)progress.SoulsInLevel / progress.Needed;
        var tolerance = progress.Needed switch { 75 => 0.07, 25 => 0.09, _ => 0.12 };
        return Math.Abs(fraction - barFill) <= tolerance;
    }

    /// <summary>Fallback MAX check: cyan pixels in the card's bottom right (only cyan text there is "MAX").</summary>
    private static bool HasCyanText(Mat bgr, Rect card)
    {
        var region = Clamp(bgr, new Rect(card.X + card.Width * 35 / 100, card.Y + card.Height * 86 / 100, card.Width * 65 / 100, card.Height * 14 / 100));
        using var area = new Mat(bgr, region);
        using var hsv = new Mat();
        using var cyan = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(75, 90, 120), new Scalar(105, 255, 255), cyan);
        return Cv2.CountNonZero(cyan) >= region.Width * region.Height * 0.05;
    }

    /// <summary>
    /// The digits in their own colour: near-white glyphs with a dark rim (user idea 2026-10-03). The exact
    /// digit colour is taken from the band itself (brightest, least saturated pixels); pixels close to it in
    /// Lab form candidate glyphs, and only those enclosed by the rim count (not touching the band's edge,
    /// as tall as the text). Bright fur, petals or slime around the text stay out: "4/25" on a pink flower
    /// and "2/25" on light-blue slime, unreadable in the other views, come out as clean black on white.
    /// Returns black text on white at the band's size (with a margin), or null when nothing glyph-like is left.
    /// </summary>
    internal static Mat? DigitColourMask(Mat bgr, Rect rect)
    {
        const int scale = 4;
        // Margin, and to the left more: a leading digit merged with bright art is often not part of the
        // found band ("1/25" read for 21/25 on the water spirit's crystals).
        var margin = Math.Max(2, rect.Height / 4);
        var left = rect.Height * 6 / 5;
        var area = Clamp(bgr, new Rect(rect.X - left, rect.Y - margin, rect.Width + left + margin, rect.Height + 2 * margin));
        using var part = new Mat(bgr, area);
        using var big = new Mat();
        Cv2.Resize(part, big, new OpenCvSharp.Size(area.Width * scale, area.Height * scale), 0, 0, InterpolationFlags.Cubic);
        using var lab = new Mat();
        using var hsv = new Mat();
        using var gray = new Mat();
        Cv2.CvtColor(big, lab, ColorConversionCodes.BGR2Lab);
        Cv2.CvtColor(big, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(big, gray, ColorConversionCodes.BGR2GRAY);

        // Digit colour: median Lab of the top 7 % by brightness minus saturation.
        int width = big.Width, height = big.Height, count = width * height;
        var score = new int[count];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                score[y * width + x] = gray.At<byte>(y, x) - hsv.At<Vec3b>(y, x).Item1;
        }
        var threshold = score.Order().ElementAt((int)(count * 0.93));
        var l = new List<byte>(); var a = new List<byte>(); var b = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (score[y * width + x] < threshold)
                    continue;
                var v = lab.At<Vec3b>(y, x);
                l.Add(v.Item0); a.Add(v.Item1); b.Add(v.Item2);
            }
        }
        if (l.Count == 0)
            return null;
        static double Median(List<byte> values) => values.Order().ElementAt(values.Count / 2);
        double refL = Median(l), refA = Median(a), refB = Median(b);
        if (refL < 170)
            return null; // no bright text in this band

        using var near = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = lab.At<Vec3b>(y, x);
                double dl = v.Item0 - refL, da = v.Item1 - refA, db = v.Item2 - refB;
                if (dl * dl + da * da + db * db < 22 * 22)
                    near.Set(y, x, (byte)255);
            }
        }
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var n = Cv2.ConnectedComponentsWithStats(near, labels, stats, centroids, PixelConnectivity.Connectivity4);
        var enclosed = new List<int>();
        for (var i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), size = stats.At<int>(i, 4);
            if (x == 0 || y == 0 || x + w >= width || y + h >= height || size < 30)
                continue;
            enclosed.Add(i);
        }
        if (enclosed.Count == 0)
            return null;
        // Glyphs share the text height; flat bright patches inside the rim area (fur streaks) do not.
        var tallest = enclosed.MaxBy(i => stats.At<int>(i, 3));
        int lineTop = stats.At<int>(tallest, 1), lineHeight = stats.At<int>(tallest, 3);
        if (lineHeight < height * 0.25)
            return null;
        // ... and the text line: bright specks in the art left of the text lie above or below it.
        var keep = enclosed.Where(i =>
        {
            int top = stats.At<int>(i, 1), h = stats.At<int>(i, 3);
            var overlap = Math.Min(top + h, lineTop + lineHeight) - Math.Max(top, lineTop);
            return h >= lineHeight * 0.55 && overlap >= h * 0.7;
        }).ToHashSet();
        using var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(255));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (keep.Contains(labels.At<int>(y, x)))
                    mask.Set(y, x, (byte)0);
            }
        }
        var result = new Mat();
        Cv2.Resize(mask, result, new OpenCvSharp.Size(area.Width, area.Height), 0, 0, InterpolationFlags.Area);
        return result;
    }

    /// <summary>Dark text on white: brightest channel, inverted and contrast-stretched.</summary>
    private static Mat MaxChannelInverted(Mat bgr)
    {
        var channels = Cv2.Split(bgr);
        try
        {
            var max = new Mat();
            Cv2.Max(channels[0], channels[1], max);
            Cv2.Max(max, channels[2], max);
            Cv2.Normalize(max, max, 0, 255, NormTypes.MinMax);
            Cv2.BitwiseNot(max, max);
            return max;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    /// <summary>White (low saturation, bright) and cyan pixels as black text on white.</summary>
    private static Mat WhiteMask(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        using var cyan = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 80, 255), mask);
        Cv2.InRange(hsv, new Scalar(75, 90, 120), new Scalar(105, 255, 255), cyan);
        Cv2.BitwiseOr(mask, cyan, mask);
        Cv2.BitwiseNot(mask, mask);
        return mask;
    }

    /// <summary>
    /// Owned pets carry a navy level badge at the bottom left. Its hue (106–125) differs from the
    /// light-blue selection glow (~100). Fixtures: owned 299–486 badge pixels, locked 0 (also when selected).
    /// </summary>
    private static bool HasLevelBadge(Mat bgr, Rect card)
    {
        var region = Clamp(bgr, new Rect(card.X - card.Width * 6 / 100, card.Y + card.Height * 84 / 100, card.Width * 28 / 100, card.Height * 20 / 100));
        using var area = new Mat(bgr, region);
        using var hsv = new Mat();
        using var badge = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(106, 100, 60), new Scalar(125, 255, 200), badge);
        return Cv2.CountNonZero(badge) >= region.Width * region.Height * 0.10;
    }

    /// <summary>
    /// Reads the digit in the level badge just enough to tell "1" (narrow glyph) from "2"/"3" (wide).
    /// Null when no badge or no digit is found.
    /// </summary>
    private static bool? BadgeShowsOne(Mat bgr, Rect card)
    {
        var region = Clamp(bgr, new Rect(card.X - card.Width * 8 / 100, card.Y + card.Height * 80 / 100, card.Width * 32 / 100, card.Height * 26 / 100));
        using var area = new Mat(bgr, region);
        using var hsv = new Mat();
        using var navy = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(106, 100, 60), new Scalar(125, 255, 200), navy);
        if (Cv2.CountNonZero(navy) < 20)
            return null;
        var circle = LargestComponent(navy);
        // The digit: white glyph inside the navy circle (the circle's light rim is excluded by size).
        using var inside = new Mat(hsv, circle);
        // White digit, or cyan on the selected card (glow).
        using var white = new Mat();
        using var cyan = new Mat();
        Cv2.InRange(inside, new Scalar(0, 0, 170), new Scalar(180, 90, 255), white);
        Cv2.InRange(inside, new Scalar(80, 60, 170), new Scalar(105, 255, 255), cyan);
        Cv2.BitwiseOr(white, cyan, white);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(white, labels, stats, centroids);
        Rect? digit = null;
        for (var i = 1; i < count; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3);
            if (h < circle.Height * 0.3 || h > circle.Height * 0.8 || w > circle.Width * 0.6)
                continue;
            // The digit sits in the middle; the selection outline of the card can reach into the corner.
            double cx = centroids.At<double>(i, 0), cy = centroids.At<double>(i, 1);
            if (cx < circle.Width * 0.2 || cx > circle.Width * 0.8 || cy < circle.Height * 0.2 || cy > circle.Height * 0.8)
                continue;
            if (digit is null || h > digit.Value.Height)
                digit = new Rect(x, y, w, h);
        }
        return digit is { } d ? d.Width < d.Height * 0.45 : null;
    }

    /// <summary>Bounding box of the largest blob (small gaps closed), e.g. the badge circle without the glow around it.</summary>
    private static Rect LargestComponent(Mat mask)
    {
        using var closed = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.MorphologyEx(mask, closed, MorphTypes.Close, kernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(closed, labels, stats, centroids);
        var best = 0;
        for (var i = 1; i < count; i++)
        {
            if (best == 0 || stats.At<int>(i, 4) > stats.At<int>(best, 4))
                best = i;
        }
        return best == 0 ? new Rect(0, 0, mask.Width, mask.Height)
            : new Rect(stats.At<int>(best, 0), stats.At<int>(best, 1), stats.At<int>(best, 2), stats.At<int>(best, 3));
    }

    // Progress bar track as fractions of the card width (fitted to 4/5, 3/5, 2/5, 42/75, 20/75, 17/75).
    private const double BarTrackLeft = 0.08, BarTrackRight = 0.905;

    /// <summary>
    /// Fill fraction of the beige progress bar. It sits directly below the text (or at the card bottom
    /// when no text was found); searching only there keeps light portraits out.
    /// </summary>
    private static double BarFill(Mat bgr, Rect card, Rect? text)
    {
        _ = text;
        var region = Clamp(bgr, new Rect(card.X, card.Y + card.Height * 84 / 100, card.Width, card.Height * 19 / 100));
        using var area = new Mat(bgr, region);
        using var hsv = new Mat();
        using var beige = new Mat();
        Cv2.CvtColor(area, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(10, 40, 150), new Scalar(35, 200, 255), beige);

        // Per row: right end of the contiguous beige run that starts near the track's left end (the
        // level badge may hide the first part, so the run may start up to 30 % in). -1 = none.
        int rows = beige.Rows, width = beige.Cols, startLimit = (int)(card.Width * 0.30);
        var rightEnds = new int[rows];
        for (var y = 0; y < rows; y++)
        {
            rightEnds[y] = -1;
            var start = -1;
            for (var x = 0; x < width; x++)
            {
                if (beige.At<byte>(y, x) == 0)
                {
                    if (start >= 0 && x - rightEnds[y] > 2)
                        break;
                    continue;
                }
                if (start < 0)
                {
                    if (x > startLimit)
                        break;
                    start = x;
                }
                rightEnds[y] = x;
            }
        }

        // The bar: at least 3 consecutive rows ending at the same x (fur does not), searched bottom-up.
        var minRows = Math.Max(3, card.Height / 60);
        for (var y = rows - 1; y >= minRows - 1; y--)
        {
            if (rightEnds[y] < 0)
                continue;
            var run = 1;
            while (y - run >= 0 && rightEnds[y - run] >= 0 && Math.Abs(rightEnds[y - run] - rightEnds[y]) <= 2)
                run++;
            if (run >= minRows)
            {
                var right = rightEnds.Skip(y - run + 1).Take(run).Order().ElementAt(run / 2);
                var fill = ((right + 1.0) / card.Width - BarTrackLeft) / (BarTrackRight - BarTrackLeft);
                return Math.Clamp(fill, 0, 1);
            }
            y -= run - 1;
        }
        return 0;
    }

    // ------------------------------------------------------------------ panel

    private async Task<PetPanelScan?> ReadPanelAsync(Mat bgr, CancellationToken cancellationToken)
    {
        // From 70 %: with a large UI scale "Pet Insight" starts at ~73 % of the width.
        var rect = Clamp(bgr, new Rect((int)(bgr.Width * 0.70), (int)(bgr.Height * 0.10), (int)(bgr.Width * 0.30), (int)(bgr.Height * 0.25)));
        using var crop = new Mat(bgr, rect);
        using var bitmap = BitmapMat.ToBitmap(crop);
        var lines = await panelReader.ReadAsync(bitmap, cancellationToken);
        var insight = lines.FirstOrDefault(l => l.Text.Contains("Insight", StringComparison.OrdinalIgnoreCase));
        if (insight is null)
            return null;
        // Everything on the "Pet Insight" row: "Lv. 1 (5/25)" / "Lv. 3 (MAX)".
        var row = string.Join(" ", lines.Where(l => Math.Abs(l.Y + l.Height / 2 - (insight.Y + insight.Height / 2)) < insight.Height * 0.9)
            .OrderBy(l => l.X).Select(l => l.Text));
        var level = PetWindowText.ParsePanelLevel(row);
        var progress = PetWindowText.ParsePanelProgress(row);
        // Name: the largest text in the band directly above "Pet Insight".
        var name = lines.Where(l => l.Y < insight.Y - insight.Height * 0.5 && l.Y > insight.Y - bgr.Height * 0.12)
            .OrderByDescending(l => l.Height).FirstOrDefault();
        var raw = string.Join(" | ", lines.Select(l => l.Text));
        return new PetPanelScan(name?.Text.Trim(), level, raw, progress);
    }

    private static async Task<string> ReadTextAsync(Mat bgr, Rect rect, WindowsOcrLineReader reader, CancellationToken cancellationToken)
    {
        rect = Clamp(bgr, rect);
        if (rect.Width < 4 || rect.Height < 4)
            return "";
        using var crop = new Mat(bgr, rect);
        using var bitmap = BitmapMat.ToBitmap(crop);
        var lines = await reader.ReadAsync(bitmap, cancellationToken);
        return string.Join(" ", lines.Select(l => l.Text)).Trim();
    }

    private static Rect Clamp(Mat image, Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, image.Width - 1);
        var y = Math.Clamp(rect.Y, 0, image.Height - 1);
        return new Rect(x, y, Math.Clamp(rect.Width, 1, image.Width - x), Math.Clamp(rect.Height, 1, image.Height - y));
    }

    /// <summary>8x8 average hash, to recognise the same unknown portrait across scroll positions.</summary>
    public static ulong AverageHash(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new OpenCvSharp.Size(8, 8), interpolation: InterpolationFlags.Area);
        var mean = Cv2.Mean(small).Val0;
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (small.At<byte>(i / 8, i % 8) > mean)
                hash |= 1UL << i;
        }
        return hash;
    }

    public static int HashDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
