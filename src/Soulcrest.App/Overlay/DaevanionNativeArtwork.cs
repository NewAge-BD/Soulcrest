namespace Soulcrest.App.Overlay;

/// <summary>Small, embedded game-style materials shared by live and offline renderers.</summary>
internal static class DaevanionNativeArtwork
{
    internal static NativeImage StatBackground { get; } = new("stat-background");
    internal static NativeImage Dot { get; } = new("dot");
    internal static NativeImage Button { get; } = new("button");
    internal static NativeImage CheckboxOn { get; } = new("checkbox-on");
    internal static NativeImage CheckboxOff { get; } = new("checkbox-off");

    // These five tiny sprites live with the assembly. Decode once, without retaining
    // resource streams. GDI+ images cannot be drawn concurrently without serialization.
    internal sealed class NativeImage(string name)
    {
        private readonly object _gate = new();
        private readonly Lazy<Bitmap> _image = new(() => Load(name));
        internal int Width { get { lock (_gate) return _image.Value.Width; } }
        internal int Height { get { lock (_gate) return _image.Value.Height; } }

        internal void Paint(Graphics graphics, RectangleF bounds)
        {
            lock (_gate) graphics.DrawImage(_image.Value, bounds);
        }

        private static Bitmap Load(string name)
        {
            using var stream = typeof(DaevanionNativeArtwork).Assembly
                .GetManifestResourceStream($"Soulcrest.Daevanion.{name}.png")
                ?? throw new InvalidDataException("Missing embedded Daevanion material.");
            using var original = new Bitmap(stream);
            return new Bitmap(original);
        }
    }
}
