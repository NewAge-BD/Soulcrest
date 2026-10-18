using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;

internal static class Program
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] Scales = [.6, 1, 1.25, 2];
    private static readonly int[] Dpis = [96, 144, 192];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Scenario(string Name, IReadOnlyList<AlertDisplayRow> Rows,
        bool Seconds = true, bool Locked = true, int Gap = 18, int MaximumWidth = int.MaxValue);
    private sealed record BossSample(string Key, string IconId, int Width, int Height);
    private sealed record DpiCheck(int Dpi, string PixelSha256);
    private sealed record CellReport(string Key, Rectangle Bounds, Rectangle IconBounds, Rectangle TextBounds, Rectangle? LabelBounds);
    private sealed record FrameReport(string File, string Language, string Scenario, double Scale,
        int LogicalWidth, int LogicalHeight, int PhysicalWidth, int PhysicalHeight,
        int Gap, bool Seconds, bool Locked, IReadOnlyList<CellReport> Cells,
        IReadOnlyList<DpiCheck> DpiChecks, bool DpiPixelsIdentical, string? Composite, string? Crop);

    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: TimerPreview <local-reference-image> <output-directory> [local-mapdata-directory]");
            return 2;
        }
        var referencePath = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        var mapData = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
        if (!File.Exists(referencePath))
        {
            Console.Error.WriteLine("The supplied local reference image does not exist.");
            return 2;
        }

        var previousLanguage = UiText.Language;
        try
        {
            Directory.CreateDirectory(output);
            using var source = Image.FromFile(referencePath);
            using var reference = new Bitmap(source);
            var bosses = BossSamples(mapData);
            using var renderer = new ProductionRenderer(mapData);
            var reports = new List<FrameReport>();
            var notificationPreviews = new List<string>();
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                foreach (var scenario in Scenarios(language, bosses))
                {
                    foreach (var scale in Scales)
                        reports.Add(WriteFrame(renderer, scenario, language, scale, reference, output));
                }
                foreach (var scale in Scales) WriteContact(reports, language, scale, output);
                if (language == "de") WriteLockContact(renderer, output);
                notificationPreviews.AddRange(WriteExpiryNotifications(language, output));
            }
            var passed = reports.All(report => report.DpiPixelsIdentical);
            File.WriteAllText(Path.Combine(output, "timer-preview.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                SyntheticRows = true,
                ReferenceContainsOnlyLocalPixels = true,
                ApplicationCreated = false,
                ServicesCreated = false,
                UserDataRead = false,
                ReferenceSize = new { reference.Width, reference.Height },
                CompositePosition = new { X = 104, Y = 36 },
                Note = "Illustrative timers at a fixed UTC instant. Only the supplied reference may contain real game pixels. " +
                    "Images and reports are local review artifacts; no paths, character names or service state are embedded in this report.",
                Now,
                Assets = AssetReports(),
                BossPortraits = bosses,
                DpiPixelsIdentical = passed,
                NotificationPreviews = notificationPreviews,
                Frames = reports
            }, JsonOptions));
            Console.WriteLine($"Wrote {reports.Count} native renderer variants, {notificationPreviews.Count} notification previews and 8 contact sheets. DPI pixels identical: {passed}.");
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner }
                ? inner.Message : error.Message);
            return 1;
        }
        finally { UiText.Language = previousLanguage; }
    }

    private static IReadOnlyList<string> WriteExpiryNotifications(string language, string output)
    {
        var type = typeof(AlertDisplayRow).Assembly.GetType("Soulcrest.App.Overlay.AlertsOverlayForm", throwOnError: true)!;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var paint = type.GetMethod("PaintNotifications", flags)!;
        var heightMethod = type.GetMethod("LogicalHeight", flags)!;
        var de = language == "de";
        AlertDisplayRow[] before =
        [
            new("boss:fixture:upcoming", de ? "Prüf-Boss A" : "Synthetic boss A", "", "#ef4444", Now.AddMinutes(1), false),
            new("boss:fixture:ending", de ? "Prüf-Boss B" : "Synthetic boss B", "", "#ef4444", Now.AddMilliseconds(100), false),
            new("boss:fixture:confirmed", de ? "Prüf-Boss C" : "Synthetic boss C", de ? "Bestätigter Spawn" : "Confirmed spawn",
                "#5eead4", Now, true)
        ];
        // Explicit layout snapshots only; runtime tests verify that the source removes the expired reminder.
        var snapshots = new[]
        {
            (Name: "before", Rows: before, At: Now),
            (Name: "after", Rows: new[] { before[0], before[2] }, At: Now.AddSeconds(1))
        };
        List<string> files = [];
        foreach (var snapshot in snapshots)
        foreach (var width in new[] { 240, 400 })
        foreach (var scale in Scales)
        {
            var height = (int)heightMethod.Invoke(null, [snapshot.Rows.Length])!;
            var file = $"notifications-expiry-{snapshot.Name}-{language}-width-{width}-scale-{ScaleName(scale)}.png";
            using var bitmap = new Bitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform((float)scale, (float)scale);
                paint.Invoke(null, [graphics, snapshot.Rows, snapshot.At, true, width]);
            }
            bitmap.Save(Path.Combine(output, file), ImageFormat.Png);
            files.Add(file);
        }
        return files;
    }

    private static IEnumerable<Scenario> Scenarios(string language, IReadOnlyList<BossSample> bosses)
    {
        var de = language == "de";
        AlertDisplayRow Row(string key, string title, int seconds, string color = "#dddcd7",
            bool started = false, bool provisional = false, string? label = null) =>
            new(key, title, "", color, Now.AddSeconds(seconds), started, provisional, label);
        AlertDisplayRow Boss(int index, int seconds, bool started = false, bool provisional = false) =>
            Row(index < bosses.Count ? bosses[index].Key : $"boss:preview-{index}", $"Boss {(char)('A' + index)}",
                seconds, "#ef4444", started, provisional) with { IconId = index < bosses.Count ? bosses[index].IconId : "boss" };

        AlertDisplayRow[] events =
        [
            Row("event:catalog:shugo", "Event A", 24 * 60 + 59, "#fbbf24"),
            Row("event:catalog:rift", "Event B", 2 * 3600 + 5 * 60 + 7, "#a78bfa"),
            Row("event:catalog:invasion", "Event C", 8 * 60 + 9, "#fb923c"),
            Row("event:catalog:abyss", "Event D", 6 * 3600 + 30 * 60, "#ef4444")
        ];
        yield return new("events", events);
        yield return new("active", events.Select((row, index) => index switch
        {
            // Festival has only a start, never an invented active duration.
            1 => row with { Started = true, Title = de ? "Spacetime Rift offen" : "Spacetime Rift Open",
                At = Now.AddMinutes(2), CountdownLabel = de ? "Portal schließt in" : "Portal closes in",
                InlineLabel = de ? "Spacetime Rift offen" : "Spacetime Rift Open" },
            2 => row with { Started = true, At = Now.AddMinutes(7), CountdownLabel = de ? "Endet in" : "Ends in" },
            3 => row with { Started = true, At = Now.AddMinutes(15), CountdownLabel = de ? "Endet in" : "Ends in" },
            _ => row
        }).ToArray());
        var rift = EventCatalog.Find("rift")!;
        foreach (var minute in new[] { 5, 10 })
        {
            var instant = Now.AddMinutes(minute);
            var window = EventWindows.At(rift.Schedule, instant, TimeSpan.FromMinutes(rift.DurationMinutes), TimeSpan.FromMinutes(rift.PortalMinutes))!;
            var display = EventTimerDisplay.From(new(rift, window, null, new()));
            var row = new AlertDisplayRow("event:catalog:rift", display.Title, "", rift.Color,
                Now + (display.CountdownAt - instant), display.IsActive, CountdownLabel: display.CountdownLabel,
                IconId: "rift", InlineLabel: display.IsActive ? display.Title : null);
            yield return new(minute == 5 ? "rift-open" : "rift-next", events.Select(original => original.Key == row.Key ? row : original).ToArray());
        }
        yield return new("provisional", [
            Boss(0, 180, provisional: true),
            Boss(1, -1, provisional: true),
            events[0], events[1] with { Provisional = true }
        ]);
        yield return new("boss-reset-custom", [
            Boss(0, 3661),
            Boss(1, -1, started: true),
            Row("event:catalog:daily-reset", de ? "Täglicher Reset" : "Daily reset", 10 * 3600 + 1),
            Row("event:catalog:weekly-reset", de ? "Wöchentlicher Reset" : "Weekly reset", 2 * 86400 + 12345),
            Row("event:preview-custom", de ? "Eigener Timer" : "Custom timer", 93),
            Row("event:preview-long", de ? "Sehr langer synthetischer Timertitel" : "Very long synthetic timer title", 8 * 86400 + 3661)
        ]);
        yield return new("minutes", events, Seconds: false);
        yield return new("boss-portraits", [Boss(0, 1234), Boss(1, 2345), Boss(2, 3456),
            Row("event:catalog:daily-reset", "Reset A", 10 * 3600 + 1),
            Row("event:catalog:weekly-reset", "Reset B", 2 * 86400 + 12345),
            Row("event:preview-custom", "Custom A", 93)]);
        yield return new("unlocked", events, Locked: false);
        yield return new("tight", events, Gap: 4);
        yield return new("wide", events, Gap: 48);
        yield return new("wrapped", events, MaximumWidth: 300);
    }

    private static FrameReport WriteFrame(ProductionRenderer renderer, Scenario scenario, string language,
        double scale, Bitmap reference, string output)
    {
        var availableWidth = Math.Max(1, (int)Math.Floor(Math.Max(1, reference.Width - 104) / scale));
        var layout = renderer.Layout(scenario.Rows, scenario.Gap, Math.Min(scenario.MaximumWidth, availableWidth));
        var logical = renderer.Size(layout);
        if (logical.Width <= 0 || logical.Height <= 0) throw new InvalidDataException("The renderer returned an empty layout.");
        var cells = renderer.Cells(layout);
        foreach (var cell in cells)
        {
            var bounds = new Rectangle(Point.Empty, logical);
            if (!bounds.Contains(cell.Bounds) || !cell.Bounds.Contains(cell.IconBounds) || !cell.Bounds.Contains(cell.TextBounds)
                || cell.LabelBounds is { } label && !cell.Bounds.Contains(label))
                throw new InvalidDataException("A timer cell or its contents leave the production layout.");
        }

        var fileStem = $"{scenario.Name}-{language}-scale-{ScaleName(scale)}";
        var checks = new List<DpiCheck>();
        using var native = Render(renderer, layout, scenario, logical, scale, 96);
        checks.Add(new(96, PixelHash(native)));
        native.Save(Path.Combine(output, fileStem + ".png"), ImageFormat.Png);
        foreach (var dpi in Dpis.Skip(1))
        {
            using var compared = Render(renderer, layout, scenario, logical, scale, dpi);
            var hash = PixelHash(compared);
            checks.Add(new(dpi, hash));
            if (hash != checks[0].PixelSha256)
                compared.Save(Path.Combine(output, fileStem + $"-dpi-{dpi}.png"), ImageFormat.Png);
        }
        var identical = checks.All(check => check.PixelSha256 == checks[0].PixelSha256);
        string? compositeName = null, cropName = null;
        if (scale == 1 || scenario.Name == "events")
        {
            compositeName = fileStem + "-reference.png";
            cropName = fileStem + "-reference-crop.png";
            using var composite = new Bitmap(reference);
            using (var graphics = Graphics.FromImage(composite)) graphics.DrawImageUnscaled(native, 104, 36);
            composite.Save(Path.Combine(output, compositeName), ImageFormat.Png);
            var crop = Rectangle.Intersect(new Rectangle(0, 0, native.Width + 128, Math.Max(96, native.Height + 52)),
                new Rectangle(Point.Empty, composite.Size));
            if (crop.Width > 0 && crop.Height > 0)
            {
                using var detail = composite.Clone(crop, PixelFormat.Format32bppArgb);
                detail.Save(Path.Combine(output, cropName), ImageFormat.Png);
            }
            else cropName = null;
        }
        return new(fileStem + ".png", language, scenario.Name, scale, logical.Width, logical.Height,
            native.Width, native.Height, scenario.Gap, scenario.Seconds, scenario.Locked,
            cells, checks, identical, compositeName, cropName);
    }

    private static Bitmap Render(ProductionRenderer renderer, object layout, Scenario scenario, Size logical,
        double scale, int dpi)
    {
        var stripWidth = Math.Max(1, (int)Math.Ceiling(logical.Width * scale));
        var lockSize = Math.Max(10, (int)Math.Round(18 * scale));
        var lockInset = Math.Max(1, (int)Math.Round(3 * scale));
        var lockBounds = new Rectangle(stripWidth + lockInset, 0, lockSize, lockSize);
        var bitmap = new Bitmap(lockBounds.Right,
            Math.Max(lockSize, (int)Math.Ceiling(logical.Height * scale)), PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)scale, (float)scale);
            renderer.Paint(graphics, layout, Now, scenario.Seconds, scenario.Locked);
            graphics.ResetTransform();
            renderer.PaintLock(graphics, lockBounds, scenario.Locked);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void WriteContact(IReadOnlyList<FrameReport> reports, string language, double scale, string output)
    {
        var frames = reports.Where(report => report.Language == language && report.Scale == scale).ToArray();
        var width = Math.Max(400, frames.Max(frame => frame.PhysicalWidth) + 24);
        var height = frames.Sum(frame => frame.PhysicalHeight + 42) + 12;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(22, 26, 33));
        using var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(206, 214, 226));
        var top = 10;
        foreach (var frame in frames)
        {
            graphics.DrawString($"{frame.Scenario} · {language.ToUpperInvariant()} · {scale:0.##}x · synthetic timers", font, ink, 12, top);
            using var strip = Image.FromFile(Path.Combine(output, frame.File));
            graphics.DrawImageUnscaled(strip, 12, top + 23);
            top += frame.PhysicalHeight + 42;
        }
        bitmap.Save(Path.Combine(output, $"contact-{language}-scale-{ScaleName(scale)}.png"), ImageFormat.Png);
    }

    private static void WriteLockContact(ProductionRenderer renderer, string output)
    {
        using var bitmap = new Bitmap(640, 264, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(35, 42, 45));
        using var font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(226, 224, 212));
        var states = new[] { ("locked", true, false, false), ("unlocked", false, false, false),
            ("hover", true, true, false), ("pressed", false, true, true) };
        for (var row = 0; row < Scales.Length; row++)
        {
            var scale = Scales[row];
            for (var column = 0; column < states.Length; column++)
            {
                var (name, locked, hover, pressed) = states[column];
                var size = Math.Max(10, (int)Math.Round(18 * scale));
                var bounds = new Rectangle(column * 160 + 18, row * 66 + 26, size, size);
                graphics.DrawString($"{name} · {scale:0.##}x", font, ink, column * 160 + 12, row * 66 + 5);
                renderer.PaintLock(graphics, bounds, locked, hover, pressed);
            }
        }
        bitmap.Save(Path.Combine(output, "lock-states.png"), ImageFormat.Png);
    }

    private static object[] AssetReports() => Directory.GetFiles(
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "events"), "*.png")
        .Order(StringComparer.Ordinal).Select(path =>
    {
        using var image = Image.FromFile(path);
        return (object)new { File = Path.GetFileName(path), image.Width, image.Height,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
    }).ToArray();

    private static IReadOnlyList<BossSample> BossSamples(string? mapData)
    {
        if (mapData is null) return [];
        var root = Path.GetFullPath(mapData);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string? WithinRoot(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        var manifestPath = WithinRoot("manifest.json")!;
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The optional mapdata directory needs its local manifest.json.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var samples = new List<BossSample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in manifest.RootElement.GetProperty("maps").EnumerateArray())
        {
            var id = map.GetProperty("id").GetString();
            if (id is null || WithinRoot(id + "/bosses.json") is not { } catalogPath || !File.Exists(catalogPath)) continue;
            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!catalog.RootElement.TryGetProperty("bosses", out var entries)) continue;
            var mapId = catalog.RootElement.GetProperty("mapId").GetInt32();
            foreach (var boss in entries.EnumerateArray())
            {
                if (!boss.TryGetProperty("icon", out var iconProperty) || iconProperty.GetString() is not { Length: > 0 } icon ||
                    !seen.Add(icon) || WithinRoot(icon) is not { } iconPath || !File.Exists(iconPath)) continue;
                using var image = Image.FromFile(iconPath);
                var spawnId = boss.GetProperty("spawnId").GetInt32();
                samples.Add(new($"boss:{mapId}:{spawnId}", "map:" + icon.Replace('\\', '/'), image.Width, image.Height));
                if (samples.Count == 2) return samples;
            }
        }
        return samples;
    }

    private static string PixelHash(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            for (var row = 0; row < bitmap.Height; row++)
                Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes, row * bitmap.Width * 4, bitmap.Width * 4);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static string ScaleName(double scale) => scale.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p');

    private sealed class ProductionRenderer : IDisposable
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly MethodInfo _layout;
        private readonly MethodInfo _paint;
        private readonly MethodInfo _lockPaint;
        private readonly IDisposable _cache;
        private readonly Func<string, Image?> _icons;

        public ProductionRenderer(string? mapData)
        {
            var assembly = typeof(AlertDisplayRow).Assembly;
            var type = assembly.GetType("Soulcrest.App.Overlay.TimerStripRenderer", throwOnError: true)!;
            _layout = type.GetMethod("Layout", Static) ?? throw new MissingMethodException(type.FullName, "Layout");
            _paint = type.GetMethod("Paint", Static) ?? throw new MissingMethodException(type.FullName, "Paint");
            var lockType = assembly.GetType("Soulcrest.App.Overlay.TimerStripLockRenderer", throwOnError: true)!;
            _lockPaint = lockType.GetMethod("Paint", Static) ?? throw new MissingMethodException(lockType.FullName, "Paint");
            var cacheType = assembly.GetType("Soulcrest.App.Overlay.TimerIconCache", throwOnError: true)!;
            var cache = Activator.CreateInstance(cacheType, Instance, binder: null, args: [mapData], culture: CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("No production timer icon cache returned.");
            _cache = (IDisposable)cache;
            var load = cacheType.GetMethod("Load", Instance) ?? throw new MissingMethodException(cacheType.FullName, "Load");
            _icons = id => (Image?)load.Invoke(cache, [id]);
        }

        public object Layout(IReadOnlyList<AlertDisplayRow> rows, int gap, int maximumWidth)
        {
            var arguments = _layout.GetParameters().Length == 3
                ? new object?[] { rows, gap, maximumWidth } : [rows, gap];
            return _layout.Invoke(null, arguments) ?? throw new InvalidOperationException("No production timer layout returned.");
        }
        public void Paint(Graphics graphics, object layout, DateTimeOffset now, bool seconds, bool locked) =>
            _paint.Invoke(null, [graphics, layout, now, seconds, locked, _icons]);
        public void PaintLock(Graphics graphics, Rectangle bounds, bool locked, bool hover = false, bool pressed = false) =>
            _lockPaint.Invoke(null, [graphics, bounds, locked, hover, pressed]);
        public void Dispose() => _cache.Dispose();
        public Size Size(object layout) => (Size)Read(layout, "Size");
        public IReadOnlyList<CellReport> Cells(object layout) => ((IEnumerable)Read(layout, "Cells")).Cast<object>()
            .Select(cell => new CellReport(((AlertDisplayRow)Read(cell, "Row")).Key,
                (Rectangle)Read(cell, "Bounds"), (Rectangle)Read(cell, "IconBounds"), (Rectangle)Read(cell, "TextBounds"),
                (Rectangle?)cell.GetType().GetProperty("LabelBounds", Instance)?.GetValue(cell))).ToArray();
        private static object Read(object value, string property) =>
            value.GetType().GetProperty(property, Instance)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, property);
    }
}
