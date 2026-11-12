using System.Collections.Immutable;

namespace Soulcrest.App.Services;

public enum CraftingProgressSource { Other, Gathering, Craft, Market }

/// <summary>A frozen plan demand, never an observed inventory or an expected attempt count used as a goal.</summary>
public sealed record CraftingProgressPlanItem(int ItemId, decimal? RequiredQuantity, bool IsCombo)
{
    public decimal? RemainingQuantity { get; init; }
    public bool IsCovered { get; init; }
}

/// <summary>Only the explicit pin and confirmed receipt counter survive restart. Transient events do not.</summary>
public sealed record CraftingProgressPin
{
    public string ProfileId { get; init; } = "";
    public string PinId { get; init; } = "";
    public int TargetItemId { get; init; }
    public int TargetQuantity { get; init; } = 1;
    public long ConfirmedQuantity { get; init; }
    public DateTimeOffset PinnedAt { get; init; }
    public CraftingScenarioDraft Scenario { get; init; } = new();
    public ImmutableArray<CraftingProgressPlanItem> PlanItems { get; init; } = [];
    public ImmutableArray<string> SeenReceipts { get; init; } = [];
    public bool ReceiptCapacityReached { get; init; }
}

public sealed record CraftingProgressRow
{
    public int ItemId { get; init; }
    public string NameDe { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string? Icon { get; init; }
    public bool IsCombo { get; init; }
    public long Quantity { get; init; }
    public DateTimeOffset LastReceivedAt { get; init; }
    public ImmutableDictionary<CraftingProgressSource, long> SourceQuantities { get; init; }
        = ImmutableDictionary<CraftingProgressSource, long>.Empty;
    public decimal? RequiredQuantity { get; init; }
    public long? KnownQuantity { get; init; }
    public bool InventoryFresh { get; init; }
    public bool InventoryComplete { get; init; }
    public bool IsManualStock { get; init; }
    public bool IsTarget { get; init; }
    public bool DemandIsFresh { get; init; } = true;
    public bool HasOpenDemand { get; init; }
    public double EventAgeSeconds { get; init; }
    public bool IsComplete => !IsManualStock && InventoryFresh && InventoryComplete && DemandIsFresh && !HasOpenDemand
        && RequiredQuantity is > 0 && KnownQuantity >= RequiredQuantity;
    public double? CoverageFraction => RequiredQuantity is > 0 && KnownQuantity is >= 0
        ? Math.Clamp((double)(KnownQuantity.Value / RequiredQuantity.Value), 0, IsComplete ? 1 : .999) : null;
}

public sealed record CraftingProgressSnapshot
{
    public string? ProfileId { get; init; }
    public CraftingProgressPin? Pinned { get; init; }
    public string TargetNameDe { get; init; } = "";
    public string TargetNameEn { get; init; } = "";
    public string? TargetIcon { get; init; }
    public bool TargetIsCombo { get; init; }
    public bool IsFresh { get; init; }
    public bool PersistentGoal { get; init; } = true;
    public bool IsCompletionVisible { get; init; }
    public double CompletionAgeSeconds { get; init; }
    public bool IsHistorical { get; init; }
    public bool PlanIsFresh { get; init; }
    public bool PlanBusy { get; init; }
    public ImmutableArray<CraftingProgressRow> Rows { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset? LastReceiptAt { get; init; }
    public int IgnoredReceipts { get; init; }
    public long Revision { get; init; }
    public bool HasContent => Pinned is not null && (PersistentGoal || !Rows.IsDefaultOrEmpty || IsCompletionVisible);
    public bool IsComplete => Pinned is { } pin && pin.ConfirmedQuantity >= pin.TargetQuantity;
    public static CraftingProgressSnapshot Empty { get; } = new();
}
