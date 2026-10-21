namespace Soulcrest.App.Services;

/// <summary>Per-boss reminder choice; timer selection and the global master switch remain separate.</summary>
public sealed record BossReminderPreference
{
    public bool Enabled { get; init; } = true;
    public int LeadSeconds { get; init; } = 60;
    public int[]? LeadSecondsList { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int[] WarningSeconds => Soulcrest.Core.Alerts.AdvanceWarnings.Resolve(LeadSecondsList, LeadSeconds, 3600);
}
