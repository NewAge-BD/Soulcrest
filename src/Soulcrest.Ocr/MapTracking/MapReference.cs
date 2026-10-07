using System.Text;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace Soulcrest.Ocr.MapTracking;

/// <summary>
/// SIFT keypoints of one whole map, the reference the in-game map is matched against
/// (technique of the Map Overlay project, re-implemented: docs/MAP_TRACKING.md).
/// Built once from the map tiles of the data package and cached on disk; building takes seconds.
/// </summary>
public sealed class MapReference : IDisposable
{
    private const string Magic = "SCMAPREF3";

    private MapReference(string mapId, int width, int height, double worldPerPixel, Point2f[] points, float[] descriptors)
    {
        MapId = mapId;
        Width = width;
        Height = height;
        WorldPerPixel = worldPerPixel;
        Points = points;
        DescriptorData = descriptors;
        Descriptors = Mat.FromPixelData(points.Length, DescriptorLength, MatType.CV_32FC1, descriptors);
    }

    public const int DescriptorLength = 128;

    public string MapId { get; }

    /// <summary>Size of the reference image in pixels.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Map coordinates (the marker coordinates of the data package) per reference pixel.</summary>
    public double WorldPerPixel { get; }

    public Point2f[] Points { get; }

    /// <summary>Row-major descriptors (Points.Length × 128), shared with <see cref="Descriptors"/>.</summary>
    public float[] DescriptorData { get; }

    public Mat Descriptors { get; }

    /// <summary>
    /// Reference image from the tile pyramid: all tiles of one zoom level side by side (missing tiles
    /// stay black). <paramref name="tilePattern"/> is a path with {z}, {x}, {y}.
    /// </summary>
    public static Mat ComposeTiles(string tilePattern, int zoom, int tileSize = 256)
    {
        var count = 1 << zoom;
        var image = new Mat(count * tileSize, count * tileSize, MatType.CV_8UC1, Scalar.All(0));
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                var path = tilePattern.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
                if (!File.Exists(path))
                    continue;
                using var tile = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
                if (tile.Empty())
                    continue;
                using var target = new Mat(image, new Rect(x * tileSize, y * tileSize, tileSize, tileSize));
                if (tile.Width == tileSize && tile.Height == tileSize)
                    tile.CopyTo(target);
                else
                    Cv2.Resize(tile, target, new OpenCvSharp.Size(tileSize, tileSize), 0, 0, InterpolationFlags.Area);
            }
        }
        return image;
    }

    /// <summary>
    /// Detects keypoints tile by tile so they cover the whole map evenly: run over the whole image with
    /// a feature limit, SIFT keeps the strongest points and whole areas end up without any.
    /// </summary>
    public static MapReference Build(string mapId, Mat gray, double worldPerPixel, int budget = 0,
        int tile = 512, int margin = 48, CancellationToken cancellationToken = default)
    {
        int tilesX = (gray.Width + tile - 1) / tile, tilesY = (gray.Height + tile - 1) / tile;
        // budget <= 0: no limit (every keypoint SIFT finds). A per-tile limit cost half the matches on
        // the live capture (docs/MAP_TRACKING.md).
        var perTile = budget <= 0 ? 0 : Math.Max(100, budget / (tilesX * tilesY));
        using var sift = SIFT.Create(perTile);
        var points = new List<Point2f>();
        var descriptors = new List<float>();
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int x0 = tx * tile, y0 = ty * tile;
                int x1 = Math.Min(gray.Width, x0 + tile), y1 = Math.Min(gray.Height, y0 + tile);
                // A margin so descriptors near the tile edge still see their full neighbourhood.
                int ex0 = Math.Max(0, x0 - margin), ey0 = Math.Max(0, y0 - margin);
                int ex1 = Math.Min(gray.Width, x1 + margin), ey1 = Math.Min(gray.Height, y1 + margin);
                using var area = new Mat(gray, new Rect(ex0, ey0, ex1 - ex0, ey1 - ey0));
                // Empty tiles (the black margin around the map) have nothing to find.
                Cv2.MinMaxLoc(area, out _, out double max);
                if (max < 20)
                    continue;
                using var des = new Mat();
                sift.DetectAndCompute(area, null, out var keypoints, des);
                if (keypoints.Length == 0 || des.Empty())
                    continue;
                des.GetArray(out float[] rows);
                for (var i = 0; i < keypoints.Length; i++)
                {
                    var p = new Point2f(keypoints[i].Pt.X + ex0, keypoints[i].Pt.Y + ey0);
                    if (p.X < x0 || p.X >= x1 || p.Y < y0 || p.Y >= y1)
                        continue;
                    points.Add(p);
                    descriptors.AddRange(new ArraySegment<float>(rows, i * DescriptorLength, DescriptorLength));
                }
            }
        }
        if (points.Count < 32)
            throw new InvalidOperationException($"Karte {mapId}: zu wenige Merkmale ({points.Count}).");
        return new MapReference(mapId, gray.Width, gray.Height, worldPerPixel, [.. points], [.. descriptors]);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(MapId);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(WorldPerPixel);
            writer.Write(Points.Length);
            foreach (var p in Points)
            {
                writer.Write(p.X);
                writer.Write(p.Y);
            }
            var bytes = new byte[DescriptorData.Length * sizeof(float)];
            Buffer.BlockCopy(DescriptorData, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Null when the file is missing or from another format version.</summary>
    public static MapReference? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadString() != Magic)
                return null;
            var mapId = reader.ReadString();
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var worldPerPixel = reader.ReadDouble();
            var count = reader.ReadInt32();
            var points = new Point2f[count];
            for (var i = 0; i < count; i++)
                points[i] = new Point2f(reader.ReadSingle(), reader.ReadSingle());
            var bytes = reader.ReadBytes(count * DescriptorLength * sizeof(float));
            if (bytes.Length != count * DescriptorLength * sizeof(float))
                return null;
            var descriptors = new float[count * DescriptorLength];
            Buffer.BlockCopy(bytes, 0, descriptors, 0, bytes.Length);
            return new MapReference(mapId, width, height, worldPerPixel, points, descriptors);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public void Dispose() => Descriptors.Dispose();
}
