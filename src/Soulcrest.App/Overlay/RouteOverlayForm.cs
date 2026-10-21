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
public sealed partial class RouteOverlayForm : Form
{
    private readonly MapTrackingService _tracking;
    private readonly MapTargetsService _targets;
    private readonly SettingsService _settings;
    private readonly LatestFrameDispatcher<Frame> _frames;
    private bool _inRecordings;
    private volatile bool _suppressed;
    private volatile bool _placementHidden, _foregroundSuppressed;
    private bool PresentationHidden => _placementHidden || _foregroundSuppressed;
    private volatile bool _disposing;
    private readonly object _composeGate = new();

    private readonly ProgressService _progress;
    private readonly Dictionary<string, Bitmap?> _icons = [];

    public RouteOverlayForm(MapTrackingService tracking, MapTargetsService targets, SettingsService settings, ProgressService progress)
    {
        _progress = progress;
        _progress.Changed += PreparationChanged;
        _tracking = tracking;
        _targets = targets;
        _settings = settings;
        _frames = new LatestFrameDispatcher<Frame>(ComposeLatest, action => { BeginInvoke(action); }, ShowFrame,
            error => System.Diagnostics.Trace.TraceWarning("Route overlay picture failed: {0}", error.Message));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _tracking.Changed += TrackingChanged;
        _tracking.PlacementMoved += Schedule;
        _targets.Changed += PreparationChanged;
        _settings.Changed += PreparationChanged;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed) Hide();
        ScheduleInvalidated();
    }

    /// <summary>Suppresses only presentation; tracking and target preparation remain current.</summary>
    public void SetPlacementHidden(bool hidden)
    {
        if (_placementHidden == hidden) return;
        _placementHidden = hidden;
        ApplyPresentationVisibility();
    }

    /// <summary>Keeps target and placement data current while the common foreground gate hides drawing.</summary>
    public void SetForegroundSuppressed(bool suppressed)
    {
        if (_foregroundSuppressed == suppressed) return;
        _foregroundSuppressed = suppressed;
        ApplyPresentationVisibility();
    }

    private void ApplyPresentationVisibility()
    {
        if (PresentationHidden)
        {
            Hide();
            _tracking.ResetOverlayPresentation();
        }
        ScheduleInvalidated();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !_suppressed && !PresentationHidden);

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
        Schedule();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _frames?.CancelPending();
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// Movement requests retain one composer and one latest UI frame. Semantic changes discard old
    /// work; ordinary 60-fps motion does not invalidate in-flight frames or impose another rate cap.
    /// </summary>
    private void Schedule()
    {
        if (!_disposing && !IsDisposed && IsHandleCreated) _frames?.Request();
    }

    private void ScheduleInvalidated()
    {
        if (!_disposing && !IsDisposed && IsHandleCreated) _frames?.Request(invalidatePending: true);
    }

    private void TrackingChanged() => ScheduleInvalidated();

    private void PreparationChanged()
    {
        InvalidatePreparation();
        ScheduleInvalidated();
    }

    private Frame? ComposeLatest()
    {
        lock (_composeGate)
        {
            if (_disposing || IsDisposed) return null;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var frame = Compose();
                if (frame is not null)
                {
                    frame.ComposeStartedAt = started;
                    frame.ComposedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                return frame;
            }
            finally { _tracking.ReportOverlayComposed(System.Diagnostics.Stopwatch.GetElapsedTime(started)); }
        }
    }

    /// <summary>A composed overlay picture (GDI bitmap with alpha) and where it goes; null = nothing to show.</summary>
    internal sealed record Frame(nint Bitmap, Point Location, Size Size, long CapturedAt = 0, MapPlacement? Placement = null, string ThemeId = "soulcrest-legacy") : IDisposable
    {
        // Written only by the compositor before publishing through the dispatcher's gate.
        internal long ComposeStartedAt { get; set; }
        internal long ComposedAt { get; set; }
        public void Dispose() => NativeMethods.DeleteObject(Bitmap);
    }

    private Frame? Compose()
    {
        if (_suppressed || !_settings.Current.MapOverlayEnabled || _tracking.EscMenuOpen
            || _tracking.Placement is not { } placement || !_tracking.IsCurrentVisualContext(placement.ContextGeneration))
            return null;
        var prepared = PreparationFor(placement);
        var onMap = TargetsWithDistance(prepared);
        var pets = VisiblePets(placement, prepared);
        var souls = VisibleSoulMonsters(placement, prepared);
        var resources = VisibleSymbols(placement, prepared);
        ApplyTargetSymbols(resources, prepared);
        var guards = VisibleGuardZones(placement, prepared);
        if (onMap.Count == 0 && pets.Count == 0 && resources.Count == 0 && souls.Count == 0 && guards is null && prepared.HiddenCubeGroups.Count == 0)
            return null;
        return RenderFrame(placement, onMap, pets, resources, souls, guards, prepared.TargetIcons, prepared.HiddenCubeGroups, OverlayThemeDefinition.Resolve(prepared.ThemeId));
    }

    internal static Frame RenderFrame(MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> onMap,
        IReadOnlyList<PetSymbol> pets, IReadOnlyList<ResourceSymbol>? resources = null, IReadOnlyList<SoulMonster>? souls = null,
        GuardZones? guards = null, IReadOnlyDictionary<string, Bitmap?>? targetIcons = null, IReadOnlyList<HiddenCubeGroup>? hiddenCubeGroups = null, OverlayThemeDefinition? theme = null)
    {
        theme ??= OverlayThemeDefinition.Legacy;
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
            DrawInto(graphics, placement, onMap, pets, resources, souls, guards, targetIcons, hiddenCubeGroups, theme); // a new DIB is all zeros: transparent
        }
        catch
        {
            NativeMethods.DeleteObject(dib);
            throw;
        }
        return new Frame(dib, placement.Region.Location, size, placement.CapturedAt, placement, theme.Id);
    }

    /// <summary>UI thread: hands the latest picture to Windows; the dispatcher owns its lifetime.</summary>
    private void ShowFrame(Frame? frame)
    {
        var uiEnteredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_disposing || IsDisposed || !IsHandleCreated) return;
        if (_settings.Current.OverlaysInRecordings != _inRecordings)
        {
            _inRecordings = _settings.Current.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        }
        if (_suppressed || PresentationHidden || frame is null || !_settings.Current.MapOverlayEnabled
            || !_tracking.IsCurrentVisualContext(frame.Placement?.ContextGeneration ?? -1)
            || !MatchesCurrentPlacement(frame, _tracking.Placement, _tracking.ContextGeneration) || !MatchesCurrentTheme(frame, _settings.Current.OverlayTheme))
        {
            _tracking.ResetOverlayPresentation();
            if (Visible) Hide();
            return;
        }
        if (!Visible)
        {
            _tracking.ResetOverlayPresentation();
            Show();
        }
        var presentStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Present(frame);
        _tracking.ReportOverlayShown(new OverlayTiming(frame.CapturedAt, frame.ComposeStartedAt, frame.ComposedAt,
            uiEnteredAt, presentStartedAt, System.Diagnostics.Stopwatch.GetTimestamp()));
    }

    // Do not reject ordinary flow movement. Only a changed map/surface/region makes a composed
    // picture unusable if that context changed after the dispatcher claimed its UI slot.
    internal static bool MatchesCurrentPlacement(Frame frame, MapPlacement? current, long? contextGeneration = null) =>
        frame.Placement is not { } composed ? contextGeneration is null : current is not null
            && composed.MapId == current.MapId && composed.WorldMap == current.WorldMap && composed.Region == current.Region
            && composed.ContextGeneration == current.ContextGeneration
            && (contextGeneration is null || composed.ContextGeneration == contextGeneration);

    internal static bool MatchesCurrentTheme(Frame frame, string? themeId) =>
        frame.ThemeId == OverlayThemeDefinition.Resolve(themeId).Id;

    /// <summary>The overlay picture for the marked map area: lines, arrows, rings and names (transparent elsewhere).</summary>
    public static Bitmap Draw(MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> targets, IReadOnlyList<PetSymbol>? pets = null,
        IReadOnlyList<ResourceSymbol>? resources = null, IReadOnlyList<SoulMonster>? souls = null, GuardZones? guards = null,
        IReadOnlyDictionary<string, Bitmap?>? targetIcons = null, IReadOnlyList<HiddenCubeGroup>? hiddenCubeGroups = null)
        => DrawThemed(OverlayThemeDefinition.Legacy, placement, targets, pets, resources, souls, guards, targetIcons, hiddenCubeGroups);

    internal static Bitmap DrawThemed(OverlayThemeDefinition theme, MapPlacement placement,
        IReadOnlyList<(MapTarget Target, int Index)> targets, IReadOnlyList<PetSymbol>? pets = null,
        IReadOnlyList<ResourceSymbol>? resources = null, IReadOnlyList<SoulMonster>? souls = null, GuardZones? guards = null,
        IReadOnlyDictionary<string, Bitmap?>? targetIcons = null, IReadOnlyList<HiddenCubeGroup>? hiddenCubeGroups = null)
    {
        var bitmap = new Bitmap(placement.Region.Width, placement.Region.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        DrawInto(graphics, placement, targets, pets, resources, souls, guards, targetIcons, hiddenCubeGroups, theme);
        return bitmap;
    }

    /// <summary>Draws the overlay into a transparent, premultiplied 32-bit surface of the region's size.</summary>
    private static void DrawInto(Graphics graphics, MapPlacement placement, IReadOnlyList<(MapTarget Target, int Index)> targets,
        IReadOnlyList<PetSymbol>? pets, IReadOnlyList<ResourceSymbol>? resources, IReadOnlyList<SoulMonster>? souls, GuardZones? guards,
        IReadOnlyDictionary<string, Bitmap?>? targetIcons, IReadOnlyList<HiddenCubeGroup>? hiddenCubeGroups, OverlayThemeDefinition theme)
    {
        var region = placement.Region;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var origin = new PointF(region.X, region.Y);
        if (guards is not null)
            DrawGuardZones(graphics, placement, origin, guards); // beneath everything else
        DrawHiddenCubeGroups(graphics, placement, origin, hiddenCubeGroups ?? [], theme);
        var area = new RectangleF(4, 4, region.Width - 8, region.Height - 8);
        PointF? player = placement.PlayerOnScreen is { } p ? Offset(p, origin) : null;
        foreach (var resource in resources ?? [])
        {
            var at = Offset(placement.WorldToScreen(resource.X, resource.Y), origin);
            DrawResource(graphics, at, resource.Icon, resource.Color, resource.Opacity, theme,
                drawBackground: placement.WorldMap || resource.Opacity >= 1f);
            if (resource.OutlineColor is { } outlineColor)
            {
                using var rim = new Pen(Color.FromArgb(230, ColorTranslator.FromHtml(outlineColor)), 1.5f);
                graphics.DrawEllipse(rim, at.X - 12, at.Y - 12, 24, 24);
            }
        }
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
            DrawPet(graphics, Offset(placement.WorldToScreen(pet.X, pet.Y), origin), pet, theme,
                drawBackground: placement.WorldMap || !pet.Done);
        foreach (var (target, index) in targets)
        {
            var color = target.Color is { } explicitColor ? ColorTranslator.FromHtml(explicitColor)
                : index < 0 ? theme.IsLegacy ? Color.White : theme.Primary : ColorTranslator.FromHtml(MapTargetsService.ColorOf(target, index));
            var preview = target.Id == "boss-next";
            var dashed = preview || target.MonsterBranch;
            if (preview) color = Color.FromArgb(105, color);
            if (target.Completed) color = Color.FromArgb(71, color);
            // User request 2026-10-20: faded minimap markers need no filled backing.
            var drawBackground = placement.WorldMap || color.A == 255;
            var at = Offset(placement.WorldToScreen(target.X, target.Y), origin);
            // A chained target (Shift + right click) starts at its predecessor, all others at the player.
            var before = target.After is { } after ? targets.FirstOrDefault(t => t.Target.Id == after).Target : null;
            PointF? start = target.BranchX is { } bx && target.BranchY is { } by ? Offset(placement.WorldToScreen(bx, by), origin)
                : before is not null ? Offset(placement.WorldToScreen(before.X, before.Y), origin) : target.Completed ? null : player;
            // Resources, hidden cubes, sealed dungeons, Kibelisks and quests: their icon beside the ring instead of
            // the name, so the label does not cover the symbols around it (user requests 2026-10-10/11); the
            // distance stays.
            var icon = ShowsIconOnly(target) && targetIcons?.GetValueOrDefault(target.Id) is { } bitmap ? bitmap : null;
            var label = icon is null ? target.Name : DistanceOf(target.Name);
            // Lines leave a predecessor's ring (or the player marker) at its rim, never from its centre.
            var startGap = target.BranchX is not null ? 0f : before is not null ? RingRadius + 2 : PlayerGap;
            if (start is { } from)
                DrawRoute(graphics, from, at, area, label, color, dashed, icon, startGap, theme, drawBackground);
            else if (area.Contains(at))
                DrawTargetRing(graphics, at, area, label, color, dashed, icon, theme, drawBackground); // world map of another zone: no player, rings only
        }
    }

    private static PointF Offset(PointF p, PointF origin) => new(p.X - origin.X, p.Y - origin.Y);

    // A target ring lies around the resource symbol (disc of 24 px) instead of on its rim (user request
    // 2026-10-10: the markers covered the resource symbols).
    internal const float RingRadius = 15;
    private const float PlayerGap = 10;
    private const int TargetIconSize = 18;

    /// <summary>
    /// Resources, hidden cubes, sealed dungeons, Kibelisks and quest NPCs are labelled with their icon instead of
    /// their name, in every mode: marked by hand, progression and leveling routes (user request 2026-10-11).
    /// </summary>
    internal static bool ShowsIconOnly(MapTarget target) => target.PetId is null && !target.MonsterBranch
        && (target.Kind.StartsWith("Resources", StringComparison.Ordinal) || target.Kind.StartsWith("Collectibles", StringComparison.Ordinal)
            || target.Kind.StartsWith("NPCs", StringComparison.Ordinal) || CategoryOf(target.Kind) is "Sealed Dungeon" or "Kibelisk");

    /// <summary>"Locations · Kibelisk" → "Kibelisk"; a progression target's kind is the category alone.</summary>
    internal static string CategoryOf(string kind) => kind.LastIndexOf(" · ", StringComparison.Ordinal) is var i and >= 0 ? kind[(i + 3)..] : kind;

    /// <summary>
    /// A target with its icon beside the ring has no symbol inside it; one there covered the game's own symbol
    /// (user report 2026-10-10), also when the legend shows the category.
    /// </summary>
    internal static void RemoveSymbolInRing(List<ResourceSymbol> resources, MapTarget target) =>
        resources.RemoveAll(r => r.X == target.X && r.Y == target.Y);

    /// <summary>"Diamond · 99 m" → "99 m"; a name without distance → "".</summary>
    internal static string DistanceOf(string name) => name.LastIndexOf(" · ", StringComparison.Ordinal) is var i and >= 0 ? name[(i + 3)..] : "";

    /// <summary>Guards to mark and the radius of their danger circle in world pixels.</summary>
    public sealed record GuardZones(IReadOnlyList<GuardPost> Guards, double RadiusWorld);

    /// <summary>
    /// One half-transparent red area: the circles are filled as a single path, so where guards stand
    /// together the red does not stack up darker, with a thin rim around the whole area.
    /// </summary>
    private static void DrawGuardZones(Graphics graphics, MapPlacement placement, PointF origin, GuardZones zones)
    {
        if (zones.Guards.Count == 0)
            return;
        var first = zones.Guards[0];
        var centre = placement.WorldToScreen(first.X, first.Y);
        var edge = placement.WorldToScreen(first.X + zones.RadiusWorld, first.Y);
        var radius = MathF.Max(2, MathF.Sqrt((edge.X - centre.X) * (edge.X - centre.X) + (edge.Y - centre.Y) * (edge.Y - centre.Y)));
        using var area = new GraphicsPath(FillMode.Winding);
        foreach (var guard in zones.Guards)
        {
            var at = Offset(placement.WorldToScreen(guard.X, guard.Y), origin);
            area.AddEllipse(at.X - radius, at.Y - radius, 2 * radius, 2 * radius);
        }
        using var fill = new SolidBrush(Color.FromArgb(80, 239, 68, 68));
        graphics.FillPath(fill, area);
        // The rim of the union: the path outline, cut to the outside of the filled area.
        using var region = new Region(area);
        var state = graphics.Save();
        graphics.ExcludeClip(region);
        using var rim = new Pen(Color.FromArgb(170, 239, 68, 68), 3f);
        graphics.DrawPath(rim, area);
        graphics.Restore(state);
    }

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

    /// <summary>A non-pet map marker to draw with its legend icon.</summary>
    public sealed record ResourceSymbol(double X, double Y, Bitmap? Icon, string? Color = null, float Opacity = 1f, string? OutlineColor = null);

    private const int PetIconSize = 26;
    private const int ResourceIconSize = 20;
    private static readonly Font LabelFont = new("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point); // used under the sprite lock only

    internal static MapSurface SurfaceOf(MapPlacement placement) => placement.WorldMap ? MapSurface.Worldmap : MapSurface.Minimap;

    internal static bool SymbolVisible(AppSettings settings, string map, MapSurface surface, MapResource symbol)
    {
        var split = symbol.Category.IndexOf('/');
        return split > 0 && MapLayerVisibility.KindVisible(settings, map, surface,
            symbol.Category[..split], symbol.Category[(split + 1)..], symbol.Kind);
    }

    /// <summary>Marked pets remain visible even when their genus or level is hidden.</summary>
    internal static bool PetVisible(AppSettings settings, string map, MapSurface surface, PetSpawn spawn, bool marked)
    {
        if (marked) return true;
        var split = spawn.Category?.IndexOf('/') ?? -1;
        return MapLayerVisibility.CategoryVisible(settings, map, surface, "Pets", split >= 0 ? spawn.Category![(split + 1)..] : "");
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

    /// <summary>Legend icon with optional backing; faded minimap symbols leave the map visible.</summary>
    private static void DrawResource(Graphics graphics, PointF at, Bitmap? icon, string? tint, float opacity, OverlayThemeDefinition theme, bool drawBackground)
    {
        const int disc = ResourceIconSize + 4;
        if (tint is not null)
        {
            var color = ColorTranslator.FromHtml(tint);
            using var back = new SolidBrush(theme.IsLegacy ? Color.FromArgb((int)(210 * opacity), 10, 14, 22) : Color.FromArgb((int)(theme.Surface.A * opacity), theme.Surface));
            if (drawBackground) graphics.FillEllipse(back, at.X - disc / 2f, at.Y - disc / 2f, disc, disc);
            if (icon is null)
            {
                using var dot = new SolidBrush(Color.FromArgb((int)(255 * opacity), color));
                graphics.FillEllipse(dot, at.X - 5, at.Y - 5, 10, 10);
            }
            else
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix([
                    [0, 0, 0, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0],
                    [0, 0, 0, opacity, 0], [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1]]));
                graphics.DrawImage(icon, new Rectangle((int)at.X - ResourceIconSize / 2, (int)at.Y - ResourceIconSize / 2, ResourceIconSize, ResourceIconSize),
                    0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
            }
            return;
        }
        if (icon is null)
        {
            using var back = new SolidBrush(theme.IsLegacy ? Color.FromArgb(150, 10, 14, 22) : theme.Surface);
            using var dot = new SolidBrush(theme.IsLegacy ? Color.FromArgb(94, 234, 212) : theme.Mint);
            if (drawBackground) graphics.FillEllipse(back, at.X - disc / 2f, at.Y - disc / 2f, disc, disc);
            graphics.FillEllipse(dot, at.X - 4, at.Y - 4, 8, 8);
            return;
        }
        if (!drawBackground)
        {
            // The cached sprite contains the disc, so copy only the original icon here.
            graphics.DrawImage(icon, new Rectangle((int)MathF.Round(at.X - ResourceIconSize / 2f),
                (int)MathF.Round(at.Y - ResourceIconSize / 2f), ResourceIconSize, ResourceIconSize));
            return;
        }
        Bitmap sprite;
        lock (ResourceSprites)
        {
            if (!ResourceSprites.TryGetValue((icon, theme.Id), out sprite!))
            {
                sprite = new Bitmap(disc, disc, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(sprite);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var back = new SolidBrush(theme.IsLegacy ? Color.FromArgb(150, 10, 14, 22) : theme.Surface);
                g.FillEllipse(back, 0, 0, disc - 1, disc - 1);
                g.DrawImage(icon, 2, 2, ResourceIconSize, ResourceIconSize);
                ResourceSprites[(icon, theme.Id)] = sprite;
            }
        }
        graphics.DrawImageUnscaled(sprite, (int)MathF.Round(at.X - disc / 2f), (int)MathF.Round(at.Y - disc / 2f));
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
    private static readonly Dictionary<(Bitmap? Icon, string Label, bool Done, string Theme, bool Background), (Bitmap Sprite, PointF Centre)> PetSprites = [];
    private static readonly Dictionary<(Bitmap Icon, string Theme), Bitmap> ResourceSprites = [];
    private const int SpriteCacheLimit = 4000;

    private static void DrawPet(Graphics graphics, PointF at, PetSymbol pet, OverlayThemeDefinition theme, bool drawBackground)
    {
        var (sprite, centre) = PetSprite(pet, theme, drawBackground);
        graphics.DrawImageUnscaled(sprite, (int)MathF.Round(at.X - centre.X), (int)MathF.Round(at.Y - centre.Y));
    }

    private static (Bitmap Sprite, PointF Centre) PetSprite(PetSymbol pet, OverlayThemeDefinition theme, bool drawBackground)
    {
        var key = (pet.Icon, pet.Label, pet.Done, theme.Id, drawBackground);
        lock (PetSprites)
        {
            if (PetSprites.TryGetValue(key, out var cached))
                return cached;
            if (PetSprites.Count > SpriteCacheLimit)
                ClearSprites(PetSprites.Values.Select(v => v.Sprite));
            var made = MakePetSprite(pet, theme, drawBackground);
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
    private static (Bitmap Sprite, PointF Centre) MakePetSprite(PetSymbol pet, OverlayThemeDefinition theme, bool drawBackground)
    {
        using var themedFont = theme.IsLegacy ? null : theme.Body(12, FontStyle.Bold);
        var labelFont = themedFont ?? LabelFont;
        SizeF labelSize;
        using (var probe = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(probe))
            labelSize = measure.MeasureString(pet.Label, labelFont);
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
        using var ring = new Pen(pet.Done ? Color.FromArgb(160, 148, 163, 184) : theme.IsLegacy ? Color.White : theme.Gold, 2);
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
        using var back = new SolidBrush(theme.IsLegacy ? Color.FromArgb(200, 15, 20, 30) : theme.Surface);
        using var text = new SolidBrush(pet.Done ? Color.FromArgb(148, 163, 184) : theme.IsLegacy ? Color.FromArgb(248, 250, 252) : theme.Primary);
        if (drawBackground)
        {
            if (theme.IsLegacy) graphics.FillRectangle(back, plate);
            else theme.Panel(graphics, Rectangle.Ceiling(plate));
        }
        graphics.DrawString(pet.Label, labelFont, text, plate.Location);
        return (sprite, centre);
    }

    private static void DrawTargetRing(Graphics graphics, PointF target, RectangleF area, string name, Color color, bool dashed, Bitmap? icon, OverlayThemeDefinition theme, bool drawBackground)
    {
        using var outline = new Pen(Color.FromArgb(Math.Min(dashed ? 50 : 170, (int)color.A), 0, 0, 0), 7) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        using var ring = new Pen(color, 3) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        graphics.DrawEllipse(outline, target.X - RingRadius, target.Y - RingRadius, 2 * RingRadius, 2 * RingRadius);
        graphics.DrawEllipse(ring, target.X - RingRadius, target.Y - RingRadius, 2 * RingRadius, 2 * RingRadius);
        DrawTargetLabel(graphics, name, icon, target, color, area, theme, drawBackground);
    }

    /// <summary>
    /// Beside the ring: the resource icon (with the distance after it) or the name. The icon sits on the side
    /// facing away from the line that arrives (<paramref name="from"/>), so the line never runs through it.
    /// </summary>
    private static void DrawTargetLabel(Graphics graphics, string name, Bitmap? icon, PointF target, Color color, RectangleF area, OverlayThemeDefinition theme, bool drawBackground, PointF? from = null)
    {
        if (icon is null)
        {
            DrawLabel(graphics, name, new PointF(target.X + RingRadius + 4, target.Y - 10), color, area, theme, drawBackground, target.X - RingRadius - 4);
            return;
        }
        var (dirX, dirY) = (1f, 0f);
        if (from is { } source && MathF.Sqrt((target.X - source.X) * (target.X - source.X) + (target.Y - source.Y) * (target.Y - source.Y)) is var length and > 1)
            (dirX, dirY) = ((target.X - source.X) / length, (target.Y - source.Y) / length);
        // Away from the line first, then to either side, then back towards it: the first spot fully inside the map.
        var reach = RingRadius + 5 + TargetIconSize / 2f;
        RectangleF BoxAt(float x, float y) => new(target.X + x * reach - TargetIconSize / 2f, target.Y + y * reach - TargetIconSize / 2f, TargetIconSize, TargetIconSize);
        var inner = RectangleF.Inflate(area, -3, -3);
        var box = new[] { BoxAt(dirX, dirY), BoxAt(-dirY, dirX), BoxAt(dirY, -dirX), BoxAt(-dirX, -dirY) }
            .FirstOrDefault(inner.Contains, BoxAt(dirX, dirY));
        using (var back = new SolidBrush(theme.IsLegacy ? Color.FromArgb(Math.Min(210, (int)color.A), 15, 20, 30)
            : Color.FromArgb(Math.Min(theme.Surface.A, color.A), theme.Surface)))
            if (drawBackground) graphics.FillEllipse(back, RectangleF.Inflate(box, 3, 3));
        using (var rim = new Pen(color, 1.5f))
            graphics.DrawEllipse(rim, RectangleF.Inflate(box, 3, 3));
        if (color.A == 255)
            graphics.DrawImage(icon, box);
        else
        {
            // Fades with its ring: completed leveling stops, the boss preview.
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = color.A / 255f });
            graphics.DrawImage(icon, Rectangle.Round(box), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
        }
        if (name.Length > 0)
            DrawLabel(graphics, name, new PointF(box.Right + 5, box.Top - 1), color, area, theme, drawBackground, box.Left - 5);
    }

    private static void DrawRoute(Graphics graphics, PointF player, PointF target, RectangleF area, string name, Color color, bool dashed,
        Bitmap? icon, float startGap, OverlayThemeDefinition theme, bool drawBackground)
    {
        {
            // Start at the rim of the ring (or the player marker) the line leaves, not in its middle.
            var dx = target.X - player.X;
            var dy = target.Y - player.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (startGap > 0 && length > startGap + RingRadius + 2)
                player = new PointF(player.X + dx * startGap / length, player.Y + dy * startGap / length);
        }
        if (RouteGeometry.Clip(player, target, area) is not { } segment)
            return;
        if (segment.ReachesEnd)
        {
            // End at the target ring, not in its middle.
            var dx = segment.End.X - segment.Start.X;
            var dy = segment.End.Y - segment.Start.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            const float gap = RingRadius + 2;
            if (length > gap)
                segment = (segment.Start, new PointF(segment.End.X - dx * gap / length, segment.End.Y - dy * gap / length), true);
        }
        using var outline = new Pen(Color.FromArgb(Math.Min(dashed ? 50 : 170, (int)color.A), 0, 0, 0), 7) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var line = new Pen(color, 3.5f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid, StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(outline, segment.Start, segment.End);
        graphics.DrawLine(line, segment.Start, segment.End);
        foreach (var (point, angle) in RouteGeometry.Arrows(segment.Start, segment.End, 56))
            DrawChevron(graphics, point, angle, 9, color);

        if (segment.ReachesEnd)
        {
            using var ring = new Pen(color, 3) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            graphics.DrawEllipse(outline, target.X - RingRadius, target.Y - RingRadius, 2 * RingRadius, 2 * RingRadius);
            graphics.DrawEllipse(ring, target.X - RingRadius, target.Y - RingRadius, 2 * RingRadius, 2 * RingRadius);
            DrawTargetLabel(graphics, name, icon, target, color, area, theme, drawBackground, player);
        }
        else
        {
            // Target beyond the map area: big arrow at the edge, pointing on.
            var angle = MathF.Atan2(target.Y - player.Y, target.X - player.X) * 180f / MathF.PI;
            DrawChevron(graphics, segment.End, angle, 16, color);
            var labelAt = new PointF(segment.End.X - 40, segment.End.Y + (segment.End.Y > area.Height / 2 ? -34 : 14));
            if (icon is null)
                DrawLabel(graphics, name, labelAt, color, area, theme, drawBackground);
            else
                DrawTargetLabel(graphics, name, icon, new PointF(labelAt.X - RingRadius, labelAt.Y + 10), color, area, theme, drawBackground);
        }
    }

    private static void DrawChevron(Graphics graphics, PointF at, float angle, float size, Color color)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(at.X, at.Y);
        graphics.RotateTransform(angle);
        PointF[] shape = [new(size, 0), new(-size * 0.7f, -size * 0.75f), new(-size * 0.25f, 0), new(-size * 0.7f, size * 0.75f)];
        using var fill = new SolidBrush(color);
        using var edge = new Pen(Color.FromArgb(Math.Min(200, (int)color.A), 0, 0, 0), 1.5f);
        graphics.FillPolygon(fill, shape);
        graphics.DrawPolygon(edge, shape);
        graphics.Restore(state);
    }

    /// <summary>
    /// Draws the name completely inside the map area (user report 2026-10-06: names were cut off at the
    /// edge). Beside a ring it moves to the ring's left side (<paramref name="leftOf"/>) when the right
    /// side is too narrow.
    /// </summary>
    private static void DrawLabel(Graphics graphics, string text, PointF at, Color color, RectangleF area, OverlayThemeDefinition theme, bool drawBackground, float? leftOf = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var path = new GraphicsPath();
        using var font = theme.Body(15, FontStyle.Bold);
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, at, StringFormat.GenericDefault);
        var (dx, dy) = LabelShift(path.GetBounds(), area, leftOf);
        if (dx != 0 || dy != 0)
        {
            using var shift = new Matrix();
            shift.Translate(dx, dy);
            path.Transform(shift);
        }
        if (!theme.IsLegacy && drawBackground)
        {
            var plate = RectangleF.Inflate(path.GetBounds(), 2, 2);
            theme.Panel(graphics, Rectangle.Ceiling(plate));
        }
        using var outline = new Pen(Color.FromArgb(Math.Min(220, (int)color.A), 0, 0, 0), 4) { LineJoin = LineJoin.Round };
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
            _disposing = true;
            _frames.Dispose();
            _tracking.Changed -= TrackingChanged;
            _tracking.PlacementMoved -= Schedule;
            _targets.Changed -= PreparationChanged;
            _settings.Changed -= PreparationChanged;
            _progress.Changed -= PreparationChanged;
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
