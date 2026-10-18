namespace Soulcrest.App.Services;

/// <summary>Timer-strip context permissions only; normal open-world play needs no extra permission.</summary>
internal static class TimerOverlayVisibility
{
    internal static bool IsSuppressed(AppSettings settings, bool trackingRunning, bool minimapFound,
        bool worldMapOpen, bool escMenuOpen, bool inInstance)
    {
        // Stopped tracking cannot establish a menu, world-map or instance context reliably.
        if (!trackingRunning) return false;
        // Instance inference deliberately stands apart from generic minimap loss: selecting Instances
        // must work without a minimap. An explicit ESC menu still needs its own permission as well.
        var menu = escMenuOpen || !minimapFound && !worldMapOpen && !inInstance;
        return worldMapOpen && !settings.TimerOverlayShowWorldMap
            || menu && !settings.TimerOverlayShowMenu
            || inInstance && !settings.TimerOverlayShowInstances;
    }
}
