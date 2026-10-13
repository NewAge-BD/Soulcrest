using System.Drawing;
using Soulcrest.Ocr.Shop;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Pure, single-caller layout gate. Two distinct capture frames must agree before placement;
/// capture age, foreground ownership and feature preferences are checked by the caller.
/// </summary>
internal sealed class MerchantLayoutStability
{
    private object? _snapshotIdentity;
    private Rectangle _bounds;
    private long _frameAt;
    private Anchor[]? _reference;
    private bool _ready;
    private readonly record struct Anchor(int X, int Y, int Width, int Height, double Scale);

    public bool Update(object snapshotIdentity, Rectangle bounds, long frameAt, IReadOnlyList<MerchantCardAnchor> anchors)
    {
        if (snapshotIdentity is null || frameAt <= 0 || !TryNormalize(bounds, anchors, out var current))
        {
            Reset();
            return false;
        }
        if (_reference is not null && frameAt < _frameAt)
        {
            Reset();
            return false;
        }
        if (_reference is null || !ReferenceEquals(snapshotIdentity, _snapshotIdentity) || bounds != _bounds || !Matches(_reference, current))
        {
            _snapshotIdentity = snapshotIdentity;
            _bounds = bounds;
            _frameAt = frameAt;
            _reference = current;
            _ready = false;
            return false;
        }
        // Re-reading an already verified frame may keep a ready layout, but never establishes it.
        if (frameAt == _frameAt) return _ready;
        _frameAt = frameAt;
        _ready = true;
        return true;
    }

    public void Reset()
    {
        _snapshotIdentity = null;
        _bounds = default;
        _frameAt = 0;
        _reference = null;
        _ready = false;
    }

    private static bool TryNormalize(Rectangle bounds, IReadOnlyList<MerchantCardAnchor> anchors, out Anchor[] current)
    {
        current = [];
        if (bounds.Width <= 0 || bounds.Height <= 0 || anchors is null || anchors.Count != 10) return false;
        current = new Anchor[10];
        var seen = new bool[10];
        foreach (var anchor in anchors)
        {
            if (anchor is null || anchor.SlotIndex is < 0 or >= 10 || seen[anchor.SlotIndex] ||
                !double.IsFinite(anchor.Scale) || anchor.Scale <= 0 || !double.IsFinite(anchor.Scale * 3) ||
                !double.IsFinite(anchor.Confidence) || anchor.Confidence < .72) return false;
            var price = anchor.PriceBounds;
            if (price.X < 0 || price.Y < 0 || price.Width <= 0 || price.Height <= 0 ||
                (long)price.X + price.Width > bounds.Width || (long)price.Y + price.Height > bounds.Height) return false;
            seen[anchor.SlotIndex] = true;
            // Copy values rather than retain the caller's potentially mutable list.
            current[anchor.SlotIndex] = new(price.X, price.Y, price.Width, price.Height, anchor.Scale);
        }
        return true;
    }

    private static bool Matches(Anchor[] reference, Anchor[] current)
    {
        for (var i = 0; i < reference.Length; i++)
        {
            var first = reference[i];
            var next = current[i];
            var tolerance = Math.Max(3, Math.Min(first.Scale, next.Scale) * 3);
            if (Math.Abs((long)first.X - next.X) > tolerance || Math.Abs((long)first.Y - next.Y) > tolerance ||
                Math.Abs((long)first.X + first.Width - next.X - next.Width) > tolerance ||
                Math.Abs((long)first.Y + first.Height - next.Y - next.Height) > tolerance) return false;
        }
        // Keep the initial geometric reference to prevent small per-frame drift accumulating.
        return true;
    }
}
