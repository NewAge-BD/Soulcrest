namespace Soulcrest.Core.Network;

/// <summary>One own-entry crafting record. Category is the native profession code, never an actor identifier.</summary>
public sealed record CharacterCraftingProficiency(byte Category, ushort Level, long Experience, ushort SkillPoints);
