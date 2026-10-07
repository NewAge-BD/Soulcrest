using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Lines with direction arrows from the player to the marked targets, drawn over the in-game map
/// (the marked map area). Per-pixel alpha (UpdateLayeredWindow), click-through, excluded from screen
/// capture, so neither the map tracking nor the OCR ever sees it.
/// </summary>
public sealed class RouteOverlayForm : Form
{
    private readonly MapTrackingService _tracking;
    private readonly MapTargetsService _targets;
    private readonly SettingsService _settings;
    private int _pending;
    private bool _inRecordings;
    private readonly object _composeGate = new();
    private readonly System.Diagnostics.Stopwatch _sinceRender = System.Diagnostics.Stopwatch.StartNew();

    private readonly ProgressService _progress;
    private readonly Dictionary<string, Bitmap?> _icons = [];

    public RouteOverlayForm(MapTrackingService tracking, MapTargetsService targets, SettingsService settings, ProgressService progress)
    {
        _progress = progress;
        _progress.Changed += Schedule;
        _tracking = tracking;
        _targets = targets;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _tracking.Changed += Schedule;
        _tracking.PlacementMoved += Schedule;
        _targets.Changed += Schedule;
        _settings.Changed += Schedule;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inRecordings = _settings.Current.OverlaysInRecordings;
        NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
    }

    /// <summary>
    /// Events come from the tracking thread (up to 60 per second). The picture is composed in the
    /// background and only shown on the UI thread: composing on the UI thread kept it busy for seconds
    /// while the world map was open (diagnosis 2026-10-05). At most one picture waits.
    /// </summary>
    private void Schedule()
    {
        if (IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            // A full-screen picture (world map): at most 20 per second.
            var minimum = _tracking.Placement is { WorldMap: true } ? 50.0 : 0.0;
            var since = _sinceRender.Elapsed.TotalMilliseconds;
            if (since < minimum)
                await Task.Delay(TimeSpan.FromMilliseconds(minimum - since));
            _sinceRender.Restart();
            Volatile.Write(ref _pending, 0); // events from now on ask for the next picture
            Frame? frame;
            try
            {
                lock (_composeGate)
                {
                    if (IsDisposed)
                        return;
                    frame = Compose();
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException or IOException)
            {
                System.Diagnostics.Trace.TraceWarning("Route overlay picture failed: {0}", error.Message);
                return;
            }
            try { BeginInvoke(() => ShowFrame(frame)); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
            {
                frame?.Dispose();
            }
        });
    }

    /// <summary>A composed overlay picture (GDI bitmap with alpha) and where it goes; null = nothing to show.</summary>
    internal sealed record Frame(nint Bitmap, Point Location, Size Size) : IDisposable
    {
        public void Dispose() => NativeMethods.DeleteObject(Bitmap);
    }

    private Frame? Compose()
    {
        var placement = _tracking.Placement;
        var targets = _targets.Targets;
        var onMap = placement is null ? [] : targets.Select((t, i) => (Target: t, Index: i)).Where(t => t.Target.MapId == placement.MapId).ToList();
        if (placement is not null && _targets.Progression is { } next && next.MapId == placement.MapId)
            onMap.Add((next, -1)); // index -1: white (progression mode)
        if (!_settings.Current.ShowRoutesInGame)
            onMap.Clear();
        // Marked pets (right click or progression target) show all their spawns and soul monsters, even
        // when the pet symbols are off or filtered (user request 2026-10-06).
        var marked = onMap.Select(t => PetOf(t.Target)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // Pet targets carry their souls of the current level: "Superior Water Spirit 24/25" (user request 2026-10-06).
        for (var i = 0; i < onMap.Count; i++)
        {
            if (PetOf(onMap[i].Target) is { } pet)
                onMap[i] = (onMap[i].Target with { Name = $"{onMap[i].Target.Name} {_progress.SoulCount(pet)}" }, onMap[i].Index);
        }
        // Targets the line leads to from the player carry their distance in game metres (user request
        // 2026-10-06): "Ninir · 258 m". Chained targets start at their predecessor and have none.
        if (_tracking.Position is { } player && player.MapId == placement?.MapId
            && _progress.Maps.FirstOrDefault(m => m.Id == player.MapId)?.MetersPerPixel is { } metersPerPixel)
        {
            var ids = onMap.Select(t => t.Target.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < onMap.Count; i++)
            {
                var target = onMap[i].Target;
                if (target.After is { } after && ids.Contains(after))
                    continue;
                var pixels = Math.Sqrt((target.X - player.X) * (target.X - player.X) + (target.Y - player.Y) * (target.Y - player.Y));
                onMap[i] = (target with { Name = $"{target.Name} · {DistanceText(pixels * metersPerPixel)}" }, onMap[i].Index);
            }
        }
        var showPets = _settings.Current.ShowPetsInGame;
        var pets = placement is not null && (showPets || marked.Count > 0) ? VisiblePets(placement, marked, showPets) : [];
        var souls = placement is not null && marked.Count > 0 ? VisibleSoulMonsters(placement, marked) : [];
        var resources = placement is not null && _settings.Current.ShowResourcesInGame ? VisibleResources(placement) : [];
        if (placement is null || (onMap.Count == 0 && pets.Count == 0 && resources.Count == 0 && souls.Count == 0))
            return null;
        return RenderFrame(placement, onMap, pets, resources, souls);
    }

    internal static Frame RenderFrame(MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> onMap,
        IReadOnlyList<PetSymbol> pets, IReadOnlyList<ResourceSymbol>? resources = null, IReadOnlyList<SoulMonster>? souls = null)
    {
        // Drawn straight into a GDI DIB section that UpdateLayeredWindow takes as it is: converting a
        // full-screen picture with GetHbitmap cost 30 ms each time (measured 2026-10-05).
        var size = placement.Region.Size;
        var header = new NativeMethods.BitmapInfoHeader { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (dib == 0)
            throw new ExternalException("CreateDIBSection failed");
        try
        {
            using var surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
            using var graphics = Graphics.FromImage(surface);
            DrawInto(graphics, placement, onMap, pets, resources, souls); // a new DIB is all zeros: transparent
        }
        catch
        {
            NativeMethods.DeleteObject(dib);
            throw;
        }
        return new Frame(dib, placement.Region.Location, size);
    }

    /// <summary>UI thread: only hands the finished picture to Windows.</summary>
    private void ShowFrame(Frame? frame)
    {
        using (frame)
        {
            if (IsDisposed)
                return;
            if (_settings.Current.OverlaysInRecordings != _inRecordings)
            {
                _inRecordings = _settings.Current.OverlaysInRecordings;
                NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
            }
            if (frame is null)
            {
                if (Visible)
                    Hide();
                return;
            }
            if (!Visible)
                Show();
            Present(frame);
        }
    }

    /// <summary>The overlay picture for the marked map area: lines, arrows, rings and names (transparent elsewhere).</summary>
    public static Bitmap Draw(MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> targets, IReadOnlyList<PetSymbol>? pets = null,
        IReadOnlyList<ResourceSymbol>? resources = null, IReadOnlyList<SoulMonster>? souls = null)
    {
        var bitmap = new Bitmap(placement.Region.Width, placement.Region.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        DrawInto(graphics, placement, targets, pets, resources, souls);
        return bitmap;
    }

    /// <summary>Draws the overlay into a transparent, premultiplied 32-bit surface of the region's size.</summary>
    private static void DrawInto(Graphics graphics, MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> targets,
        IReadOnlyList<PetSymbol>? pets, IReadOnlyList<ResourceSymbol>? resources, IReadOnlyList<SoulMonster>? souls = null)
    {
        var region = placement.Region;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var origin = new PointF(region.X, region.Y);
        var area = new RectangleF(4, 4, region.Width - 8, region.Height - 8);
        PointF? player = placement.PlayerOnScreen is { } p ? Offset(p, origin) : null;
        foreach (var resource in resources ?? [])
            DrawResource(graphics, Offset(placement.WorldToScreen(resource.X, resource.Y), origin), resource.Icon);
        if (souls is { Count: > 0 })
        {
            using var ring = new Pen(Color.FromArgb(250, 204, 21), 1.5f);
            using var fill = new SolidBrush(Color.FromArgb(248, 113, 113));
            foreach (var soul in souls)
            {
                var at = Offset(placement.WorldToScreen(soul.X, soul.Y), origin);
                graphics.FillEllipse(fill, at.X - 4, at.Y - 4, 8, 8);
                graphics.DrawEllipse(ring, at.X - 4, at.Y - 4, 8, 8);
            }
        }
        foreach (var pet in pets ?? [])
            DrawPet(graphics, Offset(placement.WorldToScreen(pet.X, pet.Y), origin), pet);
        foreach (var (target, index) in targets)
        {
            var color = index < 0 ? Color.White : ColorTranslator.FromHtml(MapTargetsService.ColorOf(index));
            var at = Offset(placement.WorldToScreen(target.X, target.Y), origin);
            // A chained target (Shift + right click) starts at its predecessor, all others at the player.
            var before = target.After is { } after ? targets.FirstOrDefault(t => t.Target.Id == after).Target : null;
            PointF? start = before is not null ? Offset(placement.WorldToScreen(before.X, before.Y), origin) : player;
            if (start is { } from)
                DrawRoute(graphics, from, at, area, target.Name, color);
            else if (area.Contains(at))
                DrawTargetRing(graphics, at, area, target.Name, color); // world map of another zone: no player, rings only
        }
    }

    private static PointF Offset(PointF p, PointF origin) => new(p.X - origin.X, p.Y - origin.Y);

    /// <summary>A pet spawn to draw: portrait, label ("St. 1 · 6/25") and whether the pet is done (max).</summary>
    public sealed record PetSymbol(double X, double Y, Bitmap? Icon, string Label, bool Done);

    /// <summary>
    /// Distance as the game shows it: whole metres below 1 km, then kilometres with one decimal. Measured
    /// over the map: the game also counts height, so on slopes it shows a little more.
    /// </summary>
    internal static string DistanceText(double meters) =>
        meters < 999.5
            ? $"{Math.Round(meters):0} m"
            : (meters / 1000).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) + " km";

    /// <summary>A monster that drops the soul of a marked pet: a red dot.</summary>
    public sealed record SoulMonster(double X, double Y);

    /// <summary>A resource or hidden cube to draw with its legend icon.</summary>
    public sealed record ResourceSymbol(double X, double Y, Bitmap? Icon);

    private const int PetIconSize = 26;
    private const int ResourceIconSize = 20;
    private static readonly Font LabelFont = new("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point); // used under the sprite lock only

    /// <summary>
    /// Resources and hidden cubes of the categories shown in the interactive map's legend for this map,
    /// inside the marked map area (user request 2026-10-04).
    /// </summary>
    private List<ResourceSymbol> VisibleResources(MapPlacement placement)
    {
        if (_progress.MapDataDirectory is not { } mapdata
            || !_settings.Current.MapCategories.TryGetValue(placement.MapId, out var shown) || shown.Length == 0)
            return [];
        var wanted = shown.ToHashSet();
        var hiddenKinds = _settings.Current.MapHiddenKinds.TryGetValue(placement.MapId, out var hidden) ? hidden.ToHashSet() : [];
        var area = new RectangleF(placement.Region.X - 12, placement.Region.Y - 12, placement.Region.Width + 24, placement.Region.Height + 24);
        var result = new List<ResourceSymbol>();
        foreach (var resource in MapPetMarkers.ResourcesFor(mapdata, placement.MapId))
        {
            if (wanted.Contains(resource.Category) && (resource.KindKey is not { } kind || !hiddenKinds.Contains(kind)) && area.Contains(placement.WorldToScreen(resource.X, resource.Y)))
                result.Add(new ResourceSymbol(resource.X, resource.Y, ResourceIcon(resource.Icon, mapdata)));
        }
        return result;
    }

    /// <summary>Legend icon scaled once to the symbol size, aspect kept (drawn up to 60 times per second).</summary>
    private Bitmap? ResourceIcon(string? relative, string mapdata)
    {
        if (relative is null)
            return null;
        var key = "resource:" + relative;
        if (_icons.TryGetValue(key, out var cached))
            return cached;
        Bitmap? icon = null;
        var path = Path.Combine(mapdata, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            using var source = Image.FromStream(stream);
            var scale = Math.Min((float)ResourceIconSize / source.Width, (float)ResourceIconSize / source.Height);
            var size = new SizeF(source.Width * scale, source.Height * scale);
            icon = new Bitmap(ResourceIconSize, ResourceIconSize, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(icon);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, (ResourceIconSize - size.Width) / 2, (ResourceIconSize - size.Height) / 2, size.Width, size.Height);
        }
        _icons[key] = icon;
        return icon;
    }

    /// <summary>Legend icon on a dark disc, readable on any map colour; a dot if the icon is missing.</summary>
    private static void DrawResource(Graphics graphics, PointF at, Bitmap? icon)
    {
        const int disc = ResourceIconSize + 4;
        if (icon is null)
        {
            using var back = new SolidBrush(Color.FromArgb(150, 10, 14, 22));
            using var dot = new SolidBrush(Color.FromArgb(94, 234, 212));
            graphics.FillEllipse(back, at.X - disc / 2f, at.Y - disc / 2f, disc, disc);
            graphics.FillEllipse(dot, at.X - 4, at.Y - 4, 8, 8);
            return;
        }
        Bitmap sprite;
        lock (ResourceSprites)
        {
            if (!ResourceSprites.TryGetValue(icon, out sprite!))
            {
                sprite = new Bitmap(disc, disc, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(sprite);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var back = new SolidBrush(Color.FromArgb(150, 10, 14, 22));
                g.FillEllipse(back, 0, 0, disc - 1, disc - 1);
                g.DrawImage(icon, 2, 2, ResourceIconSize, ResourceIconSize);
                ResourceSprites[icon] = sprite;
            }
        }
        graphics.DrawImageUnscaled(sprite, (int)MathF.Round(at.X - disc / 2f), (int)MathF.Round(at.Y - disc / 2f));
    }

    /// <summary>The pet of a marked target: saved with the mark, or (older marks) found by its name.</summary>
    private string? PetOf(MapTarget target) =>
        target.PetId ?? (target.Kind.StartsWith("Pets", StringComparison.Ordinal) ? _progress.FindByName(target.Name)?.Id : null);

    /// <summary>Soul monsters of the marked pets inside the marked map area.</summary>
    private List<SoulMonster> VisibleSoulMonsters(MapPlacement placement, HashSet<string> marked)
    {
        if (_progress.MapDataDirectory is not { } mapdata)
            return [];
        var area = new RectangleF(placement.Region.X - 8, placement.Region.Y - 8, placement.Region.Width + 16, placement.Region.Height + 16);
        return MapPetMarkers.SoulMonstersFor(mapdata, placement.MapId)
            .Where(m => marked.Contains(m.PetId) && area.Contains(placement.WorldToScreen(m.X, m.Y)))
            .Select(m => new SoulMonster(m.X, m.Y)).ToList();
    }

    /// <summary>
    /// Pet spawns of the tracked map that lie inside the marked map area: all pets the filters allow
    /// (<paramref name="all"/>), and the marked pets always.
    /// </summary>
    private List<PetSymbol> VisiblePets(MapPlacement placement, HashSet<string> marked, bool all)
    {
        if (_progress.MapDataDirectory is not { } mapdata)
            return [];
        var area = new RectangleF(placement.Region.X - 20, placement.Region.Y - 20, placement.Region.Width + 40, placement.Region.Height + 40);
        var result = new List<PetSymbol>();
        foreach (var spawn in MapPetMarkers.For(mapdata, placement.MapId))
        {
            var isMarked = marked.Contains(spawn.PetId);
            if ((!all && !isMarked) || !area.Contains(placement.WorldToScreen(spawn.X, spawn.Y)))
                continue;
            var souls = _progress.Souls(spawn.PetId);
            if (!isMarked && _settings.Current.HidesLevel(_progress.Thresholds.Level(souls)))
                continue;
            if (!isMarked && _settings.Current.ExclusivePetsOnly && _progress.Catalog.Find(spawn.PetId) is { } pet && !pet.IsExclusiveTo(placement.MapId))
                continue;
            var done = _progress.Thresholds.IsMax(souls);
            result.Add(new PetSymbol(spawn.X, spawn.Y, IconFor(spawn, mapdata), _progress.PetMapLabel(spawn.PetId), done));
        }
        return result;
    }

    /// <summary>Portrait scaled once to the symbol size and cut round (drawn up to 60 times per second).</summary>
    private Bitmap? IconFor(PetSpawn spawn, string mapdata)
    {
        if (spawn.Icon is not { } relative)
            return null;
        if (_icons.TryGetValue(relative, out var cached))
            return cached;
        Bitmap? icon = null;
        var path = Path.Combine(mapdata, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            using var source = Image.FromStream(stream);
            icon = new Bitmap(PetIconSize, PetIconSize, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(icon);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var clip = new GraphicsPath();
            clip.AddEllipse(0, 0, PetIconSize - 1, PetIconSize - 1);
            g.SetClip(clip);
            g.DrawImage(source, 0, 0, PetIconSize, PetIconSize);
        }
        _icons[relative] = icon;
        return icon;
    }

    // Pet and resource symbols are drawn once into small sprites and then only copied: measuring and
    // drawing ~200 labels for each world-map picture took 67 ms, the overlay asked for 30 pictures per
    // second, and Soulcrest stopped responding while the world map was open (diagnosis 2026-10-05).
    private static readonly Dictionary<(Bitmap? Icon, string Label, bool Done), (Bitmap Sprite, PointF Centre)> PetSprites = [];
    private static readonly Dictionary<Bitmap, Bitmap> ResourceSprites = new(ReferenceEqualityComparer.Instance);
    private const int SpriteCacheLimit = 4000;

    private static void DrawPet(Graphics graphics, PointF at, PetSymbol pet)
    {
        var (sprite, centre) = PetSprite(pet);
        graphics.DrawImageUnscaled(sprite, (int)MathF.Round(at.X - centre.X), (int)MathF.Round(at.Y - centre.Y));
    }

    private static (Bitmap Sprite, PointF Centre) PetSprite(PetSymbol pet)
    {
        var key = (pet.Icon, pet.Label, pet.Done);
        lock (PetSprites)
        {
            if (PetSprites.TryGetValue(key, out var cached))
                return cached;
            if (PetSprites.Count > SpriteCacheLimit)
                ClearSprites(PetSprites.Values.Select(v => v.Sprite));
            var made = MakePetSprite(pet);
            PetSprites[key] = made;
            return made;
        }
    }

    private static void ClearSprites(IEnumerable<Bitmap> sprites)
    {
        foreach (var sprite in sprites.ToList())
            sprite.Dispose();
        PetSprites.Clear();
    }

    /// <summary>Portrait with ring and the label on a dark plate beside it, as one transparent picture.</summary>
    private static (Bitmap Sprite, PointF Centre) MakePetSprite(PetSymbol pet)
    {
        SizeF labelSize;
        using (var probe = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(probe))
            labelSize = measure.MeasureString(pet.Label, LabelFont);
        const float margin = 3; // half the shadow pen
        var height = (int)MathF.Ceiling(Math.Max(PetIconSize + 2 * margin, labelSize.Height));
        var width = (int)MathF.Ceiling(PetIconSize + 2 * margin + 3 + labelSize.Width);
        var sprite = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(sprite);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        var centre = new PointF(margin + PetIconSize / 2f, height / 2f);
        var rect = new RectangleF(margin, centre.Y - PetIconSize / 2f, PetIconSize, PetIconSize);
        using var ring = new Pen(pet.Done ? Color.FromArgb(160, 148, 163, 184) : Color.White, 2);
        using var shadow = new Pen(Color.FromArgb(170, 0, 0, 0), 4);
        graphics.DrawEllipse(shadow, rect);
        if (pet.Icon is { } icon)
        {
            if (pet.Done)
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4f });
                graphics.DrawImage(icon, Rectangle.Round(rect), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                graphics.DrawImage(icon, rect);
            }
        }
        graphics.DrawEllipse(ring, rect);

        // Label beside the symbol, dark plate for readability on any map colour.
        var plate = new RectangleF(rect.Right + 3, centre.Y - labelSize.Height / 2, labelSize.Width, labelSize.Height);
        using var back = new SolidBrush(Color.FromArgb(200, 15, 20, 30));
        using var text = new SolidBrush(pet.Done ? Color.FromArgb(148, 163, 184) : Color.FromArgb(248, 250, 252));
        graphics.FillRectangle(back, plate);
        graphics.DrawString(pet.Label, LabelFont, text, plate.Location);
        return (sprite, centre);
    }

    private static void DrawTargetRing(Graphics graphics, PointF target, RectangleF area, string name, Color color)
    {
        using var outline = new Pen(Color.FromArgb(170, 0, 0, 0), 7);
        using var ring = new Pen(color, 3);
        graphics.DrawEllipse(outline, target.X - 12, target.Y - 12, 24, 24);
        graphics.DrawEllipse(ring, target.X - 12, target.Y - 12, 24, 24);
        DrawLabel(graphics, name, new PointF(target.X + 16, target.Y - 10), color, area, target.X - 16);
    }

    private static void DrawRoute(Graphics graphics, PointF player, PointF target, RectangleF area, string name, Color color)
    {
        if (RouteGeometry.Clip(player, target, area) is not { } segment)
            return;
        if (segment.ReachesEnd)
        {
            // End at the target ring, not in its middle.
            var dx = segment.End.X - segment.Start.X;
            var dy = segment.End.Y - segment.Start.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length > 14)
                segment = (segment.Start, new PointF(segment.End.X - dx * 14 / length, segment.End.Y - dy * 14 / length), true);
        }
        using var outline = new Pen(Color.FromArgb(170, 0, 0, 0), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var line = new Pen(color, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(outline, segment.Start, segment.End);
        graphics.DrawLine(line, segment.Start, segment.End);
        foreach (var (point, angle) in RouteGeometry.Arrows(segment.Start, segment.End, 56))
            DrawChevron(graphics, point, angle, 9, color);

        PointF labelAt;
        float? leftOf = null;
        if (segment.ReachesEnd)
        {
            using var ring = new Pen(color, 3);
            graphics.DrawEllipse(outline, target.X - 12, target.Y - 12, 24, 24);
            graphics.DrawEllipse(ring, target.X - 12, target.Y - 12, 24, 24);
            labelAt = new PointF(target.X + 16, target.Y - 10);
            leftOf = target.X - 16;
        }
        else
        {
            // Target beyond the map area: big arrow at the edge, pointing on.
            var angle = MathF.Atan2(target.Y - player.Y, target.X - player.X) * 180f / MathF.PI;
            DrawChevron(graphics, segment.End, angle, 16, color);
            labelAt = new PointF(segment.End.X - 40, segment.End.Y + (segment.End.Y > area.Height / 2 ? -34 : 14));
        }
        DrawLabel(graphics, name, labelAt, color, area, leftOf);
    }

    private static void DrawChevron(Graphics graphics, PointF at, float angle, float size, Color color)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(at.X, at.Y);
        graphics.RotateTransform(angle);
        PointF[] shape = [new(size, 0), new(-size * 0.7f, -size * 0.75f), new(-size * 0.25f, 0), new(-size * 0.7f, size * 0.75f)];
        using var fill = new SolidBrush(color);
        using var edge = new Pen(Color.FromArgb(200, 0, 0, 0), 1.5f);
        graphics.FillPolygon(fill, shape);
        graphics.DrawPolygon(edge, shape);
        graphics.Restore(state);
    }

    /// <summary>
    /// Draws the name completely inside the map area (user report 2026-10-06: names were cut off at the
    /// edge). Beside a ring it moves to the ring's left side (<paramref name="leftOf"/>) when the right
    /// side is too narrow.
    /// </summary>
    private static void DrawLabel(Graphics graphics, string text, PointF at, Color color, RectangleF area, float? leftOf = null)
    {
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)FontStyle.Bold, 15, at, StringFormat.GenericDefault);
        var (dx, dy) = LabelShift(path.GetBounds(), area, leftOf);
        if (dx != 0 || dy != 0)
        {
            using var shift = new Matrix();
            shift.Translate(dx, dy);
            path.Transform(shift);
        }
        using var outline = new Pen(Color.FromArgb(220, 0, 0, 0), 4) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(color);
        graphics.DrawPath(outline, path);
        graphics.FillPath(fill, path);
    }

    /// <summary>How far a label with these bounds moves to lie inside the area (outline included).</summary>
    internal static (float Dx, float Dy) LabelShift(RectangleF bounds, RectangleF area, float? leftOf = null)
    {
        const float pad = 2; // half the outline
        bounds.Inflate(pad, pad);
        var dx = 0f;
        if (bounds.Right > area.Right)
            dx = leftOf is { } edge && edge - bounds.Width >= area.Left ? edge - bounds.Right : area.Right - bounds.Right;
        if (bounds.Left + dx < area.Left)
            dx = area.Left - bounds.Left;
        var dy = 0f;
        if (bounds.Bottom > area.Bottom)
            dy = area.Bottom - bounds.Bottom;
        if (bounds.Top + dy < area.Top)
            dy = area.Top - bounds.Top;
        return (dx, dy);
    }

    private void Present(Frame frame)
    {
        var screen = NativeMethods.GetDC(0);
        var memory = NativeMethods.CreateCompatibleDC(screen);
        var old = NativeMethods.SelectObject(memory, frame.Bitmap);
        try
        {
            var size = new NativeMethods.NativeSize(frame.Size.Width, frame.Size.Height);
            var source = new NativeMethods.NativePoint(0, 0);
            var target = new NativeMethods.NativePoint(frame.Location.X, frame.Location.Y);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memory, old);
            NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(0, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tracking.Changed -= Schedule;
            _tracking.PlacementMoved -= Schedule;
            _targets.Changed -= Schedule;
            _settings.Changed -= Schedule;
            _progress.Changed -= Schedule;
            lock (_composeGate)
            {
                foreach (var icon in _icons.Values)
                    icon?.Dispose();
                _icons.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
