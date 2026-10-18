using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.Shop;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

// Offline only: loading the application assembly does not start its entry point. No Form, capture,
// service, user-data store or network source is instantiated. Both layout and pixels come from the
// production methods, rather than an approximate drawing implementation maintained by this tool.
internal static class Program
{
    private static readonly MethodInfo BuildBadges = ProductionMethod("BuildBadges");
    private static readonly MethodInfo DrawBadges = ProductionMethod("DrawBadges");
    private static readonly MethodInfo GetSurfaceBounds = ProductionMethod("SurfaceBounds");
    private static readonly MethodInfo PetCategory = ServiceMethod("PetCategory");
    private static readonly MethodInfo FarmSpawnText = ServiceMethod("FarmSpawnText");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Reference[] References =
    [
        new("smaller", "codex-clipboard-f2cda31f-f11f-45f7-9f4c-ebdbf906d260.png", .9375, false),
        new("small", "codex-clipboard-6540f32e-2778-441a-a04e-96439e7d5b5c.png", 1, false),
        new("medium", "codex-clipboard-c6b3db97-5fc9-4194-8cc0-231835712789.png", 1.0625, false),
        new("large", "codex-clipboard-87469ee7-d6f9-48c2-b77f-54ca57cd7f18.png", 1.125, true),
        new("larger", "codex-clipboard-bae4f049-e36e-4951-bfd8-199fc14ac52b.png", 1.25, true),
    ];

    private sealed record Reference(string Name, string FileName, double UiScale, bool MixedTab);
    private sealed record FarmContext(PetCatalog Catalog, IReadOnlyList<MapInfo> Maps, string Version);
    private sealed record DrawnSurface(Bitmap Bitmap, Rectangle Area, string PixelHash, int InkPixels) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: MerchantPreview <reference-image-directory> <output-directory>");
            return 2;
        }
        try
        {
            var input = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[1]);
            var farm = LoadFarmContext();
            Directory.CreateDirectory(output);
            var reports = new List<object>();
            foreach (var reference in References)
            {
                using var image = Cv2.ImRead(Path.Combine(input, reference.FileName), ImreadModes.Color);
                if (image.Empty()) throw new InvalidDataException($"Missing reference: {reference.FileName}");
                ProcessFrame(reference, image, "native", image.Width, reference.UiScale, output, reports, farm);
                ProcessFrame(reference, image, "capture-analysis1920", 1920, reference.UiScale, output, reports, farm);
                using var resized = new Mat();
                Cv2.Resize(image, resized, new OpenCvSharp.Size(1920, 1080), 0, 0, InterpolationFlags.Area);
                ProcessFrame(reference, resized, "1920x1080", 1920,
                    reference.UiScale * 1920 / image.Width, output, reports, farm);
                using var reduced = new Mat();
                Cv2.Resize(image, reduced, new OpenCvSharp.Size(1280, 720), 0, 0, InterpolationFlags.Area);
                ProcessFrame(reference, reduced, "1280x720", 1280,
                    reference.UiScale * 1280 / image.Width, output, reports, farm);
            }
            var manifest = new
            {
                SchemaVersion = 3,
                Layout = "Category centered above the pet name; counter centered below its price. Header and footer are separate transparent overlay regions.",
                SyntheticCounters = true,
                NativeFarmRegionsFromEmbeddedCatalog = true,
                NativePetCategoriesFromPetIdTable = true,
                NativeFarmSpawnCountsFromLocalMapPackage = true,
                MapPackageVersion = farm.Version,
                SpawnCountMeaning = "Known source-monster placements, not clustered pet symbols or a live population.",
                Note = "Counter values are illustrative. Files named synthetic-stress replace farm regions, spawn counts and " +
                    "categories to test line width; synthetic-priority replaces farm regions and spawn counts to test emphasis. " +
                    "These synthetic values do not assert sources or categories for the pictured pets.",
                ApplicationCreated = false,
                LiveGameOrUserDataAccessed = false,
                Variants = reports,
            };
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            Console.WriteLine($"Generated {reports.Count} offline variants in {output}");
            return 0;
        }
        catch (Exception error)
        {
            var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            Console.Error.WriteLine($"Offline preview failed: {actual.GetType().Name}: {actual.Message}");
            return 1;
        }
    }

    private static void ProcessFrame(Reference reference, Mat image, string frameName, int maximumAnalysisWidth,
        double expectedUiScale, string output, List<object> reports, FarmContext farm)
    {
        var analysisScale = Math.Min(1.0, (double)maximumAnalysisWidth / image.Width);
        using var analysis = new Mat();
        if (analysisScale == 1) image.CopyTo(analysis);
        else Cv2.Resize(image, analysis, new OpenCvSharp.Size(), analysisScale, analysisScale, InterpolationFlags.Area);
        var found = MerchantCardLocator.Find(analysis);
        if (found.Count != 10) throw new InvalidDataException($"{reference.Name}/{frameName}: found {found.Count}/10 anchors.");
        var anchors = found.Select(anchor => anchor with
        {
            PriceBounds = new Rect((int)Math.Round(anchor.PriceBounds.X / analysisScale),
                (int)Math.Round(anchor.PriceBounds.Y / analysisScale),
                (int)Math.Round(anchor.PriceBounds.Width / analysisScale),
                (int)Math.Round(anchor.PriceBounds.Height / analysisScale)),
            Scale = anchor.Scale / analysisScale,
        }).ToArray();
        var meanScale = anchors.Average(anchor => anchor.Scale);
        if (Math.Abs(meanScale / expectedUiScale - 1) > .05)
            throw new InvalidDataException($"{reference.Name}/{frameName}: unexpected UI scale {meanScale:F4}.");

        var rows = ExampleRows(reference.MixedTab, farm);
        var categoryOffset = frameName switch { "capture-analysis1920" => 1, "1920x1080" => 2, "1280x720" => 3, _ => 0 };
        string[] categories = ["Cogni", "Fera", "Natura", "Varian"];
        WritePreview(reference, image, frameName, anchors, rows, null, analysisScale, expectedUiScale, output, reports, farm);
        var stress = rows.Select((row, index) => row with
        {
            Counter = (index % 3) switch { 0 => "max", 1 => "?", _ => "74/75" },
            IsFresh = index % 3 == 0,
            IsMax = index % 3 == 0,
            FarmRegions = "Abyss / Altgard / Verteron",
            PetCategory = categories[(index + categoryOffset) % categories.Length],
            FarmSpawns = "Abyss (209) / Altgard (398) / Verteron (377)",
        }).ToArray();
        WritePreview(reference, image, frameName, anchors, stress, "synthetic-stress", analysisScale, expectedUiScale, output, reports, farm);
        if (frameName == "native")
        {
            var priorities = rows.Take(3).Select((row, index) => row with
            {
                Counter = index switch { 0 => "0/5", 1 => "?", _ => "max" },
                IsFresh = index != 1,
                IsMax = index == 2,
                FarmRegions = index == 0 ? "Altgard" : "Special",
                FarmSpawns = index == 0 ? "Altgard (12)" : "Special",
            }).ToArray();
            WritePreview(reference, image, frameName, anchors, priorities, "synthetic-priority", analysisScale,
                expectedUiScale, output, reports, farm);
        }
    }

    private static MerchantCounterRow[] ExampleRows(bool mixedTab, FarmContext farm) => mixedTab
        ? [Example(farm, 7, 1012, "74/75"), Example(farm, 8, 1062, "?", fresh: false), Example(farm, 9, 1097, "max", max: true)]
        : [Example(farm, 0, 1009, "0/5"), Example(farm, 1, 1013, "24/25"), Example(farm, 2, 1045, "74/75"),
            Example(farm, 3, 1052, "max", max: true), Example(farm, 4, 1057, "?", fresh: false),
            Example(farm, 5, 1094, "3/25", fresh: false), Example(farm, 6, 1146, "max", fresh: false, max: true),
            Example(farm, 7, 1156, "0/75"), Example(farm, 8, 1191, "4/5"), Example(farm, 9, 1194, "12/25")];

    private static MerchantCounterRow Example(FarmContext farm, int slot, int petNumber, string counter, bool fresh = true, bool max = false)
    {
        var pet = PetIdTable.Find(petNumber) ?? throw new InvalidDataException($"Missing example pet {petNumber}.");
        var regions = PetFarmRegions.Find(petNumber) ?? throw new InvalidDataException($"Missing farm regions {petNumber}.");
        var category = PetCategory.Invoke(null, [petNumber]) as string
            ?? throw new InvalidDataException($"Missing category result for {petNumber}.");
        var spawns = FarmSpawnText.Invoke(null, [petNumber, farm.Catalog, farm.Maps]) as string
            ?? throw new InvalidDataException($"Missing spawn-count result for {petNumber}.");
        return new(slot, pet.CatalogId ?? $"preview-pet-{petNumber}", counter, fresh, max,
            string.Join(" / ", regions), category, spawns);
    }

    private static void WritePreview(Reference reference, Mat image, string frameName,
        IReadOnlyList<MerchantCardAnchor> anchors, IReadOnlyList<MerchantCounterRow> rows, string? syntheticLabel,
        double analysisScale, double expectedUiScale, string output, List<object> reports, FarmContext farm)
    {
        var size = new Size(image.Width, image.Height);
        var badges = BuildBadges.Invoke(null, [size, anchors, rows])
            ?? throw new InvalidDataException("Production layout returned null.");
        var badgeValues = ((IEnumerable)badges).Cast<object>().ToArray();
        if (badgeValues.Length != rows.Count) throw new InvalidDataException("Production layout rejected example rows.");
        var bounds = badgeValues.Select(BadgeBounds).ToArray();
        var categoryBounds = badgeValues.Select(badge => BadgeRectangle(badge, "CategoryBounds")).ToArray();
        var surfaceBounds = badgeValues.Select(BadgeSurfaceBounds).ToArray();
        if (surfaceBounds.Any(rectangle => !new Rectangle(Point.Empty, size).Contains(rectangle)))
            throw new InvalidDataException("An example category header or footer falls outside the image.");
        var area = surfaceBounds.Aggregate(Rectangle.Union);
        using var surface = RenderSurface(badges, area, 96);
        // Pixel fonts and physical anchor coordinates must not receive a second DPI scale.
        foreach (var dpi in new[] { 144f, 192f })
        {
            using var dpiSurface = RenderSurface(badges, area, dpi);
            if (surface.PixelHash != dpiSurface.PixelHash)
                throw new InvalidDataException($"{reference.Name}/{frameName}: renderer changed pixels at {dpi} DPI.");
        }
        using var overlay = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(overlay))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(surface.Bitmap, area.Location);
        }
        using var composite = ToBitmap(image);
        using (var graphics = Graphics.FromImage(composite))
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.DrawImageUnscaled(overlay, Point.Empty);
        }
        var name = $"{reference.Name}-{frameName}{(syntheticLabel is null ? "" : "-" + syntheticLabel)}";
        composite.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        overlay.Save(Path.Combine(output, name + "-overlay.png"), ImageFormat.Png);
        var report = new
        {
            Name = name,
            Reference = reference.FileName,
            Width = size.Width,
            Height = size.Height,
            AnalysisScale = analysisScale,
            ExpectedUiScale = expectedUiScale,
            DetectedUiScale = anchors.Average(anchor => anchor.Scale),
            SyntheticCounters = true,
            SyntheticFarmRegions = syntheticLabel is not null,
            SyntheticFarmSpawns = syntheticLabel is not null,
            SyntheticPetCategories = syntheticLabel == "synthetic-stress",
            MapPackageVersion = farm.Version,
            Anchors = anchors.Select(anchor => new
            {
                anchor.SlotIndex, anchor.Scale, anchor.Confidence,
                Price = BoundsData(new Rectangle(anchor.PriceBounds.X, anchor.PriceBounds.Y,
                    anchor.PriceBounds.Width, anchor.PriceBounds.Height)),
            }).ToArray(),
            Footers = rows.Select((row, index) => new
            {
                row.SlotIndex, row.PetId, row.Counter, row.IsFresh, row.IsMax, row.FarmRegions,
                row.PetCategory, row.FarmSpawns,
                Bounds = BoundsData(bounds[index]),
                CategoryBounds = BoundsData(categoryBounds[index]),
                SurfaceBounds = BoundsData(surfaceBounds[index]),
            }).ToArray(),
            Coverage = new
            {
                VerifiedSlots = anchors.Count,
                PetRows = rows.Count,
                CategoryHeaders = categoryBounds.Count(rectangle => !rectangle.IsEmpty),
                Normal = rows.Any(row => row.Counter != "?" && !row.IsMax),
                Max = rows.Any(row => row.IsMax),
                Unknown = rows.Any(row => row.Counter == "?"),
                Saved = rows.Any(row => !row.IsFresh && row.Counter != "?"),
                MultipleRegions = rows.Any(row => row.FarmRegions.Contains(" / ", StringComparison.Ordinal)),
                ThreeRegions = rows.Any(row => row.FarmRegions == "Abyss / Altgard / Verteron"),
                PetCategories = rows.Select(row => row.PetCategory).Distinct().Order().ToArray(),
                ThreeDigitSpawnCounts = rows.Any(row => Regex.IsMatch(row.FarmSpawns, @"\([0-9]{3,}\)")),
                UnlockPriority = rows.Any(row => row.Counter == "0/5"),
                SpecialPriority = rows.Any(row => row.FarmRegions == "Special"),
                surface.InkPixels,
                DpiChecks = new[] { 96, 144, 192 },
                DpiPixelsIdentical = true,
            },
        };
        File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(report, JsonOptions));
        reports.Add(report);
        Console.WriteLine($"{name}: {anchors.Count} anchors, {rows.Count} footers, UI scale {anchors[0].Scale:F4}, DPI 96/144/192 identical");
    }

    private static DrawnSurface RenderSurface(object badges, Rectangle area, float dpi)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        try
        {
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                DrawBadges.Invoke(null, [graphics, area.Location, badges]);
            }
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            byte[] pixels;
            try
            {
                pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally { bitmap.UnlockBits(data); }
            var inkPixels = 0;
            for (var offset = 3; offset < pixels.Length; offset += 4)
                if (pixels[offset] != 0) inkPixels++;
            return new(bitmap, area, Convert.ToHexString(SHA256.HashData(pixels)), inkPixels);
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Rectangle BadgeBounds(object badge) => BadgeRectangle(badge, "Bounds");

    private static Rectangle BadgeRectangle(object badge, string name) =>
        badge.GetType().GetProperty(name)?.GetValue(badge) is Rectangle rectangle ? rectangle
            : throw new InvalidDataException($"Production badge has no {name} property.");

    private static Rectangle BadgeSurfaceBounds(object badge) =>
        GetSurfaceBounds.Invoke(null, [badge]) is Rectangle rectangle ? rectangle
            : throw new InvalidDataException("Production badge has no surface bounds.");

    private static object BoundsData(Rectangle rectangle) =>
        new { rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, rectangle.Right, rectangle.Bottom };

    private static Bitmap ToBitmap(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    private static FarmContext LoadFarmContext()
    {
        var root = FindRepository(Environment.CurrentDirectory) ?? FindRepository(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Run this tool from the Soulcrest repository or its build directory.");
        var data = Path.Combine(root, "imports", "generated", "mapdata");
        var petsPath = Path.Combine(data, "pets.json");
        var manifestPath = Path.Combine(data, "manifest.json");
        if (!File.Exists(petsPath) || !File.Exists(manifestPath))
            throw new FileNotFoundException("Offline previews require imports/generated/mapdata/pets.json and manifest.json; no substitute data is generated.");
        var catalog = PetCatalog.Load(petsPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var maps = manifest.RootElement.GetProperty("maps").EnumerateArray().Select(map =>
            new MapInfo(map.GetProperty("id").GetString() ?? throw new InvalidDataException("Map ID is missing."),
                map.GetProperty("label").GetString() ?? throw new InvalidDataException("Map label is missing."),
                map.GetProperty("group").GetString() ?? throw new InvalidDataException("Map group is missing."))).ToArray();
        if (catalog.Pets.Count == 0 || maps.Length == 0)
            throw new InvalidDataException("The local map package must contain pets and maps.");
        var version = manifest.RootElement.TryGetProperty("version", out var value) ? value.GetString() ?? "unknown" : "unknown";
        return new(catalog, maps, version);
    }

    private static string? FindRepository(string start)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) return directory.FullName;
        return null;
    }

    private static MethodInfo ServiceMethod(string name) => typeof(MerchantService)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(MerchantService).FullName, name);

    private static MethodInfo ProductionMethod(string name) => typeof(MerchantOverlayForm)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(MerchantOverlayForm).FullName, name);
}
