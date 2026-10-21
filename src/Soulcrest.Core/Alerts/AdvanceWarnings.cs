namespace Soulcrest.Core.Alerts;

/// <summary>Positive warning offsets; an explicit empty selection never falls back to a legacy value.</summary>
public static class AdvanceWarnings
{
    public static int[] Resolve(int[]? values, int legacy, int maximum) => (values ?? [legacy])
        .Where(value => value > 0 && value <= maximum).Distinct().OrderDescending().ToArray();

    public static bool Valid(int[]? values, int maximum) => values is null || values.All(value => value > 0 && value <= maximum);

    public static int[] Toggle(int[] values, int value, bool enabled) =>
        (enabled && values.Contains(value) ? values.Where(item => item != value) : values.Append(value))
        .Distinct().OrderDescending().ToArray();
}
