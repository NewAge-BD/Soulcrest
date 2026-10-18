using System.Globalization;
using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

public sealed record TimerOverlayChoice(string Key, string Title, string? IconId, bool Selected, bool Available);

internal sealed record TimerOverlayCandidate(string Key, string Title, string? IconId,
    bool LegacySelected, AlertDisplayRow? Row);

/// <summary>Pure selection and countdown projection for the currency-style timer strip.</summary>
public static class TimerOverlayPresentation
{
    private static readonly string[] DefaultIds =
        ["event:catalog:shugo", "event:catalog:rift", "event:catalog:invasion", "event:catalog:abyss"];

    internal static string[]? NormalizeIds(IEnumerable<string>? ids) => ids is null ? null
        : ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();

    internal static void NormalizeSettings(AppSettings settings)
    {
        settings.TimerOverlayTimerIds = NormalizeIds(settings.TimerOverlayTimerIds);
        settings.TimerOverlayGap = Math.Clamp(settings.TimerOverlayGap, 4, 48);
        settings.TimerOverlayLeadMinutes = Math.Clamp(settings.TimerOverlayLeadMinutes, 0, 10080);
    }

    internal static IReadOnlyList<TimerOverlayChoice> Choices(IReadOnlyList<TimerOverlayCandidate> candidates,
        IEnumerable<string>? ids)
    {
        var selected = Selection(candidates, ids);
        return candidates.Select(candidate => new TimerOverlayChoice(candidate.Key, candidate.Title,
            candidate.IconId, selected.Contains(candidate.Key), candidate.Row is not null)).ToArray();
    }

    internal static IReadOnlyList<AlertDisplayRow> Rows(IReadOnlyList<TimerOverlayCandidate> candidates,
        IEnumerable<string>? ids, DateTimeOffset now, int leadMinutes)
    {
        var selected = Selection(candidates, ids);
        var lead = TimeSpan.FromMinutes(Math.Clamp(leadMinutes, 0, 10080));
        // Keep slots stable while countdowns change; schedule order does not move icons every tick.
        return candidates.Where(candidate => selected.Contains(candidate.Key))
            .Select(candidate => candidate.Row).OfType<AlertDisplayRow>()
            .Where(row => leadMinutes <= 0 || row.Started || row.At - now <= lead)
            .OrderBy(row => Order(row.Key)).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }

    internal static int Order(string key) => key switch
    {
        "event:catalog:shugo" => 0, "event:catalog:rift" => 1,
        "event:catalog:invasion" => 2, "event:catalog:abyss" => 3,
        "event:catalog:daily-reset" => 4, "event:catalog:weekly-reset" => 5,
        _ => key.StartsWith("boss:", StringComparison.Ordinal) ? 6 : 7
    };

    private static HashSet<string> Selection(IReadOnlyList<TimerOverlayCandidate> candidates,
        IEnumerable<string>? ids)
    {
        if (ids is not null) return new HashSet<string>(NormalizeIds(ids)!, StringComparer.Ordinal);
        var legacy = candidates.Where(candidate => candidate.LegacySelected).Select(candidate => candidate.Key).ToArray();
        return new HashSet<string>(legacy.Length > 0 ? legacy : DefaultIds, StringComparer.Ordinal);
    }

    internal static TimerOverlayCandidate FromEvent(EventTimerDefinition definition, EventTimerSnapshot? timer)
    {
        var key = "event:" + EventCatalog.ReminderId(definition.Id);
        var title = UiText.T(definition.Title);
        if (timer is null) return new(key, title, definition.Id, false, null);
        var display = EventTimerDisplay.From(timer);
        return new(key, display.Title, definition.Id, timer.Presentation.ShowOverlay,
            new(key, display.Title, "", definition.Color, display.CountdownAt, display.IsActive,
                CountdownLabel: display.CountdownLabel, IconId: definition.Id,
                InlineLabel: definition.Id == "rift" && display.IsActive ? display.Title : null));
    }

    internal static TimerOverlayCandidate FromBoss(BossChoice choice, BossRushEntry? timer,
        bool legacySelected, string title, DateTimeOffset now)
    {
        var key = "boss:" + choice.Key;
        var iconId = TimerOverlayIcons.ForBoss(choice.Boss.Icon);
        if (timer is null) return new(key, title, iconId, legacySelected, null);
        DateTimeOffset at;
        try { at = now + timer.Remaining; }
        catch (ArgumentOutOfRangeException) { at = timer.Remaining < TimeSpan.Zero ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue; }
        return new(key, title, iconId, legacySelected, new(key, title, "", "#ef4444", at, timer.Spawned,
            timer.Cached || !timer.Spawned && timer.Remaining <= TimeSpan.Zero, IconId: iconId));
    }

    internal static TimerOverlayCandidate FromCustom(EventAlert alert, DateTimeOffset now)
    {
        var key = "event:" + alert.Id;
        var at = alert.Enabled && AlertSchedules.Error(alert.Schedule) is null
            ? AlertSchedules.Next(alert.Schedule, now) : null;
        return new(key, alert.Title, "custom", alert.Enabled && alert.ShowOverlay,
            at is { } next ? new(key, alert.Title, alert.Message, alert.Color, next, false, IconId: "custom") : null);
    }

    /// <summary>Compact, rounded-up time remaining; active event phases still count to their own end.</summary>
    public static string Countdown(AlertDisplayRow row, DateTimeOffset now, bool seconds = true)
    {
        var remaining = row.At - now;
        if (!row.Provisional && row.Started && row.CountdownLabel is null) return UiText.T("Jetzt");
        if (remaining <= TimeSpan.Zero) return row.Provisional ? "?" : UiText.T("Jetzt");
        var unit = seconds ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMinute;
        var rounded = (remaining.Ticks + unit - 1) / unit;
        var totalHours = seconds ? rounded / 3600 : rounded / 60;
        var days = totalHours / 24;
        if (days > 99) return UiText.F("{0} T.", days);
        var hours = totalHours % 24;
        var minutes = seconds ? rounded / 60 % 60 : rounded % 60;
        if (days > 0) return seconds
            ? UiText.F("{0}T {1:00}:{2:00}:{3:00}", days, hours, minutes, rounded % 60)
            : UiText.F("{0}T {1:00}:{2:00}", days, hours, minutes);
        return hours.ToString("00", CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture)
            + (seconds ? ":" + (rounded % 60).ToString("00", CultureInfo.InvariantCulture) : "");
    }
}
