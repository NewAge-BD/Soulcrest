using Soulcrest.App.OverlayEditorKit;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Soulcrest.App.Components.OverlayKit;

public partial class OriginalKitOverlayEditor
{
    private string _editingOverlayId = "";
    private bool _windowBusy;
    private OverlayInstance? _windowToDelete;
    private string SelectedOverlayName => Overlay.Overlays.FirstOrDefault(window => window.Id == _editingOverlayId)?.Name ?? "Overlay";
    private bool WindowActionsBusy => _disposed || _saving || _windowBusy || _confirmingLayout ||
        _pendingLayoutChange is not null || _templateSaveOpen ||
        _templateBusy || _templateToDelete is not null || _windowToDelete is not null;

    private void LoadSelectedOverlay()
    {
        _editingOverlayId = Overlay.SelectedOverlayId;
        _settings = OverlayLayout.Normalize(Overlay.Settings);
        _selectedId = null;
        _itemSearch = "";
        _error = null;
        RefreshPresentationLease();
    }

    private async Task ReloadOverlays()
    {
        if (_disposed || _windowBusy || _saving) return;
        _windowBusy = true;
        try
        {
            var result = await Overlay.ReloadAsync();
            if (result.Succeeded) LoadSelectedOverlay();
            else _error = result.Error;
        }
        finally { _windowBusy = false; }
    }

    private async Task ChangeWindow(Func<Task<OverlaySaveResult>> action)
    {
        if (WindowActionsBusy) return;
        _windowBusy = true;
        _error = null;
        try
        {
            var result = await action();
            if (result.Succeeded) LoadSelectedOverlay();
            else _error = result.Error;
        }
        catch (Exception exception) { _error = F("Das Overlay-Fenster konnte nicht geändert werden: {0}", exception.Message); }
        finally { _windowBusy = false; }
    }

    private Task SelectOverlay(ChangeEventArgs e) => ChangeWindow(() => Overlay.SelectOverlayAsync(Text(e)));

    private Task CreateOverlay() => ChangeWindow(() => Overlay.CreateOverlayAsync(NextOverlayName("Overlay")));

    private Task DuplicateOverlay() => ChangeWindow(() => Overlay.CreateOverlayAsync(
        NextOverlayName(SelectedOverlayName, copy: true), _editingOverlayId));

    private Task RenameOverlay(ChangeEventArgs e) => ChangeWindow(() => Overlay.RenameOverlayAsync(_editingOverlayId, Text(e)));

    private string NextOverlayName(string name, bool copy = false)
    {
        for (var index = copy ? 1 : 2; ; index++)
        {
            var suffix = copy ? index == 1 ? T(" (Kopie)") : F(" (Kopie {0})", index) : $" {index}";
            var candidate = name[..Math.Min(name.Length, 60 - suffix.Length)] + suffix;
            if (!Overlay.Overlays.Any(window => string.Equals(window.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private async Task RequestOverlayDelete()
    {
        if (WindowActionsBusy || Overlay.Overlays.Count <= 1) return;
        _windowToDelete = Overlay.Overlays.FirstOrDefault(window => window.Id == _editingOverlayId);
        _error = null;
        StateHasChanged();
        try { await JS.InvokeVoidAsync("soulcrestOverlayDialogs.showDialog", "overlay-window-delete"); }
        catch (JSException exception)
        {
            _windowToDelete = null;
            _error = F("Die Bestätigung konnte nicht geöffnet werden: {0}", exception.Message);
        }
    }

    private async Task CloseOverlayDelete()
    {
        if (_windowBusy) return;
        _windowToDelete = null;
        await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-window-delete");
    }

    private async Task DeleteOverlay()
    {
        if (_disposed || _windowBusy || _saving || _windowToDelete is not { } window) return;
        _windowBusy = true;
        _error = null;
        try
        {
            var result = await Overlay.DeleteOverlayAsync(window.Id);
            if (result.Succeeded)
            {
                _windowToDelete = null;
                LoadSelectedOverlay();
                await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-window-delete");
            }
            else _error = result.Error;
        }
        catch (Exception exception) { _error = F("Das Overlay-Fenster konnte nicht gelöscht werden: {0}", exception.Message); }
        finally { _windowBusy = false; }
    }
}
