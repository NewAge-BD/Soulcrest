using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Timed reminders use their own compact layout and remain independent of HUD suppression.</summary>
public sealed class AlertNotificationsForm : AlertsOverlayForm
{
    public AlertNotificationsForm(AlertsService alerts, SettingsService settings)
        : base(alerts, settings, notification: true) { }
}
