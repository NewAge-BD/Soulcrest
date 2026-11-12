namespace Soulcrest.App.Components;

/// <summary>Plain display text and exact identity for an item-bearing selection.</summary>
public sealed record ItemSelectOption(int Value, int? ItemId, string Text, string? Detail = null);
