using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.App.OverlayEditorKit;

internal readonly record struct GenusInsightOverlayInterval(GenusInsightOption Option, double Start, double End);

internal readonly record struct GenusInsightOverlayLabel(byte Rank, string Text, double Center, int Row, double HalfWidth);

/// <summary>Positions for the five fixed lanes on the slot's shared min/max scale.</summary>
internal readonly struct GenusInsightOverlaySpectrum(GenusInsightSlotView slot)
{
    internal const byte RankCount = 5;

    internal double? Current => Confirmed(slot)
        && slot.RollFraction is { } supplied && double.IsFinite(supplied) && supplied is >= 0 and <= 1
            ? Position(slot.Value!.Value, slot.Minimum!.Value, slot.Maximum!.Value) : null;

    internal GenusInsightOverlayInterval? Interval(byte rank)
    {
        if (slot.Unit is not ("Points" or "Percent") || slot.Minimum is not { } minimum || slot.Maximum is not { } maximum
            || maximum < minimum)
            return null;

        foreach (var option in slot.RarityOptions)
        {
            if (option.Rank == rank && option.StatId == slot.StatId
                && option.Minimum >= minimum && option.Maximum <= maximum && option.Minimum <= option.Maximum)
                return new(option, Position(option.Minimum, minimum, maximum), Position(option.Maximum, minimum, maximum));
        }
        return null;
    }

    internal IReadOnlyList<GenusInsightOverlayLabel> Labels(CultureInfo culture, double trackWidth, Func<string, double> measure)
    {
        var labels = new List<GenusInsightOverlayLabel>(RankCount);
        for (byte rank = 1; rank <= RankCount; rank++)
        {
            if (Interval(rank) is not { } interval) continue;
            var text = $"{BoundaryText(interval.Option.Minimum, culture)}–{BoundaryText(interval.Option.Maximum, culture)}";
            var center = (interval.Start + interval.End) / 2;
            var halfWidth = (measure(text) + 3) / 2;
            var row = 0;
            // Move close labels down while keeping their exact position on the value scale.
            while (labels.Any(label => label.Row == row
                && Math.Abs(label.Center - center) * trackWidth < label.HalfWidth + halfWidth))
                row++;
            labels.Add(new(rank, text, center, row, halfWidth));
        }
        return labels;
    }

    internal static int LabelRows(IReadOnlyList<GenusInsightOverlayLabel> labels) =>
        labels.Count == 0 ? 1 : labels.Max(label => label.Row) + 1;

    internal static string BoundaryText(decimal value, CultureInfo culture) => value.ToString("0.############################", culture);

    internal static bool Confirmed(GenusInsightSlotView slot) => slot.IsCurrent && slot.Rank is >= 1 and <= RankCount
        && slot.Value is { } value && slot.Minimum is { } minimum && slot.Maximum is { } maximum
        && value >= minimum && value <= maximum && slot.Unit is "Points" or "Percent";

    // Keep an exact one-value roll at the same end as the existing roll fraction.
    private static double Position(decimal value, decimal minimum, decimal maximum) =>
        minimum == maximum ? 1d : (double)((value - minimum) / (maximum - minimum));
}
