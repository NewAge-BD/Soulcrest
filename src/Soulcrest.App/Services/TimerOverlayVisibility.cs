namespace Soulcrest.App.Services;

/// <summary>Compatibility entry point; all freely placed panels now share one visibility policy.</summary>
internal static class TimerOverlayVisibility
{
    internal static bool IsSuppressed(AppSettings settings, bool trackingRunning, bool minimapFound,
        bool worldMapOpen, bool escMenuOpen, bool inInstance) =>
        OverlayVisibilityPolicy.IsSuppressed(settings, "alerts",
            new(trackingRunning, minimapFound, worldMapOpen, escMenuOpen, inInstance));
}
