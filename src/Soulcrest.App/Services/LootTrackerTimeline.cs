using System.Globalization;

namespace Soulcrest.App.Services;

/// <summary>Read-only projections of recorded facts. A playhead never changes session totals.</summary>
public static class LootTrackerTimeline
{
    public sealed record Sample(TimeSpan Elapsed, long? Value);
    public sealed record Series(string Key, string Label, IReadOnlyList<Sample> Samples, bool Aggregated = false)
    {
        public long? ValueAt(TimeSpan elapsed)
        {
            var low = 0; var high = Samples.Count - 1; var found = -1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (Samples[middle].Elapsed <= elapsed) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return found < 0 ? null : Samples[found].Value;
        }
        public long Minimum { get; } = Math.Min(0, Samples.Where(s => s.Value is not null).Select(s => s.Value!.Value).DefaultIfEmpty(0).Min());
        public long Maximum { get; } = Math.Max(1, Samples.Where(s => s.Value is not null).Select(s => s.Value!.Value).DefaultIfEmpty(0).Max());
        public IReadOnlyList<string> Paths(TimeSpan duration)
        {
            var result = new List<string>(); var points = new List<string>();
            var minimum = Minimum; var maximum = Maximum;
            foreach (var sample in Samples)
            {
                if (sample.Value is null) { Flush(); continue; }
                var x = Fraction(sample.Elapsed, duration) * 1000;
                var y = 86 - (sample.Value.Value - (double)minimum) / (maximum - (double)minimum) * 74;
                points.Add($"{N(x)},{N(y)}");
            }
            Flush(); return result;
            void Flush() { if (points.Count > 0) result.Add(string.Join(" ", points)); points.Clear(); }
        }
    }
    public sealed record Event(string Id, string Lane, TimeSpan Elapsed, DateTimeOffset At, string Label);
    public sealed record EventGroup(string Lane, TimeSpan Elapsed, IReadOnlyList<Event> Events);

    public static IReadOnlyList<Series> Build(LootTrackerSession session, LootTimelineValueMode mode)
    {
        if (session.ProjectionViewMode is not null
            || session.Kind != LootTrackerSessionKind.Abyss && LootTrackerSessionProjection.ShowsPoints(session))
        {
            var series = new List<Series>();
            if (LootTrackerSessionProjection.ShowsPoints(session))
            {
                series.Add(Metric(session, "ap", "AP-Gewinn", [LootTrackerTimelineMetric.AbyssPoints], mode));
                series.Add(Metric(session, "rp", "Rangpunkte netto", [LootTrackerTimelineMetric.RankPoints], mode));
            }
            if (LootTrackerSessionProjection.ShowsPve(session) || LootTrackerSessionProjection.ShowsAbyss(session))
            {
                var unbound = session.KinaBound is null;
                series.Add(Metric(session, "kina", unbound ? "Ungebundene Kina" : "Kina gesamt", unbound
                    ? [LootTrackerTimelineMetric.KinaUnbound] : [LootTrackerTimelineMetric.KinaBound, LootTrackerTimelineMetric.KinaUnbound], mode));
                series.Add(Counter(session, "souls", "Souls", mode));
            }
            series.Add(Counter(session, "items", "Itemmenge", mode));
            if (LootTrackerSessionProjection.ShowsHarvest(session))
            {
                series.Add(Counter(session, "success", "Ernten", mode));
                series.Add(Counter(session, "failure", "Fehlversuche", mode));
            }
            return series;
        }
        if (session.Kind == LootTrackerSessionKind.Abyss)
            return [Metric(session, "ap", "AP-Gewinn", [LootTrackerTimelineMetric.AbyssPoints], mode),
                Metric(session, "rp", "Rangpunkte netto", [LootTrackerTimelineMetric.RankPoints], mode)];
        if (session.Kind == LootTrackerSessionKind.Lifeskill)
            return [Counter(session, "items", "Itemmenge", mode), Counter(session, "success", "Sammelerfolge", mode),
                Counter(session, "failure", "Fehlversuche", mode)];
        var onlyUnbound = session.KinaBound is null && session.KinaUnbound is >= 0;
        return [Metric(session, "kina", onlyUnbound ? "Ungebundene Kina" : "Kina gesamt",
                onlyUnbound ? [LootTrackerTimelineMetric.KinaUnbound] : [LootTrackerTimelineMetric.KinaBound, LootTrackerTimelineMetric.KinaUnbound], mode),
            Counter(session, "items", "Itemmenge", mode), Counter(session, "souls", "Souls", mode)];
    }

    private static Series Metric(LootTrackerSession session, string key, string label,
        LootTrackerTimelineMetric[] metrics, LootTimelineValueMode mode)
    {
        if (session.TimelineVersion == 0 && session.TimelineEvents.IsDefaultOrEmpty)
            return Legacy(session, key, label, mode);
        var available = metrics.ToDictionary(m => m, _ => false);
        var samples = new List<Sample> { new(TimeSpan.Zero, null) };
        var increments = new List<(TimeSpan At, long Amount)>(); long sum = 0;
        foreach (var row in Rows(session))
        {
            var metric = MetricFor(row.Kind);
            if (row.Kind == LootTrackerTimelineEventKind.Availability && row.Metric is { } channel && available.ContainsKey(channel))
            {
                samples.Add(new(row.Elapsed, available.Values.All(v => v) ? sum : null));
                available[channel] = row.Available == true;
            }
            else if (metric is { } income && available.ContainsKey(income) && row.Amount is { } amount)
            {
                samples.Add(new(row.Elapsed, available.Values.All(v => v) ? sum : null));
                sum = checked(sum + amount); available[income] = true; increments.Add((row.Elapsed, amount));
            }
            else continue;
            samples.Add(new(row.Elapsed, available.Values.All(v => v) ? sum : null));
        }
        samples.Add(new(End(session), available.Values.All(v => v) ? sum : null));
        return new(key, label, mode == LootTimelineValueMode.Cumulative ? samples : PerMinute(samples, increments, End(session)));
    }

    private static Series Counter(LootTrackerSession session, string key, string label, LootTimelineValueMode mode)
    {
        var exact = session.TimelineVersion > 0 || !session.TimelineEvents.IsDefaultOrEmpty;
        if (key == "failure" && session.FailedHarvestCount is null)
            return new(key, label, [new(TimeSpan.Zero, null), new(End(session), null)]);
        if (session.ProjectionViewMode is not null && (key == "success" && session.HarvestCount is null || key == "items" && session.ItemCount is null
            || key == "souls" && session.SoulCount is null))
            return new(key, label, [new(TimeSpan.Zero, null), new(End(session), null)]);
        if (!exact) return new(key, label, [new(TimeSpan.Zero, null), new(End(session), null)], true);
        var samples = new List<Sample> { new(TimeSpan.Zero, 0) };
        var increments = new List<(TimeSpan At, long Amount)>(); long sum = 0;
        foreach (var row in Rows(session))
        {
            long amount = key switch
            {
                "success" => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess ? 1 : 0,
                "failure" => row.Kind == LootTrackerTimelineEventKind.HarvestFailure ? 1 : 0,
                _ => row.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess
                    && !row.Loot.IsDefaultOrEmpty ? row.Loot.Where(l => l.Kind == (key == "souls" ? LootTrackerLootKind.Soul : LootTrackerLootKind.Item)).Sum(l => l.Quantity) : 0
            };
            if (amount == 0) continue;
            samples.Add(new(row.Elapsed, sum)); sum = checked(sum + amount);
            samples.Add(new(row.Elapsed, sum)); increments.Add((row.Elapsed, amount));
        }
        samples.Add(new(End(session), sum));
        return new(key, label, mode == LootTimelineValueMode.Cumulative ? samples : PerMinute(samples, increments, End(session)));
    }

    private static List<Sample> PerMinute(List<Sample> cumulative, List<(TimeSpan At, long Amount)> increments, TimeSpan end)
    {
        var result = new List<Sample>(); var values = increments.GroupBy(i => (int)Math.Floor(i.At.TotalMinutes)).ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));
        var source = new Series("", "", cumulative);
        var sampleIndex = 0;
        for (var minute = 0; minute <= (int)Math.Floor(end.TotalMinutes); minute++)
        {
            var start = TimeSpan.FromMinutes(minute); var finish = TimeSpan.FromTicks(Math.Min(end.Ticks, start.Add(TimeSpan.FromMinutes(1)).Ticks));
            var known = source.ValueAt(start) is not null;
            var amount = values.GetValueOrDefault(minute);
            result.Add(new(start, known ? amount : null));
            while (sampleIndex < cumulative.Count && cumulative[sampleIndex].Elapsed <= start) sampleIndex++;
            while (sampleIndex < cumulative.Count && cumulative[sampleIndex].Elapsed < finish)
            {
                var sample = cumulative[sampleIndex++];
                known = sample.Value is not null;
                result.Add(new(sample.Elapsed, known ? amount : null));
            }
            result.Add(new(finish, known ? amount : null));
        }
        return result;
    }

    private static Series Legacy(LootTrackerSession session, string key, string label, LootTimelineValueMode mode)
    {
        var samples = new List<Sample> { new(TimeSpan.Zero, null) }; long sum = 0; int? previous = null;
        foreach (var bin in session.MinuteIncome.IsDefault ? Enumerable.Empty<LootTrackerIncomeBin>() : session.MinuteIncome.OrderBy(b => b.Minute))
        {
            if (bin.Minute < 0) continue;
            long? amount = key switch
            {
                "ap" => bin.AbyssPoints,
                "rp" => bin.RankPointsGained is { } gained && bin.RankPointsLost is { } lost ? checked(gained - lost) : null,
                _ => session.KinaBound is null ? bin.KinaUnbound : bin.KinaBound is { } bound && bin.KinaUnbound is { } unbound ? checked(bound + unbound) : null
            };
            var start = TimeSpan.FromTicks(Math.Min(End(session).Ticks, TimeSpan.FromMinutes(bin.Minute).Ticks));
            var finish = TimeSpan.FromTicks(Math.Min(End(session).Ticks, TimeSpan.FromMinutes((double)bin.Minute + 1).Ticks));
            if (previous is null || previous + 1 != bin.Minute || amount is null) samples.Add(new(start, null));
            if (amount is { } value)
            {
                if (mode == LootTimelineValueMode.Cumulative)
                { samples.Add(new(start, sum)); sum = checked(sum + value); samples.Add(new(finish, sum)); }
                else { samples.Add(new(start, value)); samples.Add(new(finish, value)); }
            }
            if (finish < End(session)) samples.Add(new(finish, null));
            previous = bin.Minute;
        }
        if (samples[^1].Elapsed < End(session)) samples.Add(new(samples[^1].Elapsed, null));
        samples.Add(new(End(session), samples[^1].Value));
        return new(key, label, samples, true);
    }

    public static IReadOnlyList<Event> Events(LootTrackerSession session)
    {
        var events = new List<Event>();
        if (!session.CombatEvents.IsDefaultOrEmpty)
            events.AddRange(session.CombatEvents.Select(e => new Event(e.Id, e.Kind == LootTrackerCombatKind.Kill ? "kill" : "death", e.Elapsed, e.At,
                UiText.T(e.Kind == LootTrackerCombatKind.Kill ? "Kill" : "Tod"))));
        foreach (var row in Rows(session))
        {
            var lane = row.Kind switch
            {
                LootTrackerTimelineEventKind.PointsStageReached => "rank", LootTrackerTimelineEventKind.HarvestSuccess => "gather",
                LootTrackerTimelineEventKind.HarvestFailure => "failure", LootTrackerTimelineEventKind.Drop => "loot", _ => null
            };
            if (lane is null) continue;
            var label = row.Kind switch
            {
                LootTrackerTimelineEventKind.PointsStageReached => UiText.F("Punkte-Stufe erreicht: {0}", AbyssRankCatalog.Default.Stages.FirstOrDefault(s => s.Id == row.ToStageId)?.Name(UiText.Language) ?? UiText.T("Unbekannt")),
                LootTrackerTimelineEventKind.HarvestFailure => UiText.T("Fehlversuch"),
                _ => !row.Loot.IsDefaultOrEmpty ? string.Join(", ", row.Loot.Select(l => $"{l.Name} ×{l.Quantity.ToString("N0", CultureInfo.InvariantCulture)}")) : UiText.T("Sammelerfolg")
            };
            events.Add(new(row.Id, lane, row.Elapsed, row.At, label));
        }
        return events.Where(e => e.Elapsed >= TimeSpan.Zero && e.Elapsed <= End(session)).OrderBy(e => e.Elapsed).ToArray();
    }

    public static IReadOnlyList<EventGroup> Groups(IReadOnlyList<Event> events, TimeSpan duration) => events
        .GroupBy(e => (e.Lane, Bucket: (int)Math.Floor(Fraction(e.Elapsed, duration) * 80)))
        .Select(g => new EventGroup(g.Key.Lane, g.First().Elapsed, g.ToArray())).ToArray();

    private static IEnumerable<LootTrackerTimelineEvent> Rows(LootTrackerSession session) => session.TimelineEvents.IsDefaultOrEmpty ? []
        : session.TimelineEvents.Where(e => e.Elapsed >= TimeSpan.Zero && e.Elapsed <= End(session)).OrderBy(e => e.Elapsed);
    private static LootTrackerTimelineMetric? MetricFor(LootTrackerTimelineEventKind kind) => kind switch
    {
        LootTrackerTimelineEventKind.AbyssPoints => LootTrackerTimelineMetric.AbyssPoints,
        LootTrackerTimelineEventKind.RankPoints => LootTrackerTimelineMetric.RankPoints,
        LootTrackerTimelineEventKind.KinaBound => LootTrackerTimelineMetric.KinaBound,
        LootTrackerTimelineEventKind.KinaUnbound => LootTrackerTimelineMetric.KinaUnbound, _ => null
    };
    private static TimeSpan End(LootTrackerSession session) => session.Elapsed < TimeSpan.Zero ? TimeSpan.Zero : session.Elapsed;
    public static double Fraction(TimeSpan elapsed, TimeSpan duration) => duration <= TimeSpan.Zero ? 0 : Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
    public static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
