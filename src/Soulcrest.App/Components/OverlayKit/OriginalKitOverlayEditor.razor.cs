using System.Globalization;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using OverlayLayout = Soulcrest.App.OverlayEditorKit.OverlayLayout;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Soulcrest.App.Components.OverlayKit;

public partial class OriginalKitOverlayEditor
{
    [Inject] private IServiceProvider EditorServices { get; set; } = default!;
    [Parameter] public string? Section { get; set; }
    [Parameter] public EventCallback<string> SectionChanged { get; set; }
    private static readonly MenuSection[] Sections =
    [
        new("layout", "Layout", "edit", "/?tab=overlay-editor&section=layout"),
        new("display", "Anzeige & Verhalten", "settings", "/?tab=overlay-editor&section=display"),
        new("presets", "Overlay Presets", "spark", "/?tab=overlay-editor&section=presets"),
        new("static", "Static Overlays", "monitor", "/?tab=overlay-editor&section=static"),
    ];
    private string SelectedSection => Section is "windows" or "templates" ? "presets"
        : Sections.Any(section => section.Id == Section) ? Section! : "layout";

    private void SelectSection(string section)
    {
        if (section is "windows" or "templates") section = "presets";
        if (WindowActionsBusy || section == SelectedSection || !Sections.Any(candidate => candidate.Id == section)) return;
        NavigateToSection(section);
    }

    private void NavigateToSection(string section)
    {
        Section = section;
        _previewModule = null;
        RefreshPresentationLease();
        _ = SectionChanged.InvokeAsync(section);
    }

    private OverlaySettings _settings = new();
    private string? _selectedId, _error;
    private bool _saving, _disposed;
    private SettingsService? _themeSettings;
    private bool _savingTheme;
    private string? _themeError;
    private bool _startingPlaceMode, _changingActivation;
    private OverlayPlacementService? _placement;
    private OverlayRuntimeState WindowRuntime => Overlay.GetState(_editingOverlayId);
    private bool PlaceModeActive => _placement?.Active == true;
    private bool PlaceModeBusy => _startingPlaceMode || _placement?.IsEnding == true;
    // A start may wait for queued saves. Other modal/window actions must finish first.
    private bool PlaceModeButtonDisabled => _disposed || PlaceModeBusy || _placement is null || _windowBusy
        || _confirmingLayout || _pendingLayoutChange is not null || _templateSaveOpen || _templateBusy
        || _templateToDelete is not null || _windowToDelete is not null || !PlaceModeActive && Overlay.LoadError is not null;
    private bool ActivationButtonDisabled => _disposed || _changingActivation || _startingPlaceMode
        || _placement?.IsEnding == true || _windowBusy || _confirmingLayout || _pendingLayoutChange is not null
        || _templateSaveOpen || _templateBusy || _templateToDelete is not null || _windowToDelete is not null
        || Overlay.LoadError is not null;
    private bool _demo;
    private string _itemSearch = "";
    private OverlaySnapshot _liveSnapshot = new(), _exampleSnapshot = new();
    private IDisposable? _presentationLease;
    private (bool Examples, bool Market)? _presentationMode;
    private DotNetObjectReference<OriginalKitOverlayEditor>? _reference;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _revision;
    private sealed record LayoutChange(string? Preset, OverlayTemplate? Template = null);
    private LayoutChange? _pendingLayoutChange;
    private bool _confirmingLayout;
    private bool PendingClearLayout => _pendingLayoutChange?.Preset is null && _pendingLayoutChange?.Template is null;
    private string LayoutConfirmationTitle => PendingClearLayout ? T("Layout leeren?") : T("Vorlage anwenden?");
    private string LayoutConfirmationAction => PendingClearLayout ? T("Layout leeren") : T("Vorlage anwenden");
    private string PendingPresetLabel => _pendingLayoutChange?.Template?.Name
        ?? T(OverlayEditorDefaults.All.FirstOrDefault(preset => preset.Id == _pendingLayoutChange?.Preset)?.Label ?? "Beispiellayout");
    private static IReadOnlyList<OverlayWidgetDefinition> Modules => OverlayCatalog.Widgets;
    private sealed record ModuleCategory(string Id, string Label);
    private static readonly ModuleCategory[] ModuleCategories =
    [
        new("all", "Alle"), new("drops", "Drops"), new("kina", "Kina"), new("ap", "AP"),
        new("rank", "Rangpunkte"), new("time", "Zeit"), new("notices", "Hinweise"), new("pets", "Pets"),
    ];
    private string _moduleCategory = "all", _moduleSearch = "";
    private static string CategoryForModule(string kind) => kind switch
    {
        "drop-grid" or "drop-strip" or "drop-list" => "drops",
        "market-kina" or "dropped-kina" or "daily-kina" or "market-kina-hour" or "dropped-kina-hour" => "kina",
        "abyss-points" or "abyss-points-hour" => "ap",
        "duration" or "clock" => "time",
        "rare-notice" => "notices",
        "genus-insight" => "pets",
        _ => "rank",
    };
    private static int ModuleCategoryCount(string category) => category == "all" ? Modules.Count
        : Modules.Count(module => CategoryForModule(module.Kind) == category);
    private IReadOnlyList<OverlayWidgetDefinition> VisibleModules => Modules
        .Where(module => (_moduleCategory == "all" || CategoryForModule(module.Kind) == _moduleCategory) && ModuleMatchesSearch(module))
        .OrderBy(module => Array.FindIndex(ModuleCategories, category => category.Id == CategoryForModule(module.Kind)))
        .ToArray();
    private bool ModuleMatchesSearch(OverlayWidgetDefinition module)
    {
        var terms = _moduleSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var category = ModuleCategories.First(value => value.Id == CategoryForModule(module.Kind)).Label;
        var aliases = CategoryForModule(module.Kind) switch { "ap" => "AP Abyss Points", "rank" => "RP Rank Points", _ => "" };
        var rate = module.Kind.EndsWith("-hour", StringComparison.Ordinal) ? "/h" : "";
        var searchable = string.Join(' ', module.Kind, module.Label, T(module.Label), module.Description, T(module.Description), category, T(category), aliases, rate);
        return terms.All(term => UiCulture.CompareInfo.IndexOf(searchable, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }
    private void ChangeModuleSearch(ChangeEventArgs e) { _moduleSearch = Text(e); _previewModule = null; }
    private void SelectModuleCategory(string category)
    {
        if (!ModuleCategories.Any(value => value.Id == category)) return;
        _moduleCategory = category;
        _previewModule = null;
    }
    private bool LockSelected => _selectedId == OverlayLockWidget.Id;
    private OverlayWidget? SelectedWidget => LockSelected ? OverlayLockWidget.Layout(_settings)
        : _settings.Widgets.FirstOrDefault(w => w.Id == _selectedId);
    /// <summary>The example session, also used for the hovered module's preview while the canvas shows live data.</summary>
    private OverlaySnapshot DemoSnapshot => _exampleSnapshot;
    private OverlaySnapshot PreviewSnapshot => _demo || SelectedSection == "presets" ? _exampleSnapshot : _liveSnapshot;
    private string? _previewModule;
    // A compact card preserves the module's aspect ratio and fits below the horizontal library.
    private const double ModulePreviewWidth = 280;
    private static double ModulePreviewScale(OverlayWidgetDefinition module) => Math.Min(1, ModulePreviewWidth / module.Width);
    private string ModulePreviewStyle(OverlayWidgetDefinition module) =>
        $"width:{Css(module.Width * ModulePreviewScale(module))}px;height:{Css(module.Height * ModulePreviewScale(module))}px;{OverlayEditorThemes.CssVariables(DemoSnapshot.ThemeId)}";
    private string ModulePreviewSize(OverlayWidgetDefinition module) =>
        $"width:{Css(module.Width)}px;height:{Css(module.Height)}px;transform:scale({Css(ModulePreviewScale(module))})";
    private void ShowModulePreview(string kind) { _previewModule = kind; StateHasChanged(); }
    private void HideModulePreview() { _previewModule = null; StateHasChanged(); }
    private OverlayWindowChrome Chrome => OverlayWindowChrome.For(PreviewSnapshot.ThemeId, _settings.ShowBorder);
    private string StageStyle => $"width:{Css(Chrome.OuterWidth(_settings.Width))}px;height:{Css(Chrome.OuterHeight(_settings.Height))}px;{OverlayEditorThemes.CssVariables(PreviewSnapshot.ThemeId)}";
    private string SelectedThemeId => OverlayEditorThemes.ResolveThemeId(_themeSettings?.Current.OverlayTheme ?? PreviewSnapshot.ThemeId);
    private string ContentStyle => $"inset:{Css(Chrome.Top)}px {Css(Chrome.Right)}px {Css(Chrome.Bottom)}px {Css(Chrome.Left)}px";
    private static string WidgetStyle(OverlayWidget widget) => $"left:{Css(widget.X)}px;top:{Css(widget.Y)}px;width:{Css(widget.Width)}px;height:{Css(widget.Height)}px";
    private static string Css(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private string Percent(double value) => value.ToString("P0", UiCulture);
    private static string Text(ChangeEventArgs e) => e.Value?.ToString() ?? "";
    private static bool Checked(ChangeEventArgs e) => e.Value is true;
    private string Label(string kind) => T(OverlayCatalog.Find(kind)?.Label ?? "Modul");
    protected override void OnInitialized()
    {
        base.OnInitialized();
        _themeSettings = EditorServices.GetService(typeof(SettingsService)) as SettingsService;
        // Optional resolution keeps pure Razor renderers independent of the native host.
        _placement = EditorServices.GetService(typeof(OverlayPlacementService)) as OverlayPlacementService;
        if (_placement is not null) _placement.Changed += PlacementChanged;
        Presentation.Changed += PresentationChanged;
        LoadSelectedOverlay();
        Overlay.Changed += OverlayChanged;
    }

    protected override void OnParametersSet() => RefreshPresentationLease();

    private void RefreshSnapshots()
    {
        _liveSnapshot = SelectedSection is "static" or "presets" ? new() : Presentation.Capture(false, UiLanguage);
        _exampleSnapshot = Presentation.Capture(true, UiLanguage);
    }
    private void RefreshPresentationLease()
    {
        if (_disposed) return;
        if (SelectedSection is "static" or "presets")
        {
            _presentationLease?.Dispose();
            _presentationLease = null;
            _presentationMode = null;
            RefreshSnapshots();
            return;
        }
        var mode = (Examples: _demo, Market: !_demo && _settings.Widgets.Any(widget => widget.Kind is "market-kina" or "market-kina-hour"));
        if (_presentationMode != mode)
        {
            _presentationLease?.Dispose();
            _presentationLease = Presentation.Acquire(mode.Examples, mode.Market);
            _presentationMode = mode;
        }
        RefreshSnapshots();
    }
    private void PresentationChanged()
    {
        if (!_disposed) _ = InvokeAsync(() => { if (!_disposed) { RefreshSnapshots(); StateHasChanged(); } });
    }
    private void PlacementChanged()
    {
        if (!_disposed) _ = InvokeAsync(() => { if (!_disposed) StateHasChanged(); });
    }
    protected override void OnUiLanguageChanged() => RefreshSnapshots();
    protected override void OnUiThemeChanged() => RefreshSnapshots();

    private async Task ChangeTheme(ChangeEventArgs e)
    {
        var settings = _themeSettings;
        var id = Text(e);
        if (_disposed || _savingTheme || settings is null || !OverlayEditorThemes.All.Any(theme => theme.Id == id)
            || id == SelectedThemeId) return;
        _savingTheme = true;
        _themeError = null;
        try { await Task.Run(() => settings.SetOverlayTheme(id)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _themeError = "Overlay-Einstellungen konnten nicht gespeichert werden."; }
        finally
        {
            _savingTheme = false;
            if (!_disposed) RefreshSnapshots();
        }
    }

    private void ChangePreviewMode(ChangeEventArgs e)
    {
        _demo = Checked(e);
        RefreshPresentationLease();
    }

    private void OverlayChanged()
    {
        if (!_disposed) _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            // Native resize or a global interaction shortcut may update the
            // same settings while the editor is visible.
            if (!_saving)
            {
                if (_editingOverlayId != Overlay.SelectedOverlayId) LoadSelectedOverlay();
                else if (_error is null) _settings = OverlayLayout.Normalize(Overlay.Settings);
                else _settings = _settings with
                {
                    Enabled = Overlay.Settings.Enabled,
                    Interaction = Overlay.Settings.Interaction,
                };
            }
            RefreshPresentationLease();
            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed) return;
        _reference = DotNetObjectReference.Create(this);
        try { await JS.InvokeVoidAsync("soulcrestOverlayEditor.mount", "overlay-editor", _reference); }
        catch (JSException exception)
        {
            _error = F("Die Mausbedienung konnte nicht geladen werden. Position und Größe können weiterhin über die Felder geändert werden. {0}", exception.Message);
            StateHasChanged();
        }
    }

    private async Task Change(Func<OverlaySettings, OverlaySettings> update)
    {
        if (_disposed || _startingPlaceMode || Overlay.LoadError is not null) return;
        var overlayId = _editingOverlayId;
        var original = _settings;
        _settings = OverlayLayout.Normalize(update(_settings));
        RefreshPresentationLease();
        var revision = ++_revision;
        var draft = _settings;
        _saving = true;
        _error = null;
        await SaveChange(overlayId, original, draft, revision, saveGateHeld: false);
    }

    private async Task SaveChange(string overlayId, OverlaySettings original, OverlaySettings draft, long revision, bool saveGateHeld)
    {
        if (!saveGateHeld) await _saveGate.WaitAsync();
        try
        {
            // Moving the native window can happen while the editor is open.
            // Layout edits must retain that newly saved screen position.
            var current = Overlay.Overlays.FirstOrDefault(window => window.Id == overlayId)?.Settings;
            if (current is null)
            {
                if (revision == _revision)
                {
                    if (_editingOverlayId == overlayId && Overlay.SelectedOverlayId != overlayId) LoadSelectedOverlay();
                    _error = "Das Overlay-Fenster ist nicht mehr vorhanden.";
                }
                return;
            }
            draft = draft with
            {
                PositionX = current.PositionX, PositionY = current.PositionY,
                PositionMonitorId = current.PositionMonitorId,
                Scale = draft.Scale == original.Scale ? current.Scale : draft.Scale,
                // Global hotkeys can fire while a layout save is queued. Keep
                // their result unless this edit explicitly changed that field.
                Enabled = draft.Enabled == original.Enabled ? current.Enabled : draft.Enabled,
                Interaction = draft.Interaction == original.Interaction ? current.Interaction : draft.Interaction,
                Visibility = draft.Visibility == original.Visibility ? current.Visibility : draft.Visibility,
                CaptureExcluded = draft.CaptureExcluded == original.CaptureExcluded ? current.CaptureExcluded : draft.CaptureExcluded,
                TriggerRules = OverlayTriggerRules.Equivalent(draft.TriggerRules, original.TriggerRules) ? current.TriggerRules : draft.TriggerRules,
                LockWidget = Equals(draft.LockWidget, original.LockWidget) ? current.LockWidget : draft.LockWidget,
            };
            if (revision == _revision && _editingOverlayId == overlayId)
            {
                // Keep the newest unsaved geometry even when this write fails,
                // including behavior committed by an earlier queued action.
                _settings = draft;
                RefreshPresentationLease();
            }
            var result = await Overlay.SaveAsync(overlayId, draft);
            if (revision == _revision && _editingOverlayId == overlayId)
            {
                _error = result.Error;
                if (result.Succeeded)
                {
                    if (Overlay.SelectedOverlayId != overlayId) LoadSelectedOverlay();
                    else _settings = OverlayLayout.Normalize(Overlay.Settings);
                }
            }
        }
        catch (Exception exception) { if (revision == _revision) _error = F("Das Overlay konnte nicht gespeichert werden: {0}", exception.Message); }
        finally
        {
            if (!saveGateHeld) _saveGate.Release();
            if (revision == _revision) _saving = false;
        }
    }

    private Task ChangeNumber(ChangeEventArgs e, double minimum, double maximum, Func<OverlaySettings, double, OverlaySettings> update)
    {
        if (!TryNumber(e, minimum, maximum, out var value)) return Task.CompletedTask;
        return Change(settings => update(settings, value));
    }

    private Task ChangeWindowEnabled(string overlayId, OverlaySettings current, bool enabled, long revision) =>
        SaveChange(overlayId, current, OverlayLayout.Normalize(current with { Enabled = enabled }), revision, saveGateHeld: true);

    private Task SelectPresetWindow(string id) => ChangeWindow(() => Overlay.SelectOverlayAsync(id));

    private async Task EditPresetWindow(string id)
    {
        if (WindowActionsBusy) return;
        await SelectPresetWindow(id);
        if (_error is null && _editingOverlayId == id) NavigateToSection("layout");
    }

    private Task ChangeWindowVisibility(ChangeEventArgs e)
    {
        var value = Text(e);
        return value is "game" or "session" or "always"
            ? Change(settings => settings with { Visibility = value }) : Task.CompletedTask;
    }

    private bool TryNumber(ChangeEventArgs e, double minimum, double maximum, out double value)
    {
        if (double.TryParse(Text(e), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value) && value >= minimum && value <= maximum) return true;
        _error = F("Bitte einen Wert zwischen {0} und {1} eingeben. Die Änderung wurde nicht gespeichert.", Css(minimum), Css(maximum));
        return false;
    }

    private Task CanvasDimension(ChangeEventArgs e, bool width) => width
        ? ChangeNumber(e, 160, 1600, (s, value) => OverlayLayout.ResizeCanvas(s, value, s.Height))
        : ChangeNumber(e, 64, 1200, (s, value) => OverlayLayout.ResizeCanvas(s, s.Width, value));

    private Task ChangeWidget(Func<OverlayWidget, OverlayWidget> update)
    {
        var id = _selectedId;
        if (id == OverlayLockWidget.Id)
            return Change(settings =>
            {
                var position = update(OverlayLockWidget.Layout(settings));
                return OverlayLockWidget.WithPosition(settings, position.X, position.Y);
            });
        return Change(settings => settings with { Widgets = settings.Widgets.Select(w => w.Id == id ? update(w) : w).ToArray() });
    }

    private Task WidgetNumber(ChangeEventArgs e, string field)
    {
        if (SelectedWidget is not { } widget) return Task.CompletedTask;
        var (minimum, maximum) = field switch
        {
            "x" => (0d, (LockSelected ? _settings.Width : 1600) - widget.Width), "y" => (0d, (LockSelected ? _settings.Height : 1200) - widget.Height),
            "width" => (Math.Min(80, widget.Width), 1600 - widget.X), "height" => (Math.Min(40, widget.Height), 1200 - widget.Y),
            "fontScale" => (.7, 2d), "killFeedRows" => (1d, 10d), "itemLimit" => (1d, 24d), "itemSize" => (32d, 112d), _ => (0d, 0d),
        };
        if (!TryNumber(e, minimum, maximum, out var value)) return Task.CompletedTask;
        if ((field is "itemLimit" or "killFeedRows") && value != Math.Truncate(value))
        {
            _error = T(field == "killFeedRows" ? "Bitte eine ganze Zeilenanzahl eingeben." : "Bitte eine ganze Anzahl an Items eingeben.");
            return Task.CompletedTask;
        }
        return ChangeWidget(w => field switch
        {
            "x" => w with { X = value }, "y" => w with { Y = value },
            "width" => OverlayLayout.ResizeWidget(w, value, w.Height), "height" => OverlayLayout.ResizeWidget(w, w.Width, value),
            "fontScale" => w with { FontScale = value }, "itemLimit" => w with { ItemLimit = (int)value },
            "itemSize" => w with { ItemSize = value }, "killFeedRows" => w with { KillFeedRows = (int)value }, _ => w,
        });
    }

    [JSInvokable]
    public Task SelectWidget(string id)
    {
        if (_selectedId != id) _itemSearch = "";
        _selectedId = id == OverlayLockWidget.Id || _settings.Widgets.Any(w => w.Id == id) ? id : null;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task ClearSelection()
    {
        if (_disposed || _selectedId is null) return Task.CompletedTask;
        _selectedId = null;
        _itemSearch = "";
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task AddModule(string kind)
    {
        if (kind == OverlayLockWidget.Kind) { await SelectWidget(OverlayLockWidget.Id); return; }
        if (_settings.Widgets.Count >= OverlayCatalog.MaximumWidgets || OverlayCatalog.Find(kind) is not { } definition) return;
        var width = Math.Min(definition.Width, _settings.Width - 16);
        var height = Math.Min(definition.Height, 1200 - 16);
        for (var y = 8d; y <= 1200 - height - 8; y += 8)
        for (var x = 8d; x <= _settings.Width - width - 8; x += 8)
        {
            if (_settings.Widgets.Any(w => x < w.X + w.Width + 4 && x + width + 4 > w.X && y < w.Y + w.Height + 4 && y + height + 4 > w.Y)) continue;
            var widget = OverlayCatalog.CreateWidget(kind, x, y) with { Width = width, Height = height };
            _selectedId = widget.Id;
            await Change(s => s with { Height = Math.Max(s.Height, y + height + 8), Widgets = s.Widgets.Append(widget).ToArray() });
            return;
        }
        _error = "Für dieses Modul ist kein freier Platz vorhanden. Vergrößere das Layout oder ziehe es direkt an die gewünschte Stelle.";
    }

    [JSInvokable]
    public async Task AddModuleAt(string kind, double x, double y)
    {
        if (kind == OverlayLockWidget.Kind) { await SelectWidget(OverlayLockWidget.Id); return; }
        if (_disposed || _settings.Widgets.Count >= OverlayCatalog.MaximumWidgets || OverlayCatalog.Find(kind) is null || !double.IsFinite(x) || !double.IsFinite(y)) return;
        var widget = OverlayCatalog.CreateWidget(kind, x, y);
        _selectedId = widget.Id;
        await Change(s => s with { Widgets = s.Widgets.Append(widget).ToArray() });
        StateHasChanged();
    }

    [JSInvokable]
    public async Task CommitWidgetGeometry(string id, double x, double y, double width, double height)
    {
        if (_disposed || !new[] { x, y, width, height }.All(double.IsFinite)) return;
        _selectedId = id;
        await ChangeWidget(w => width == w.Width && height == w.Height ? w with { X = x, Y = y } :
            OverlayLayout.ResizeWidget(w, width, height) with { X = x, Y = y });
        StateHasChanged();
    }

    [JSInvokable]
    public async Task CommitCanvasSize(double width, double height)
    {
        if (_disposed || !double.IsFinite(width) || !double.IsFinite(height)) return;
        await Change(s => OverlayLayout.ResizeCanvas(s, width, height));
        StateHasChanged();
    }

    [JSInvokable]
    public async Task CommitCanvasCorner(double width, double height, string corner)
    {
        if (_disposed || !double.IsFinite(width) || !double.IsFinite(height)) return;
        await Change(s => {
            var resized = OverlayLayout.ResizeCanvas(s, width, height);
            var west = corner is "nw" or "sw";
            var north = corner is "nw" or "ne";
            if (s.Widgets.Count > 0)
            {
                // Translate the complete layout together; clamping individual
                // origins would collapse modules onto each other when shrinking.
                if (west) resized = resized with { Width = Math.Clamp(resized.Width,
                    Math.Max(160, s.Width - s.Widgets.Min(w => w.X)),
                    Math.Min(1600, s.Width + 1600 - s.Widgets.Max(w => w.X + w.Width))) };
                if (north) resized = resized with { Height = Math.Clamp(resized.Height,
                    Math.Max(64, s.Height - s.Widgets.Min(w => w.Y)),
                    Math.Min(1200, s.Height + 1200 - s.Widgets.Max(w => w.Y + w.Height))) };
            }
            var dx = west ? resized.Width - s.Width : 0;
            var dy = north ? resized.Height - s.Height : 0;
            var control = OverlayLockWidget.Bounds(s);
            return OverlayLockWidget.WithPosition(resized with
            {
                Widgets = s.Widgets.Select(w => w with { X = w.X + dx, Y = w.Y + dy }).ToArray(),
            }, control.X + dx, control.Y + dy);
        });
        StateHasChanged();
    }

    [JSInvokable]
    public async Task NudgeWidget(string id, double dx, double dy)
    {
        if (_disposed || !double.IsFinite(dx) || !double.IsFinite(dy)) return;
        _selectedId = id;
        await ChangeWidget(w => w with { X = w.X + Math.Clamp(dx, -8, 8), Y = w.Y + Math.Clamp(dy, -8, 8) });
        StateHasChanged();
    }

    private Task RemoveSelected() => RemoveWidget(_selectedId);

    private async Task RemoveWidget(string? id)
    {
        if (id is null || !_settings.Widgets.Any(widget => widget.Id == id)) return;
        if (_selectedId == id) _selectedId = null;
        await Change(s => s with { Widgets = s.Widgets.Where(w => w.Id != id).ToArray() });
    }

    private async Task ApplyPreset(string name)
    {
        if (_disposed || _saving || _confirmingLayout || _pendingLayoutChange is not null) return;
        if (_settings.Widgets.Count > 0)
        {
            await RequestLayoutConfirmation(new(name));
            return;
        }
        _selectedId = null;
        await Change(s => ApplyPresetTo(name, s));
        if (_error is null && SelectedSection != "layout") NavigateToSection("layout");
    }

    private static OverlaySettings ApplyPresetTo(string name, OverlaySettings current)
    {
        if (OverlayEditorDefaults.All.Any(preset => preset.Id == name))
            return new OverlayTemplate { Layout = OverlayEditorDefaults.CreateSettings(name) }.ApplyTo(current);
        // Keep the original neutral example compatible with saved layouts and callers.
        var example = OverlayCatalog.Preset(name);
        return current with { Width = example.Width, Height = example.Height, Widgets = example.Widgets };
    }

    private async Task ClearLayout()
    {
        if (_disposed || _saving || _confirmingLayout || _pendingLayoutChange is not null || _settings.Widgets.Count == 0) return;
        await RequestLayoutConfirmation(new(null));
    }

    private async Task RequestLayoutConfirmation(LayoutChange change)
    {
        _pendingLayoutChange = change;
        StateHasChanged();
        try { await JS.InvokeVoidAsync("soulcrestOverlayDialogs.showDialog", "overlay-layout-confirm"); }
        catch (JSException exception)
        {
            _pendingLayoutChange = null;
            _error = F("Die Bestätigung konnte nicht geöffnet werden. {0}", exception.Message);
        }
    }

    private async Task CloseLayoutConfirmation()
    {
        if (_confirmingLayout) return;
        _pendingLayoutChange = null;
        await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-layout-confirm");
    }

    private async Task ConfirmLayoutChange()
    {
        if (_disposed || _saving || _confirmingLayout || _pendingLayoutChange is not { } change) return;
        _confirmingLayout = true;
        try
        {
            _selectedId = null;
            if (change.Template is { } template)
                await Change(template.ApplyTo);
            else if (change.Preset is { } name)
            {
                await Change(s => ApplyPresetTo(name, s));
            }
            else await Change(s => s with { Widgets = [] });
            if (_error is null)
            {
                _pendingLayoutChange = null;
                await JS.InvokeVoidAsync("soulcrestOverlayDialogs.closeDialog", "overlay-layout-confirm");
                if (SelectedSection != "layout") NavigateToSection("layout");
            }
        }
        finally { _confirmingLayout = false; }
    }

    private async Task TogglePreview()
    {
        try { await Overlay.SetPreviewAsync(!Overlay.State.Previewing); }
        catch (Exception exception) { _error = F("Die Bildschirmvorschau konnte nicht geändert werden: {0}", exception.Message); }
    }

    private async Task ToggleWindowActivation()
    {
        if (ActivationButtonDisabled) return;
        var overlayId = _editingOverlayId;
        var enabled = !_settings.Enabled;
        // Activation changes metadata, not the geometry draft. Keep the pending
        // draft's revision so its failure remains visible; later gestures are newer.
        var revision = _revision;
        _saving = true;
        _changingActivation = true;
        _windowBusy = true;
        _error = null;
        var entered = false;
        try
        {
            // A completed canvas gesture may still be queued. Preserve its contents
            // and the clicked window identity before changing only activation.
            await _saveGate.WaitAsync();
            entered = true;
            if (_disposed || Overlay.LoadError is not null || _error is not null) return;
            var window = Overlay.Overlays.FirstOrDefault(candidate => candidate.Id == overlayId);
            if (window is null)
            {
                _error = "Das Overlay-Fenster ist nicht mehr vorhanden.";
                return;
            }
            if (_editingOverlayId != overlayId || Overlay.SelectedOverlayId != overlayId) return;
            await ChangeWindowEnabled(overlayId, window.Settings, enabled, revision);
        }
        finally
        {
            if (entered) _saveGate.Release();
            // Never replace a newer gesture's local draft or clear its busy state.
            if (revision == _revision)
            {
                _saving = false;
                if (_editingOverlayId == overlayId && Overlay.SelectedOverlayId != overlayId) LoadSelectedOverlay();
                else if (_editingOverlayId == overlayId && Overlay.Overlays.FirstOrDefault(window => window.Id == overlayId) is { } current)
                    _settings = _settings with { Enabled = current.Settings.Enabled };
            }
            _changingActivation = false;
            _windowBusy = false;
        }
    }

    private async Task TogglePlaceMode()
    {
        if (PlaceModeButtonDisabled) return;
        if (PlaceModeActive)
        {
            _placement!.Stop();
            return;
        }
        var overlayId = _editingOverlayId;
        _startingPlaceMode = true;
        _windowBusy = true;
        _error = null;
        await _saveGate.WaitAsync();
        try
        {
            if (_disposed || Overlay.LoadError is not null || _error is not null) return;
            if (!Overlay.Overlays.Any(window => window.Id == overlayId))
            {
                _error = "Das Overlay-Fenster ist nicht mehr vorhanden.";
                return;
            }
            // A selection changed elsewhere while the launch waited is not a request
            // to position a different window. The next click can use that selection.
            if (_editingOverlayId != overlayId || Overlay.SelectedOverlayId != overlayId) return;
            if (!_placement!.StartEditor(overlayId: overlayId))
                _error = _placement.Error ?? "Place Mode konnte nicht geöffnet werden.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException
            or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            _error = "Place Mode konnte nicht geöffnet werden.";
        }
        finally
        {
            _saveGate.Release();
            _startingPlaceMode = false;
            _windowBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        base.Dispose();
        _disposed = true;
        if (_placement is not null) _placement.Changed -= PlacementChanged;
        Presentation.Changed -= PresentationChanged;
        _presentationLease?.Dispose();
        Overlay.Changed -= OverlayChanged;
        try { await JS.InvokeVoidAsync("soulcrestOverlayEditor.unmount", "overlay-editor"); }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { }
        _reference?.Dispose();
        await _saveGate.WaitAsync();
        _saveGate.Release();
        // Preview is temporary setup, separate from the saved Enabled choice.
        foreach (var window in Overlay.Overlays)
            if (Overlay.GetState(window.Id).Previewing) await Overlay.SetPreviewAsync(window.Id, false);
        GC.SuppressFinalize(this);
    }
}
