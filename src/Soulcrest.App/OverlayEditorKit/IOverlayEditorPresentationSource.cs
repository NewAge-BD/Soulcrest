namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Separate read-only live/example presentation; layouts never own or start tracking.</summary>
public interface IOverlayEditorPresentationSource : IDisposable
{
    event Action? Changed;
    OverlaySnapshot Capture(bool examples = false, string? language = null);
    /// <summary>A visible editor keeps one shared clock. Examples and the library never acquire online prices.</summary>
    IDisposable Acquire(bool examples = false, bool market = false);
}
