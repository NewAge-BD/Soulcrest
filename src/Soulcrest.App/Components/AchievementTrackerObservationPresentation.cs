using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

/// <summary>Anonymous diagnostics only: explicit counters, fixed enums and binding equality; never serialize services or identities.</summary>
public static class AchievementTrackerObservationPresentation
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Enabled(bool requested = true) => requested && int.TryParse(
        Environment.GetEnvironmentVariable("SOULCREST_WEBVIEW_DEBUG_PORT"), out var port) && port is > 1024 and < 65536;

    public static AchievementTrackerObservationDto Create(NetworkAchievementObservation? source,
        AchievementOwnSnapshot? own, AchievementViewState? selected, AchievementTrackerSnapshot? tracker,
        bool trackerSourceInstanceMatchesOwn = false)
    {
        var sourceMatches = source is not null && own is not null && source.SourceInstanceId != Guid.Empty
            && source.SourceInstanceId == own.SourceInstanceId;
        var hasOwnBinding = own?.Binding is not null;
        IEnumerable<AchievementTrackerRow> rows = tracker is null || tracker.Rows.IsDefault ? [] : tracker.Rows;
        return new(
            source is null ? null : new(source.Sequence, source.Generation,
                Enum.IsDefined(source.Domain.Status) ? source.Domain.Status : NetworkCombatSourceStatus.Unknown,
                source.Domain.IsFresh, Enum.IsDefined(source.Kind) ? source.Kind : null,
                source.Domain.Value?.Achievements.Count ?? 0),
            own is null ? null : new(own.State.Sequence, own.State.Generation, own.IsLive,
                Enum.IsDefined(own.State.Source) ? own.State.Source : AchievementViewSource.Unknown,
                own.Raw?.Achievements.Count ?? 0, hasOwnBinding && selected?.Binding == own!.Binding, sourceMatches, own.State.MetadataBuild),
            tracker is null ? null : new(tracker.IsLive, hasOwnBinding && tracker.Binding == own!.Binding,
                own is not null && own.SourceInstanceId != Guid.Empty && trackerSourceInstanceMatchesOwn,
                Array.AsReadOnly(rows.Select(row => new AchievementTrackerRowObservationDto(
                    row.FamilyId, row.TrackedTitleId, row.StageId,
                    Enum.IsDefined(row.State) ? row.State : AchievementTrackerRowState.Unknown,
                    row.Current, row.Target, row.RawValue,
                    Enum.IsDefined(row.Unit) ? row.Unit : CombatAchievementProgressUnit.Unknown,
                    row.IsFavorite, row.IsHistorical)).ToArray())));
    }

    public static string Serialize(AchievementTrackerObservationDto observation) => JsonSerializer.Serialize(observation, Json);
}

public sealed record AchievementTrackerObservationDto(AchievementSourceObservationDto? Source,
    AchievementOwnStateObservationDto? OwnState, AchievementNativeTrackerObservationDto? Tracker)
{ public int Schema { get; init; } = 1; }

public sealed record AchievementSourceObservationDto(long Seq, long Gen, NetworkCombatSourceStatus Status,
    bool Fresh, NetworkAchievementObservationKind? Kind, int RowCount);
public sealed record AchievementOwnStateObservationDto(long Seq, long Gen, bool IsLive, AchievementViewSource Source,
    int RowCount, bool SelectedBindingMatchesOwn, bool SourceInstanceMatchesSource, int MetadataBuild);
public sealed record AchievementNativeTrackerObservationDto(bool IsLive, bool BindingMatchesOwn,
    bool SourceInstanceMatchesOwn, IReadOnlyList<AchievementTrackerRowObservationDto> Rows);
public sealed record AchievementTrackerRowObservationDto(int FamilyId, int? TitleId, int? StageId,
    AchievementTrackerRowState State, long? Current, long? Target, int? RawValue,
    CombatAchievementProgressUnit Unit, bool Favorite, bool Historical);
