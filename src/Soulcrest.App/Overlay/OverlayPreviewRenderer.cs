using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.MapTracking;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace Soulcrest.App.Overlay;

/// <summary>Read-only examples drawn by the production renderers; never starts tracking or raises events.</summary>
public sealed class OverlayPreviewRenderer : IDisposable
{
    private readonly PetCatalog _catalog;
    private readonly string? _mapDataDirectory;
    private readonly TimerIconCache _timerIcons;
    private readonly DaevanionOverlayRenderer _daevanion = new();
    private readonly LootOverlayIconCache _lootIcons = new();
    private readonly Dictionary<string, Bitmap> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<BossPlace> _bosses;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private bool _disposed;

    public OverlayPreviewRenderer(ProgressService progress) : this(progress.Catalog, progress.MapDataDirectory) { }

    internal OverlayPreviewRenderer(PetCatalog catalog, string? mapDataDirectory = null)
    {
        _catalog = catalog;
        _mapDataDirectory = mapDataDirectory;
        _timerIcons = new TimerIconCache(mapDataDirectory);
        _bosses = LoadBosses();
    }

    /// <summary>Returns caller-owned logical 1x pixels. maximumWidth controls timer wrapping and bound previews.</summary>
    public Bitmap Render(string id, AppSettings settings, int? maximumWidth = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The shop keeps its original native-card styling regardless of the global overlay theme.
        if (id == "merchant") return FitBound(Merchant(settings), maximumWidth);
        if (id == "daevanion") return FitBound(_daevanion.Preview(language: settings.LootNameLanguage), maximumWidth);
        var theme = OverlayThemeDefinition.Resolve(settings.OverlayTheme);
        return id switch
        {
            "loot" => Loot(settings, theme),
            "farm-loot" => LootOverlayRenderer.RenderPreview(FarmLootExample(settings.LootNameLanguage), theme.Id,
                animationSeconds: 1.4, icons: _lootIcons, backgroundOpacity: settings.LootOverlayBackgroundOpacity),
            "abyss-rank" => AbyssRankOverlayRenderer.RenderPreview(AbyssRankExample(includeKillFeed: true) with
                { KillFeedEnabled = settings.AbyssKillFeedEnabled, ShowKills = settings.AbyssKillFeedShowKills,
                    ShowDeaths = settings.AbyssKillFeedShowDeaths, ShowAssists = settings.AbyssKillFeedShowAssists }, theme.Id,
                language: settings.LootNameLanguage, icons: _lootIcons),
            "boss" => Boss(settings, theme),
            "leveling" => Leveling(settings, theme),
            "alerts" => Timers(settings, theme, maximumWidth),
            "notifications" => Notifications(settings, theme),
            "event-timeline" => Timeline(settings, theme),
            "map" => FitBound(Map(settings, theme), maximumWidth),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown overlay.")
        };
    }

    public string ToDataUri(string id, AppSettings settings, int? maximumWidth = null)
    {
        using var bitmap = Render(id, settings, maximumWidth);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private Bitmap Loot(AppSettings settings, OverlayThemeDefinition theme)
    {
        var pets = _catalog.Pets.Take(3).ToArray();
        PetOverlayForm.LootRow Row(int index, int quantity)
        {
            var pet = pets.ElementAtOrDefault(index);
            var name = pet?.DisplayName(settings.LootNameLanguage) ?? UiText.T("Vorschau");
            return new(name, pet?.Genus ?? "cogni", 1, false, 17, 25,
                Image(pet?.Icon), quantity, SoulName: name, PetId: pet?.Id);
        }
        PetOverlayForm.LootRow[] focus = [Row(0, 2)];
        PetOverlayForm.LootRow[] drops = [Row(1, 1), Row(2, 3)];
        return Surface(new(PetOverlayFormWidth, PetOverlayForm.LootLogicalHeight(1, 2, theme: theme)),
            graphics => PetOverlayForm.PaintLootPanel(graphics, focus, drops, running: true, locked: true, theme: theme));
    }

    private const int PetOverlayFormWidth = 360;
    internal static AbyssRankOverlaySnapshot AbyssRankExample(bool includeKillFeed = false) => new(12_500, CharacterBound: true, Confirmed: true)
    {
        WorldContextVisible = true,
        SessionId = "synthetic-abyss-session", SessionActive = true,
        SessionNetRankPoints = 1_240, SessionRankPointsPerHour = 3_720,
        KillFeed = includeKillFeed ?
        [
            new(new DateTimeOffset(2026, 10, 26, 12, 0, 2, TimeSpan.Zero), AbyssKillFeedKind.Kill, "Example opponent", 120) { Generation = 1, Sequence = 3 },
            new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), AbyssKillFeedKind.Assist, "Example opponent", null) { Generation = 1, Sequence = 2 },
            new(new DateTimeOffset(2026, 10, 26, 11, 59, 58, TimeSpan.Zero), AbyssKillFeedKind.Death, null, -45) { Generation = 1, Sequence = 1 }
        ] : []
    };

    internal static LootOverlaySnapshot FarmLootExample(string language) => new("synthetic-session", true, 184_720, 684_720,
    [
        new("synthetic-crystal", MonsterLootItemCatalog.Default.Name(531900004, language), 24, LootOverlayDropKind.SoulCrystal)
            { IconPath = MonsterLootIconCatalog.Default.ItemAsset(531900004) },
        new("synthetic-helm", MonsterLootItemCatalog.Default.Name(210350009, language), 1)
            { IconPath = MonsterLootIconCatalog.Default.ItemAsset(210350009) },
        new("synthetic-life-crystal", MonsterLootItemCatalog.Default.Name(510116011, language), 12)
            { IconPath = MonsterLootIconCatalog.Default.ItemAsset(510116011) },
        new("synthetic-seed", MonsterLootItemCatalog.Default.Name(511490001, language), 3)
            { IconPath = MonsterLootIconCatalog.Default.ItemAsset(511490001) }
    ], CompletionScopeKey: "synthetic-character:synthetic-day")
        { KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031),
            SessionElapsed = TimeSpan.FromMinutes(28), SessionKinaPerHour = 184_720 / TimeSpan.FromMinutes(28).TotalHours };

    private Bitmap Boss(AppSettings settings, OverlayThemeDefinition theme)
    {
        var rows = Enumerable.Range(0, Math.Clamp(settings.BossOverlayCount, 1, 12))
            .Select(index => new BossRushEntry(new(0, "preview", BossAt(index)), false,
                TimeSpan.FromMinutes(3 + index * 7), TimeSpan.Zero)).ToArray();
        return Surface(new(BossOverlayForm.PanelWidth, BossOverlayForm.LogicalHeight(rows.Length, 0, true, theme)),
            graphics => BossOverlayForm.PaintPanel(graphics, rows, [], list: true, locked: true,
                settings.LootNameLanguage, boss => Image(boss.Icon), theme));
    }

    private Bitmap Leveling(AppSettings settings, OverlayThemeDefinition theme)
    {
        var targets = Enumerable.Range(0, 6).Select(index => new MapTarget("preview-" + index,
            "preview", 100 + index * 20, 200, UiText.F("Station {0}", index + 1), "NPCs", null,
            RouteId: "preview", Color: index % 2 == 0 ? LevelingRouteStyle.MainQuest : LevelingRouteStyle.Exploration,
            Leveling: true, Completed: index < 2, StopIndex: index)).ToArray();
        var snapshot = new LevelingOverlaySnapshot("preview", UiText.T("Vorschau"), "preview",
            UiText.T("Vorschau"), new(2, 6), targets[2], targets);
        return Surface(new(LevelingOverlayForm.PanelWidth, LevelingOverlayForm.LogicalHeight(targets.Length, theme)),
            graphics => LevelingOverlayForm.PaintPanel(graphics, snapshot, locked: true, theme: theme));
    }

    private AlertDisplayRow[] TimerRows() => EventCatalog.All.Take(4).Select((definition, index) =>
        new AlertDisplayRow("event:catalog:" + definition.Id, UiText.T(definition.Title), "", definition.Color,
            _now.AddMinutes(12 + index * 17), false, IconId: definition.Id)).ToArray();

    private Bitmap Timers(AppSettings settings, OverlayThemeDefinition theme, int? maximumWidth)
    {
        var layout = TimerStripRenderer.Layout(TimerRows(), settings.TimerOverlayGap,
            Math.Max(160, maximumWidth ?? 1000));
        return Surface(layout.Size, graphics => TimerStripRenderer.Paint(graphics, layout, _now,
            settings.TimerOverlayShowSeconds, locked: true, _timerIcons.Load, theme));
    }

    private Bitmap Notifications(AppSettings settings, OverlayThemeDefinition theme)
    {
        var width = Math.Clamp(settings.AlertNotificationsWidth, 240, 1000);
        var rows = TimerRows().Take(2).Select(row => row with { Message = UiText.T("Vorschau") }).ToArray();
        return Surface(new(width, AlertsOverlayForm.LogicalHeight(rows.Length, theme)), graphics =>
            AlertsOverlayForm.PaintNotifications(graphics, rows, _now, locked: true, width, theme));
    }

    private Bitmap Timeline(AppSettings settings, OverlayThemeDefinition theme)
    {
        var events = EventCatalog.All.Select(definition => new EventTimerSnapshot(definition,
            EventWindows.At(definition.Schedule, _now, TimeSpan.FromMinutes(definition.DurationMinutes),
                TimeSpan.FromMinutes(definition.PortalMinutes))!, null, new())).ToArray();
        var boss = new BossRushEntry(new(0, "preview", BossAt(0)), false, TimeSpan.FromMinutes(20), TimeSpan.Zero);
        var snapshot = EventTimelinePresentation.Create(events, [boss], [boss.Place.Key], _now,
            settings.EventTimelineHours, settings.LootNameLanguage);
        var width = Math.Clamp(settings.EventTimelineOverlayWidth, 360, 1600);
        return Surface(new(width, EventTimelineOverlayForm.LogicalHeight(snapshot, theme)), graphics =>
            EventTimelineOverlayForm.PaintPanel(graphics, snapshot, locked: true, width, row => Image(row.Icon), theme));
    }

    private Bitmap Map(AppSettings settings, OverlayThemeDefinition theme)
    {
        var size = new Size(600, 300);
        var placement = new MapPlacement("preview", new MapFix(1, 0, 0, 0, 1, 0, 0, 0, 0, false),
            1, new Rectangle(Point.Empty, size), new Point2d(145, 205));
        MapTarget[] targets = [new("preview-map-1", "preview", 345, 95,
            UiText.T("Vorschau") + " · 240 m", "NPCs", null),
            new("preview-map-2", "preview", 485, 220, UiText.T("Vorschau") + " · 390 m", "NPCs", null,
                After: "preview-map-1")];
        var pets = _catalog.Pets.Take(2).Select((pet, index) => new RouteOverlayForm.PetSymbol(
            220 + index * 180, 175 - index * 65, Image(pet.Icon), pet.DisplayName(settings.LootNameLanguage), false)).ToArray();
        using var overlay = RouteOverlayForm.DrawThemed(theme, placement, targets.Select((target, index) => (target, index)).ToArray(), pets);
        return Surface(size, graphics =>
        {
            graphics.Clear(Color.FromArgb(22, 30, 36));
            using var grid = new Pen(Color.FromArgb(34, 48, 56));
            for (var x = 0; x < size.Width; x += 30) graphics.DrawLine(grid, x, 0, x, size.Height);
            for (var y = 0; y < size.Height; y += 30) graphics.DrawLine(grid, 0, y, size.Width, y);
            graphics.DrawImageUnscaled(overlay, Point.Empty);
            using var player = new SolidBrush(Color.FromArgb(245, 223, 148));
            graphics.FillEllipse(player, 140, 200, 10, 10);
        });
    }

    private Bitmap Merchant(AppSettings settings)
    {
        var size = new Size(684, 332);
        var pets = _catalog.Pets.Take(3).ToArray();
        var badges = Enumerable.Range(0, 3).Select(index =>
        {
            var x = 10 + index * 224;
            var price = new Rectangle(x + 54, 245, 104, 22);
            return new MerchantOverlayForm.Badge(new(x, price.Top, 212, 77), index == 2 ? "MAX" : "17/25",
                true, index == 2, "Altgard", price, pets.ElementAtOrDefault(index)?.Genus ?? "?",
                "Altgard", new Rectangle(x + 58, 17, 96, 20));
        }).ToArray();
        return Surface(size, graphics =>
        {
            graphics.Clear(Color.FromArgb(22, 27, 31));
            using var material = new SolidBrush(Color.FromArgb(44, 44, 41));
            using var edge = new Pen(Color.FromArgb(105, 98, 78));
            using var ink = new SolidBrush(Color.FromArgb(225, 220, 206));
            using var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            for (var index = 0; index < 3; index++)
            {
                var x = 10 + index * 224;
                graphics.FillRectangle(material, x, 10, 212, 258);
                graphics.DrawRectangle(edge, x, 10, 212, 258);
                var pet = pets.ElementAtOrDefault(index);
                if (Image(pet?.Icon) is { } portrait) graphics.DrawImage(portrait, new Rectangle(x + 64, 83, 84, 84));
                graphics.DrawString(pet?.DisplayName(settings.LootNameLanguage) ?? UiText.T("Vorschau"),
                    font, ink, new RectangleF(x + 8, 202, 196, 38), center);
            }
            MerchantOverlayForm.DrawBadges(graphics, Point.Empty, badges);
        });
    }

    private BossPlace BossAt(int index) => _bosses.Count > 0 ? _bosses[index % _bosses.Count]
        : new(index, 0, UiText.T("Vorschau") + " " + (index + 1), null, null, null);

    private IReadOnlyList<BossPlace> LoadBosses()
    {
        if (_mapDataDirectory is null) return [];
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_mapDataDirectory))
            {
                var path = Path.Combine(directory, "bosses.json");
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("bosses", out var array))
                {
                    var bosses = array.Deserialize<BossPlace[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (bosses is { Length: > 0 }) return bosses;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private Bitmap? Image(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _mapDataDirectory is null) return null;
        var path = Path.Combine(_mapDataDirectory, relativePath);
        if (_images.TryGetValue(path, out var cached)) return cached;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var image = System.Drawing.Image.FromStream(stream);
            return _images[path] = new Bitmap(image);
        }
        catch (Exception error) when (error is IOException or ArgumentException or ExternalException or OutOfMemoryException) { return null; }
    }

    private static Bitmap Surface(Size size, Action<Graphics> paint)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            paint(graphics);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Bitmap FitBound(Bitmap image, int? maximumWidth)
    {
        if (maximumWidth is not > 0 || image.Width <= maximumWidth.Value) return image;
        using (image)
        {
            var size = new Size(maximumWidth.Value, Math.Max(1, (int)Math.Round(image.Height * maximumWidth.Value / (double)image.Width)));
            return Surface(size, graphics =>
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(Point.Empty, size));
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timerIcons.Dispose();
        _daevanion.Dispose();
        _lootIcons.Dispose();
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}
