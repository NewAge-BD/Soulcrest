using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Library examples use actual display ranges with synthetic rolls; never a network or profile observation.</summary>
internal static class GenusInsightOverlayExamples
{
    internal static GenusInsightSnapshot Create(DateTimeOffset at, string profile)
    {
        var catalog = GenusInsightCatalog.Default;
        var boards = GenusInsightPresentation.Genera.Select(genus => new GenusInsightBoardView(genus, 10,
            Array.AsReadOnly(Enumerable.Range(1, 9).Select(number =>
            {
                var options = catalog.Options(genus, number);
                var rank = number == 9 ? (byte)5 : (byte)((number - 1) % 5 + 1);
                var option = options.FirstOrDefault(candidate => candidate.Rank == rank) ?? options.First();
                var stat = catalog.FindStat(option.StatId)!;
                var range = catalog.Range(genus, number, stat.StatId)!;
                var value = number == 9 ? option.Maximum : option.Minimum + (option.Maximum - option.Minimum) / 2;
                if (stat.Unit == "Points") value = decimal.Floor(value);
                return new GenusInsightSlotView
                {
                    Slot = (byte)number, StatId = stat.StatId, LabelDe = stat.LabelDe, LabelEn = stat.LabelEn,
                    Unit = stat.Unit, Value = value, Rank = option.Rank, Minimum = range.Minimum, Maximum = range.Maximum,
                    RollFraction = range.Minimum == range.Maximum ? 1 : (double)((value - range.Minimum) / (range.Maximum - range.Minimum)),
                    IsMaxRoll = value == range.Maximum, IsFavorite = number is 2 or 4, IsLocked = number is 2 or 4,
                    IsActivated = true, IsCurrent = true
                };
            }).ToArray()))).ToArray();
        return new() { ProfileId = profile, Generation = 1, Sequence = 1, ObservedAt = at, IsCurrent = true,
            Preset = 1, ViewedPreset = 1, ViewedBoard = boards[0], MenuVisible = true, SelectedGenus = "cogni", MenuSourceGeneration = 1, Boards = Array.AsReadOnly(boards) };
    }
}
