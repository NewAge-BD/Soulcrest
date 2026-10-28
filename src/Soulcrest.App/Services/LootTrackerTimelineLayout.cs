using System.Globalization;
using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>View-local filtering of confirmed harvest facts; other activities and legacy unknowns stay intact.</summary>
public static class LootTrackerTimelineResources
{
    public sealed record Resource(string Key, int? GatherableId, string? Name, long? Attempts);
    public static bool IsHarvest(LootTrackerTimelineEvent fact) => fact.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure;
    public static string Key(LootTrackerTimelineEvent fact) => Key(fact.GatherableId, fact.GatherVisit);
    private static string Key(int? id, LootTrackerGatherVisit? visit)
    {
        int? known = id is > 0 ? id : visit?.GatherableId;
        return known is > 0 ? "resource:" + known.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)
            : !string.IsNullOrWhiteSpace(visit?.Name) ? "name:" + visit.Name.Trim() : "unknown";
    }
    public static IReadOnlyList<Resource> Choices(LootTrackerSession session)
    {
        if (!LootTrackerSessionProjection.ShowsHarvest(session)) return [];
        var facts = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents)
            .Where(row => IsHarvest(row) && row.Elapsed >= TimeSpan.Zero && row.Elapsed <= session.Elapsed).ToArray();
        var attempts = facts.Select(row => (Key: Key(row), Id: row.GatherableId is > 0 ? row.GatherableId : row.GatherVisit?.GatherableId,
            Name: row.GatherVisit?.Name)).ToList();
        if (session.TimelineVersion == 0 && facts.Length == 0)
            attempts.AddRange((session.GatherVisits.IsDefault ? [] : session.GatherVisits)
                .Select(visit => (Key(null, visit), visit.GatherableId, (string?)visit.Name)));
        var choices = attempts.GroupBy(row => row.Key, StringComparer.Ordinal).Select(group => new Resource(group.Key,
            group.First().Id, group.Select(row => row.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)), group.LongCount())).ToList();
        // Minute totals cannot be attributed to a resource, even if some old positioned visits survive.
        if (session.TimelineVersion == 0 && facts.Length == 0 && (session.HarvestCount is not null || session.FailedHarvestCount is not null))
        {
            choices.RemoveAll(row => row.Key == "unknown"); choices.Add(new("unknown", null, null, null));
        }
        return choices.OrderBy(row => row.Key == "unknown").ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }
    public static LootTrackerSession Project(LootTrackerSession session, IReadOnlySet<string> selected)
    {
        if (!LootTrackerSessionProjection.ShowsHarvest(session) || session.TimelineEvents.IsDefaultOrEmpty) return session;
        var facts = session.TimelineEvents.Where(row => !IsHarvest(row) || selected.Contains(Key(row))).ToImmutableArray();
        // Exact source facts remain authoritative even when the last selected harvest is hidden.
        return facts.Length == session.TimelineEvents.Length ? session : session with { TimelineEvents = facts, TimelineVersion = Math.Max(1, session.TimelineVersion) };
    }
}

/// <summary>Stable, view-specific preferences. Missing selections mean all layers; an empty selection means none.</summary>
public static class LootTrackerTimelineLayers
{
    public sealed record Layer(string Id, string Label, string Color, bool IsSeries = false);
    public static readonly IReadOnlyList<Layer> All =
    [
        new("ap", "AP-Gewinn", "#78dccc", true), new("rp", "Rangpunkte netto", "#c2a2f5", true),
        new("kina", "Kina", "#efd096", true), new("items", "Itemmenge", "#8fb7e8", true),
        new("souls", "Souls", "#bda0ec", true), new("success", "Ernten", "#9ed9a1", true),
        new("failure", "Fehlversuche (Anzahl)", "#f49eaa", true), new("kill", "Kills", "#86efac"),
        new("death", "Tode", "#fda4af"), new("rank", "Punkte-Stufen", "#efd096"),
        new("gather", "Sammelerfolge", "#67e8f9"), new("failed", "Fehlversuche (Ereignisse)", "#f49eaa"),
        new("loot", "Beute", "#c8b3ef")
    ];
    public static string[] Normalize(IEnumerable<string>? selection) => selection is null ? All.Select(row => row.Id).ToArray()
        : All.Where(row => selection.Contains(row.Id, StringComparer.Ordinal)).Select(row => row.Id).ToArray();
    public static string Color(string key) => All.FirstOrDefault(row => row.Id == key)?.Color ?? "#9aa7b4";
    public static IReadOnlyList<Layer> Available(LootTrackerSession session, IReadOnlyList<LootTrackerTimeline.Series> series)
    {
        var ids = series.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        if (LootTrackerSessionProjection.ShowsAbyss(session)) ids.UnionWith(["kill", "death", "rank"]);
        if (LootTrackerSessionProjection.ShowsHarvest(session)) ids.UnionWith(["gather", "failed"]);
        if (!session.CombatEvents.IsDefaultOrEmpty) ids.UnionWith(session.CombatEvents.Select(row => row.Kind == LootTrackerCombatKind.Kill ? "kill" : "death"));
        ids.Add("loot");
        return All.Where(row => ids.Contains(row.Id)).ToArray();
    }
}

/// <summary>Finite chart geometry and pixel-based clusters; neither normalized heights nor inferred facts.</summary>
public static class LootTrackerTimelineLayout
{
    public sealed record Curve(LootTrackerTimeline.Series Series, string Color, string Axis,
        long Minimum, long Maximum, IReadOnlyList<string> Paths)
    {
        public double Zero => Y(0, Minimum, Maximum);
        public bool IsLine => Series.Key is "ap" or "rp" or "kina";
        public IReadOnlyList<string> Areas { get; init; } = [];
        public IReadOnlyList<Bar> Bars { get; init; } = [];
        public bool HasValues { get; init; }
        public long ScaleMinimum { get; init; }
        public long ScaleMaximum { get; init; }
    }
    public sealed record Bar(TimeSpan From, TimeSpan To, long Value, double X, double Width, double Y, double Height);
    private sealed record Point(TimeSpan Elapsed, double Value);
    private sealed record Interval(TimeSpan From, TimeSpan To, long Value);
    private static readonly double[] IntervalSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    public sealed record Plot(string Key, IReadOnlyList<Curve> Curves);
    public sealed record LootItem(string Key, string Name, long Quantity, string? IconAsset);
    public sealed record LootMoment(string Id, TimeSpan Elapsed, DateTimeOffset At, IReadOnlyList<LootItem> Items);
    public sealed record LootCluster(TimeSpan Elapsed, IReadOnlyList<LootMoment> Moments, IReadOnlyList<LootItem> Items);
    private sealed class LootClusterBuilder
    {
        public List<LootMoment> Moments { get; } = [];
        public Dictionary<string, LootItem> Items { get; } = new(StringComparer.Ordinal);
        public TimeSpan Elapsed => Moments[0].Elapsed;
        public void Add(LootMoment moment)
        {
            Moments.Add(moment);
            foreach (var item in moment.Items)
                Items[item.Key] = Items.TryGetValue(item.Key, out var previous)
                    ? previous with { Quantity = checked(previous.Quantity + item.Quantity) } : item;
        }
        public LootCluster Snapshot() => new(Elapsed, Moments.ToArray(), Items.Values.ToArray());
    }

    /// <summary>
    /// One shared plot, with each unit using its own signed linear scale. Line and translucent area geometry follows
    /// Grindcrest's SessionTimeline.SilverCurve/SilverArea; its pause compression and height flattening are not used.
    /// </summary>
    public static IReadOnlyList<Plot> Plots(IReadOnlyList<LootTrackerTimeline.Series> series, LootTrackerTimelineViewport viewport)
    {
        if (series.Count == 0) return [];
        var intervals = IntervalBounds(viewport);
        return [new("combined", series.Select(row =>
        {
            var isLine = row.Key is "ap" or "rp" or "kina";
            var segments = isLine ? WindowSegments(row, viewport) : [];
            var bars = isLine ? [] : BarIntervals(row, intervals, viewport.Duration);
            var bounds = Bounds(isLine ? segments.SelectMany(segment => segment).Select(point => point.Value)
                : WindowValues(row, viewport));
            var paths = segments.Select(segment => string.Join(" ", segment.Select(point =>
                LootTrackerTimeline.N(viewport.Fraction(point.Elapsed) * 1000) + "," + LootTrackerTimeline.N(Y(point.Value, bounds.Minimum, bounds.Maximum))))).ToArray();
            var baseline = LootTrackerTimeline.N(Y(0, bounds.Minimum, bounds.Maximum));
            return new Curve(row, LootTrackerTimelineLayers.Color(row.Key), row.Key == "rp" ? "right" : "left",
                bounds.Minimum, bounds.Maximum, paths)
            {
                HasValues = bounds.HasValues, ScaleMinimum = bounds.Minimum, ScaleMaximum = bounds.ScaleMaximum,
                Areas = segments.Select((segment, index) => LootTrackerTimeline.N(viewport.Fraction(segment[0].Elapsed) * 1000)
                    + "," + baseline + " " + paths[index] + " " + LootTrackerTimeline.N(viewport.Fraction(segment[^1].Elapsed) * 1000) + "," + baseline).ToArray(),
                Bars = bars.Select(bar =>
                {
                    var x = viewport.Fraction(bar.From) * 1000;
                    var y = Y(bar.Value, bounds.Minimum, bounds.Maximum);
                    var zero = Y(0, bounds.Minimum, bounds.Maximum);
                    return new Bar(bar.From, bar.To, bar.Value, x, Math.Max(0, viewport.Fraction(bar.To) * 1000 - x), Math.Min(y, zero), Math.Abs(y - zero));
                }).ToArray()
            };
        }).ToArray())];
    }

    public static IReadOnlyList<LootTrackerTimeline.EventGroup> ClusterEvents(IEnumerable<LootTrackerTimeline.Event> events,
        LootTrackerTimelineViewport viewport, double width, double markerWidth = 26)
    {
        var result = new List<LootTrackerTimeline.EventGroup>();
        var pixels = double.IsFinite(width) && width > 0 ? width : 1000;
        foreach (var lane in events.Where(row => row.Elapsed >= viewport.From && row.Elapsed <= viewport.To).GroupBy(row => row.Lane))
        {
            var current = new List<LootTrackerTimeline.Event>(); double right = double.NegativeInfinity;
            foreach (var row in lane.OrderBy(row => row.Elapsed))
            {
                var x = Math.Clamp(viewport.Fraction(row.Elapsed) * pixels, markerWidth / 2, Math.Max(markerWidth / 2, pixels - markerWidth / 2));
                if (current.Count > 0 && x - markerWidth / 2 > right) Flush();
                current.Add(row); right = x + markerWidth / 2;
            }
            Flush();
            void Flush() { if (current.Count == 0) return; result.Add(new(lane.Key, current[0].Elapsed, current.ToArray())); current.Clear(); }
        }
        return result.OrderBy(row => row.Elapsed).ToArray();
    }

    public static IReadOnlyList<LootMoment> LootMoments(LootTrackerSession session)
    {
        if (session.TimelineEvents.IsDefaultOrEmpty) return [];
        return session.TimelineEvents.Where(row => row.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess
                && row.Elapsed >= TimeSpan.Zero && row.Elapsed <= session.Elapsed && !row.Loot.IsDefaultOrEmpty)
            .Select(row => new LootMoment(row.Id, row.Elapsed, row.At,
                row.Loot.Where(item => item.Quantity > 0).Select(item => new LootItem(CanonicalKey(item.Id), item.Name,
                    item.Quantity, MonsterLootIconCatalog.Default.DropAsset(item.Id))).ToArray()))
            .Where(row => row.Items.Count > 0).OrderBy(row => row.Elapsed).ToArray();
    }

    public static IReadOnlyList<LootCluster> ClusterLoot(IEnumerable<LootMoment> moments, LootTrackerTimelineViewport viewport,
        double width, IReadOnlySet<string> selected)
    {
        var result = new List<LootClusterBuilder>();
        var pixels = double.IsFinite(width) && width > 0 ? width : 1000;
        var maximumIcons = Math.Max(1, Math.Min(8, (int)(pixels / 31) - 1));
        foreach (var moment in moments.Where(row => row.Elapsed >= viewport.From && row.Elapsed <= viewport.To).OrderBy(row => row.Elapsed))
        {
            var items = moment.Items.Where(item => selected.Contains(item.Key)).ToArray();
            if (items.Length == 0) continue;
            var cluster = new LootClusterBuilder(); cluster.Add(moment with { Items = items });
            // Edge-clamping and a wider combined icon row can reach an earlier cluster.
            // Merge backwards until the actual rendered rectangles are disjoint.
            while (result.Count > 0 && Left(cluster) <= Left(result[^1]) + Width(result[^1]))
            {
                var previous = result[^1];
                foreach (var row in cluster.Moments) previous.Add(row);
                cluster = previous;
                result.RemoveAt(result.Count - 1);
            }
            result.Add(cluster);
        }
        return result.Select(row => row.Snapshot()).ToArray();
        double Width(LootClusterBuilder cluster) => (Math.Min(cluster.Items.Count, maximumIcons) + (cluster.Items.Count > maximumIcons ? 1 : 0)) * 31 - 5;
        double Left(LootClusterBuilder cluster) => Math.Clamp(viewport.Fraction(cluster.Elapsed) * pixels - 13, 0, Math.Max(0, pixels - Width(cluster)));
    }

    public static string CanonicalKey(string key) => MonsterLootItemCatalog.TryItemId(key, out var id)
        ? "Item:" + id.ToString(CultureInfo.InvariantCulture) : key;
    public static double Y(long value, long minimum, long maximum) => Y((double)value, minimum, maximum);
    private static double Y(double value, long minimum, long maximum)
    {
        var range = maximum - (double)minimum;
        return !double.IsFinite(range) || range <= 0 ? 85 : Math.Clamp(150 - (value - minimum) / range * 130, 20, 150);
    }
    private static (long Minimum, long Maximum, bool HasValues, long ScaleMaximum) Bounds(IEnumerable<double> samples)
    {
        var minimum = 0d; var maximum = 0d; var known = false;
        foreach (var value in samples) { known = true; minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
        var low = Long(Math.Floor(minimum)); var high = Long(Math.Ceiling(maximum));
        return (low, high == low ? 1 : high, known, high);
        static long Long(double value) => value <= long.MinValue ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)value;
    }

    private static IEnumerable<double> WindowValues(LootTrackerTimeline.Series series, LootTrackerTimelineViewport viewport)
    {
        if (series.ValueAt(viewport.From) is { } start) yield return start;
        foreach (var sample in series.Samples)
            if (sample.Elapsed >= viewport.From && sample.Elapsed <= viewport.To && sample.Value is { } value) yield return value;
    }

    // Collapse only consecutive confirmed points at one timestamp. A null always splits a source segment, even if
    // confirmation resumes at the same timestamp. ValueAt and the exact-value table keep their recorded step values.
    private static IReadOnlyList<IReadOnlyList<Point>> WindowSegments(LootTrackerTimeline.Series series, LootTrackerTimelineViewport viewport)
    {
        var result = new List<IReadOnlyList<Point>>(); var segment = new List<Point>();
        foreach (var sample in series.Samples)
        {
            if (sample.Value is null)
            {
                if (segment.Count > 0 && segment[^1].Elapsed < sample.Elapsed)
                    segment.Add(segment[^1] with { Elapsed = sample.Elapsed });
                Flush(); continue;
            }
            var point = new Point(sample.Elapsed, sample.Value.Value);
            if (segment.Count > 0 && segment[^1].Elapsed == point.Elapsed) segment[^1] = point;
            else segment.Add(point);
        }
        if (segment.Count > 0 && segment[^1].Elapsed < viewport.To)
            segment.Add(segment[^1] with { Elapsed = viewport.To });
        Flush(); return result;
        void Flush()
        {
            if (segment.Count > 0 && segment[0].Elapsed <= viewport.To && segment[^1].Elapsed >= viewport.From)
            {
                var from = segment[0].Elapsed > viewport.From ? segment[0].Elapsed : viewport.From;
                var to = segment[^1].Elapsed < viewport.To ? segment[^1].Elapsed : viewport.To;
                var visible = new List<Point> { Interpolate(segment, from) };
                visible.AddRange(segment.Where(point => point.Elapsed > from && point.Elapsed < to));
                if (to > from) visible.Add(Interpolate(segment, to));
                result.Add(visible);
            }
            segment.Clear();
        }
    }
    private static Point Interpolate(IReadOnlyList<Point> points, TimeSpan at)
    {
        var low = 0; var high = points.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].Elapsed <= at) low = middle + 1; else high = middle - 1;
        }
        var before = points[Math.Max(0, high)];
        if (before.Elapsed == at || low >= points.Count) return before with { Elapsed = at };
        var after = points[low];
        var fraction = (double)(((decimal)at.Ticks - before.Elapsed.Ticks) / ((decimal)after.Elapsed.Ticks - before.Elapsed.Ticks));
        var value = before.Value + (after.Value - before.Value) * fraction;
        return new(at, Math.Clamp(value, Math.Min(before.Value, after.Value), Math.Max(before.Value, after.Value)));
    }

    // Fixed intervals and a bounded bar count match Grindcrest's SessionTimelineChart without normalizing distinct
    // units against each other. Whole-session anchors keep bars stable when the viewport pans.
    private static IReadOnlyList<(TimeSpan From, TimeSpan To)> IntervalBounds(LootTrackerTimelineViewport viewport)
    {
        if (viewport.To <= viewport.From) return [];
        var seconds = viewport.Window.TotalSeconds;
        var step = IntervalSteps.FirstOrDefault(value => seconds / value <= 96, IntervalSteps[^1]);
        while (seconds / step > 96) step *= 10;
        var ticks = Math.Max(1, (long)Math.Ceiling(step * TimeSpan.TicksPerSecond));
        var result = new List<(TimeSpan, TimeSpan)>();
        for (var at = viewport.From.Ticks / ticks * ticks; at < viewport.To.Ticks;)
        {
            var end = at > long.MaxValue - ticks ? long.MaxValue : at + ticks;
            result.Add((TimeSpan.FromTicks(Math.Max(at, viewport.From.Ticks)), TimeSpan.FromTicks(Math.Min(end, viewport.To.Ticks))));
            if (end <= at) break;
            at = end;
        }
        return result;
    }
    private static IReadOnlyList<Interval> BarIntervals(LootTrackerTimeline.Series series,
        IReadOnlyList<(TimeSpan From, TimeSpan To)> intervals, TimeSpan duration)
    {
        // At a timestamp the final source sample controls the following interval. An instantaneous availability
        // transition has no invented duration; a real unknown span excludes the affected bar in full.
        var source = new List<LootTrackerTimeline.Sample>();
        foreach (var sample in series.Samples)
            if (source.Count > 0 && source[^1].Elapsed == sample.Elapsed) source[^1] = sample; else source.Add(sample);
        var result = new List<Interval>(); var index = 0; long? current = null;
        foreach (var interval in intervals)
        {
            while (index < source.Count && source[index].Elapsed <= interval.From) current = source[index++].Value;
            var known = current is not null;
            while (index < source.Count && source[index].Elapsed < interval.To)
            {
                current = source[index++].Value;
                if (current is null) known = false;
            }
            // A last drop may be the session's terminal fact, and has no later interval in which to appear.
            if (interval.To == duration)
                while (index < source.Count && source[index].Elapsed == interval.To) current = source[index++].Value;
            if (known && current is { } value) result.Add(new(interval.From, interval.To, value));
        }
        return result;
    }
}
