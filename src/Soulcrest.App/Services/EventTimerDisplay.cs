using Soulcrest.Core.Alerts;

namespace Soulcrest.App.Services;

/// <summary>Display-only event phases; calendar rules and reminder occurrences remain unchanged.</summary>
public sealed record EventTimerDisplay(string Title, string PhaseLabel, string CountdownLabel,
    DateTimeOffset CountdownAt, bool IsActive, EventWindow Window)
{
    public static EventTimerDisplay From(EventTimerSnapshot timer)
    {
        var window = timer.Window;
        if (timer.Definition.Id == "rift")
        {
            // User request 2026-10-18: show only the portal's ten-minute opening, then the next rift.
            // The sixty-minute source event remains available to calendars and reminder policies.
            if (window.Phase == EventWindowPhase.Active && window.NextAt is { } next)
            {
                var end = PortalEnd(next, timer.Definition.PortalMinutes);
                window = new(next, end, end, next, EventWindowPhase.Upcoming);
            }
            else if (window.PortalEndsAt is { } portalEnd)
                window = window with { EndsAt = portalEnd };
        }
        var portal = window.Phase == EventWindowPhase.PortalOpen;
        var title = timer.Definition.Id == "rift" && portal ? "Spacetime Rift offen" : timer.Definition.Title;
        var phase = portal ? "Portal offen" : window.IsActive ? "Läuft" : "Als Nächstes";
        var label = portal ? "Portal schließt in" : window.IsActive ? "Endet in" : "Startet in";
        var at = portal ? window.PortalEndsAt ?? window.EndsAt : window.IsActive ? window.EndsAt : window.StartsAt;
        return new(UiText.T(title), UiText.T(phase), UiText.T(label), at, window.IsActive, window);
    }

    private static DateTimeOffset PortalEnd(DateTimeOffset start, int minutes)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        return DateTimeOffset.MaxValue - start < duration ? DateTimeOffset.MaxValue : start + duration;
    }
}
