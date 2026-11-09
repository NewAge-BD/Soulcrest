namespace Soulcrest.App.OverlayEditorKit;

// Original editor contract, without tracking, native runtime or global shortcuts.
public sealed record OverlayRuntimeState
{
    public bool Previewing { get; init; }
    public bool IsVisible { get; init; }
    public string Status { get; init; } = "Editor-Vorschau bereit.";
    public bool IsError { get; init; }
}

public sealed record OverlaySaveResult(string? Error = null)
{
    public bool Succeeded => Error is null;
}

public interface IOverlayService : IDisposable
{
    event Action? Changed;
    OverlaySettings Settings { get; }
    string? LoadError { get; }
    Task<OverlaySaveResult> ReloadAsync();
    OverlayRuntimeState State { get; }
    IReadOnlyList<OverlayInstance> Overlays { get; }
    string SelectedOverlayId { get; }
    OverlayRuntimeState GetState(string id);
    void SetRuntimeState(string id, bool visible, string? error = null);
    Task<OverlaySaveResult> SelectOverlayAsync(string id);
    Task<OverlaySaveResult> CreateOverlayAsync(string name, string? duplicateId = null);
    Task<OverlaySaveResult> RenameOverlayAsync(string id, string name);
    Task<OverlaySaveResult> DeleteOverlayAsync(string id);
    IReadOnlyList<OverlayTemplate> Templates { get; }
    string? TemplateError { get; }
    Task<OverlaySaveResult> SaveTemplateAsync(string name, OverlaySettings layout, string? replaceId = null);
    Task<OverlaySaveResult> DeleteTemplateAsync(string id);
    Task<OverlaySaveResult> SaveAsync(OverlaySettings settings);
    Task<OverlaySaveResult> SaveAsync(string id, OverlaySettings settings);
    Task<OverlaySaveResult> SavePositionAsync(double x, double y, double? width = null, double? height = null);
    Task<OverlaySaveResult> SavePositionAsync(string id, double x, double y, double? width = null, double? height = null);
    Task SetPreviewAsync(bool enabled);
    Task SetPreviewAsync(string id, bool enabled);
    Task<OverlaySaveResult> SavePlacementAsync(string id, string? monitorId, double x, double y, double scale);
    Task ResetPositionAsync();
}
