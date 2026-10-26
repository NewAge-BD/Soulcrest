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
    public bool DailyLimitReached => DailyMonsterKina >= DailyLimit;
    /// <summary>False means a saved value for the safely bound character, awaiting fresh confirmation.</summary>
    public bool DailyConfirmed { get; init; } = true;
    public string? KinaIconPath { get; init; }
    public bool AnimationsEnabled { get; init; } = true;
    public RareDropNotice? RareDrop { get; init; }
    public IReadOnlyList<RareDropNotice> RareDrops { get; init; } = [];
    public bool LimitedKinaSession { get; init; }
    public double? SessionKinaPerHour { get; init; }
    public TimeSpan SessionElapsed { get; init; }
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
}
