namespace Soulcrest.App.Services;

/// <summary>Per-boss reminder choice; timer selection and the global master switch remain separate.</summary>
public sealed record BossReminderPreference
{
    public bool Enabled { get; init; } = true;
    public int LeadSeconds { get; init; } = 60;
}
