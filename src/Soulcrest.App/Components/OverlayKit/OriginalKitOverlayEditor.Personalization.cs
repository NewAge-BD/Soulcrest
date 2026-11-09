using Soulcrest.App.OverlayEditorKit;
using Microsoft.JSInterop;

namespace Soulcrest.App.Components.OverlayKit;

public partial class OriginalKitOverlayEditor
{
    private string? _selectedTemplateId, _templateReplaceId, _templateFormError;
    private string _templateNameDraft = "";
    private OverlaySettings? _templateLayoutDraft;
    private OverlayTemplate? _templateToDelete;
    private bool _templateBusy, _templateSaveOpen;

    private OverlayTemplate? SelectedTemplate => Overlay.Templates.FirstOrDefault(t => t.Id == _selectedTemplateId);
    private OverlayTemplate? TemplateToReplace => _templateReplaceId is not null
        ? Overlay.Templates.FirstOrDefault(t => t.Id == _templateReplaceId)
        : Overlay.Templates.FirstOrDefault(t => string.Equals(t.Name, _templateNameDraft.Trim(), StringComparison.OrdinalIgnoreCase));
    private async Task OpenTemplateSave(OverlayTemplate? replace)
    {
        if (_disposed || _saving || _templateBusy) return;
        _templateReplaceId = replace?.Id;
        _templateNameDraft = replace?.Name ?? "";
        _templateLayoutDraft = OverlayLayout.Normalize(_settings);
        _templateFormError = null;
        _templateSaveOpen = true;
        await ShowFeatureDialog("overlay-template-save");
    }

    private async Task CloseTemplateSave()
    {
        if (_templateBusy) return;
        _templateSaveOpen = false;
        _templateLayoutDraft = null;
        _templateReplaceId = null;
        await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-template-save");
    }

    private async Task SaveTemplate()
    {
        if (_disposed || !_templateSaveOpen || _templateBusy || _templateLayoutDraft is not { } layout ||
            string.IsNullOrWhiteSpace(_templateNameDraft)) return;
        _templateBusy = true;
        try
        {
            var result = await Overlay.SaveTemplateAsync(_templateNameDraft, layout, _templateReplaceId ?? TemplateToReplace?.Id);
            _templateFormError = result.Error;
            if (result.Succeeded)
            {
                _selectedTemplateId = Overlay.Templates.FirstOrDefault(t =>
                    string.Equals(t.Name, _templateNameDraft.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
                _templateSaveOpen = false;
                _templateLayoutDraft = null;
                _templateReplaceId = null;
                await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-template-save");
            }
        }
        catch (Exception exception) { _templateFormError = F("Vorlage nicht gespeichert: {0}", exception.Message); }
        finally { _templateBusy = false; }
    }

    private async Task ApplySelectedTemplate()
    {
        if (_disposed || _saving || _confirmingLayout || _pendingLayoutChange is not null || SelectedTemplate is not { } template) return;
        if (_settings.Widgets.Count > 0)
        {
            await RequestLayoutConfirmation(new(null, template));
            return;
        }
        _selectedId = null;
        await Change(template.ApplyTo);
        if (_error is null && SelectedSection != "layout") NavigateToSection("layout");
    }

    private async Task RequestTemplateDelete()
    {
        if (_disposed || _templateBusy || SelectedTemplate is not { } template) return;
        _templateToDelete = template;
        _templateFormError = null;
        await ShowFeatureDialog("overlay-template-delete");
    }

    private async Task CloseTemplateDelete()
    {
        if (_templateBusy) return;
        _templateToDelete = null;
        await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-template-delete");
    }

    private async Task DeleteTemplate()
    {
        if (_disposed || _templateBusy || _templateToDelete is not { } template) return;
        _templateBusy = true;
        try
        {
            var result = await Overlay.DeleteTemplateAsync(template.Id);
            _templateFormError = result.Error;
            if (result.Succeeded)
            {
                if (_selectedTemplateId == template.Id) _selectedTemplateId = null;
                _templateToDelete = null;
                await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-template-delete");
            }
        }
        catch (Exception exception) { _templateFormError = F("Vorlage nicht gelöscht: {0}", exception.Message); }
        finally { _templateBusy = false; }
    }

    private async Task ShowFeatureDialog(string id)
    {
        StateHasChanged();
        try { await JS.InvokeVoidAsync("soulcrestOverlayDialogs.showDialog", id); }
        catch (JSException exception)
        {
            // A dialog which never opened cannot provide a cancel action. Release
            // its draft so window selection and creation remain available.
            if (id == "overlay-template-save")
            {
                _templateSaveOpen = false;
                _templateLayoutDraft = null;
                _templateReplaceId = null;
            }
            if (id == "overlay-template-delete") _templateToDelete = null;
            _error = F("Der Dialog konnte nicht geöffnet werden. {0}", exception.Message);
        }
    }
}
