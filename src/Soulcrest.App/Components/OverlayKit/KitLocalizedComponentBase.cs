using System.Globalization;
using Microsoft.AspNetCore.Components;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components.OverlayKit;

public abstract class KitLocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] private IServiceProvider LocalizationServices { get; set; } = default!;
    private SettingsService? _settings;
    private string? _lastLanguage;
    private string? _lastTheme;
    private bool _disposed;
    protected virtual string UiLanguage => UiText.Language;
    protected CultureInfo UiCulture => CultureInfo.GetCultureInfo(UiLanguage == "de" ? "de-DE" : "en-US");
    protected string T(string? source) => UiText.T(source);
    protected string F(string source, params object?[] arguments) => UiText.F(source, arguments);
    protected override void OnInitialized()
    {
        _settings = LocalizationServices.GetService(typeof(SettingsService)) as SettingsService;
        _lastLanguage = UiText.Language;
        _lastTheme = _settings?.Current.OverlayTheme;
        if (_settings is not null) _settings.Changed += LanguageChanged;
    }
    private void LanguageChanged()
    {
        var languageChanged = _lastLanguage != UiText.Language;
        var themeChanged = _lastTheme != _settings?.Current.OverlayTheme;
        if (!languageChanged && !themeChanged) return;
        _lastLanguage = UiText.Language;
        _lastTheme = _settings?.Current.OverlayTheme;
        if (!_disposed) _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            if (languageChanged) OnUiLanguageChanged();
            if (themeChanged) OnUiThemeChanged();
            StateHasChanged();
        });
    }
    protected virtual void OnUiLanguageChanged() { }
    protected virtual void OnUiThemeChanged() { }
    public virtual void Dispose()
    {
        _disposed = true;
        if (_settings is not null) _settings.Changed -= LanguageChanged;
        GC.SuppressFinalize(this);
    }
}
