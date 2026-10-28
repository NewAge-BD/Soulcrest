namespace Soulcrest.App.Overlay;

public enum AbyssKillFeedKind { Kill = 0, Death = 1, Assist = 2 }

/// <summary>A qualified own PvP result. Opponent names are transient display data and must never be journaled.</summary>
public sealed record AbyssKillFeedEntry(DateTimeOffset At, AbyssKillFeedKind Kind, string? OpponentName = null,
    long? RankPointDelta = null)
{
    public long Generation { get; init; }
    public long Sequence { get; init; }
    /// <summary>Explicit earned AP for this result; null means not attributed, not zero.</summary>
    public long? AbyssPointGain { get; init; }
    public string? OpponentClassId { get; init; }
    /// <summary>Explicit opponent game rank grade (1..19), never the own points tier.</summary>
    public int? OpponentAbyssRankGrade { get; init; }
}

/// <summary>Own absolute rank points. A points tier is not the game's population-based class rank.</summary>
public sealed record AbyssRankOverlaySnapshot(long? RankPoints, bool CharacterBound = false, bool Confirmed = false)
{
    public static AbyssRankOverlaySnapshot Empty { get; } = new(RankPoints: null);
    /// <summary>Current own binding is confirmed inside the Abyss; cached rank data alone never shows the HUD.</summary>
    public bool WorldContextVisible { get; init; }
    public string? ProfileId { get; init; }
    public long? BindingGeneration { get; init; }
    /// <summary>The accepted source sequence of this rank fact; unrelated AP/UI updates do not advance it.</summary>
    public long RankObservationSequence { get; init; }
    public DateTimeOffset? RankPointsObservedAt { get; init; }
    public bool RankTrackingActive { get; init; }
    /// <summary>RAM-only epoch changes across invalidation, including boundaries hidden by UI event coalescing.</summary>
    public long RankContinuityRevision { get; init; }
    public string? SessionId { get; init; }
    public bool SessionActive { get; init; }
    /// <summary>Observed gains minus losses in the matching current Abyss session; null is unknown.</summary>
    public long? SessionNetRankPoints { get; init; }
    public double? SessionRankPointsPerHour { get; init; }
    public IReadOnlyList<AbyssKillFeedEntry> KillFeed { get; init; } = [];
    public bool KillFeedEnabled { get; init; } = true;
    public bool ShowKills { get; init; } = true;
    public bool ShowDeaths { get; init; }
    public bool ShowAssists { get; init; } = true;
}

public interface IAbyssRankOverlaySource
{
    AbyssRankOverlaySnapshot Snapshot { get; }
    event Action? Changed;
}

public sealed record AbyssRankOverlayOptions(bool Enabled = false, int X = 48, int Y = 220, double Scale = 1,
    string Theme = "aion2-stylized", bool VisibleInRecordings = false)
{
    public bool ReducedMotion { get; init; }
    public string Language { get; init; } = "en";
    public bool KillFeedEnabled { get; init; } = true;
    public bool ShowKills { get; init; } = true;
    public bool ShowDeaths { get; init; }
    public bool ShowAssists { get; init; } = true;
}
