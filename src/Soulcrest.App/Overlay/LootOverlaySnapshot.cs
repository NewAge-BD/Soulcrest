using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

public enum LootOverlayDropKind { Item, SoulCrystal }

/// <summary>One already counted drop representation. The source resolves crystal/auto-use duplicates.</summary>
public sealed record LootOverlayDrop(string Key, string Name, long Count,
    LootOverlayDropKind Kind = LootOverlayDropKind.Item)
{
    /// <summary>Exact bundled icon asset, or null when no matching graphic is known.</summary>
    public string? IconPath { get; init; }
    public LootItemRarity Rarity { get; init; }
}

/// <summary>Presentation only: an absent daily total must never be inferred from session Kina.</summary>
public sealed record LootOverlaySnapshot(string? SessionId, bool SessionActive, long SessionKina,
    long? DailyMonsterKina, IReadOnlyList<LootOverlayDrop> Drops, bool TrackingAvailable = true,
    string? CompletionScopeKey = null)
{
    public const long DailyLimit = 1_000_000;
    public static LootOverlaySnapshot Empty { get; } = new(null, false, 0, null, [], TrackingAvailable: false);
    public bool DailyLimitReached => HasPveActivity && SessionKind is not (LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Lifeskill)
        && DailyMonsterKina >= DailyLimit;
    public LootTrackerSessionKind SessionKind { get; init; }
    public long? SessionAbyssPoints { get; init; }
    public double? SessionAbyssPointsRate { get; init; }
    public AbyssPointRatePeriod AbyssPointRatePeriod { get; init; }
    public long? FarmedAbyssPoints { get; init; }
    public long? AbyssPointCap { get; init; }
    public bool AbyssFarmConfirmed { get; init; }
    public bool SessionKinaKnown { get; init; } = true;
    public string? AbyssPointIconPath { get; init; }
    /// <summary>False means a saved value for the safely bound character, awaiting fresh confirmation.</summary>
    public bool DailyConfirmed { get; init; } = true;
    public string? KinaIconPath { get; init; }
    public bool AnimationsEnabled { get; init; } = true;
    public RareDropNotice? RareDrop { get; init; }
    public IReadOnlyList<RareDropNotice> RareDrops { get; init; } = [];
    public bool LimitedKinaSession { get; init; }
    public double? SessionKinaPerHour { get; init; }
    public TimeSpan SessionElapsed { get; init; }
    /// <summary>Activity channels of the shared current session, independent of the detail-view selection.</summary>
    public bool HasPveActivity { get; init; } = true;
    /// <summary>Confirmed AP income of the current session, never a wallet or cap baseline.</summary>
    public bool HasAbyssPointActivity { get; init; }
    public bool HasLifeskillActivity { get; init; }
    public long? HarvestCount { get; init; }
    public long? FailedHarvestCount { get; init; }
    /// <summary>Successful harvests divided by the entire shared session duration; null remains unknown.</summary>
    public double? HarvestsPerHour { get; init; }
    public long? ItemCount { get; init; }
}

public interface ILootOverlaySource
{
    LootOverlaySnapshot Snapshot { get; }
    event Action? Changed;
}

/// <summary>Adapter boundary for the host's persisted settings; does not write settings itself.</summary>
public sealed record LootOverlayOptions(bool Enabled = false, int X = 48, int Y = 300, double Scale = 1,
    string Theme = "aion2-stylized", bool VisibleInRecordings = false, bool AnimationsEnabled = true)
{
    public double BackgroundOpacity { get; init; } = 1;
    public bool ShowOnSessionStart { get; init; }
    public bool OnlyDuringSession { get; init; }
}
