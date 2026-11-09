namespace Soulcrest.App.Components;

/// <summary>An exact catalogue relationship to open, independently of the user's current progress tier.</summary>
public sealed record CombatAchievementNavigationRequest(int FamilyId, int? StageId = null);
