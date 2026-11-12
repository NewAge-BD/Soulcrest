using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AppThemeRuntimeTests
{
    [Fact]
    public void LateAcknowledgementsCannotConfirmAnEarlierThemeOrDocument()
    {
        var runtime = new AppThemeRuntime();
        var first = runtime.Request(AppThemeDefinition.DarkId);
        runtime.ObserveNative(first.Revision);
        var second = runtime.Request(AppThemeDefinition.DefaultId);
        runtime.ObserveWeb(first.Revision, AppThemeDefinition.DarkId);
        Assert.Null(runtime.Snapshot.WebAppliedId);
        runtime.ObserveNative(second.Revision);
        runtime.InvalidateWeb();
        runtime.ObserveWeb(second.Revision, AppThemeDefinition.DefaultId);
        Assert.Null(runtime.Snapshot.WebAppliedId);
        var page = runtime.Snapshot;
        runtime.ObserveWeb(page.Revision, AppThemeDefinition.DefaultId);
        Assert.Equal(AppThemeDefinition.DefaultId, runtime.Snapshot.WebAppliedId);
        Assert.Equal(AppThemeDefinition.DefaultId, runtime.Snapshot.NativeAppliedId);
    }

    [Fact]
    public void FailedOrMismatchingApplicationRemainsVisibleUntilConfirmed()
    {
        var runtime = new AppThemeRuntime();
        var request = runtime.Request(AppThemeDefinition.DarkId);
        runtime.ObserveWeb(request.Revision, AppThemeDefinition.DefaultId);
        Assert.Equal("ThemeAcknowledgementMismatch", runtime.Snapshot.WebError);
        Assert.Null(runtime.Snapshot.AppliedAtUtc);
        runtime.ObserveWeb(request.Revision, null, "InvalidOperationException");
        Assert.Equal("InvalidOperationException", runtime.Snapshot.WebError);
        runtime.ObserveWeb(request.Revision, AppThemeDefinition.DarkId);
        Assert.Null(runtime.Snapshot.WebError);
        Assert.NotNull(runtime.Snapshot.AppliedAtUtc);
    }

    [Fact]
    public void ThemeDiagnosisReadsOnlyExistingStateAndRequiresBothApplicationAcknowledgements()
    {
        var settings = new SettingsService();
        var previous = settings.Current.AppTheme;
        settings.Current.AppTheme = AppThemeDefinition.DarkId;
        try
        {
            var registry = new DiagnosticServiceRegistry(settings);
            var contributor = DiagnosticContributors.Create(registry).Single(row => row.FeatureIds.Contains("app-themes"));
            Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("app-themes").Status);
            var runtime = new AppThemeRuntime();
            registry.Add(runtime);
            var request = runtime.Request(AppThemeDefinition.DarkId);
            runtime.ObserveNative(request.Revision);
            Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("app-themes").Status);
            runtime.ObserveWeb(request.Revision, AppThemeDefinition.DarkId);
            var healthy = contributor.Collect("app-themes");
            Assert.Equal(DiagnosticStatus.Healthy, healthy.Status);
            Assert.Equal(AppThemeDefinition.DarkId, healthy.Facts["selectedThemeId"]);
            Assert.Equal(AppThemeDefinition.DarkId, healthy.Facts["webAppliedThemeId"]);
            Assert.Equal(true, healthy.Facts["mainWindowOnly"]);
            Assert.DoesNotContain(AppPaths.DataDirectory, JsonSerializer.Serialize(healthy));
            runtime.InvalidateWeb();
            Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("app-themes").Status);
            runtime.ObserveNative(runtime.Snapshot.Revision, "COMException");
            Assert.Equal(DiagnosticStatus.Error, contributor.Collect("app-themes").Status);
        }
        finally { settings.Current.AppTheme = previous; }
    }

    [Fact]
    public void SuccessfulNativeAcknowledgementClearsThePreviousNativeFailure()
    {
        var runtime = new AppThemeRuntime();
        var request = runtime.Request(AppThemeDefinition.DarkId);
        runtime.ObserveWeb(request.Revision, AppThemeDefinition.DarkId);
        runtime.ObserveNative(request.Revision, "DwmThemeApplyFailed");
        Assert.Null(runtime.Snapshot.NativeAppliedId);
        Assert.Equal("DwmThemeApplyFailed", runtime.Snapshot.NativeError);

        runtime.ObserveNative(request.Revision);

        Assert.Equal(AppThemeDefinition.DarkId, runtime.Snapshot.NativeAppliedId);
        Assert.Null(runtime.Snapshot.NativeError);
        Assert.Equal(AppThemeDefinition.DarkId, runtime.Snapshot.WebAppliedId);
        Assert.NotNull(runtime.Snapshot.AppliedAtUtc);
    }

    [Fact]
    public void FailedThemeSaveIsDiagnosedAnonymouslyAndSuccessfulRetryRequiresFreshApplicationAcknowledgements()
    {
        var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Assert.False(string.IsNullOrWhiteSpace(redirected));
        Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.GetFullPath(userData), Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(AppPaths.SettingsFile));
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var previousLanguage = UiText.Language;
        try
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings
            {
                SettingsRevision = 5, MonsterLootAutoSessionRevision = 1,
                AppTheme = AppThemeDefinition.DefaultId, UiLanguage = "en"
            });
            var settings = new SettingsService();
            var saved = File.ReadAllBytes(AppPaths.SettingsFile);
            var runtime = new AppThemeRuntime();
            var previousRequest = runtime.Request(settings.Current.AppTheme);
            runtime.ObserveNative(previousRequest.Revision);
            runtime.ObserveWeb(previousRequest.Revision, settings.Current.AppTheme);
            var registry = new DiagnosticServiceRegistry(settings, runtime);
            var contributor = DiagnosticContributors.Create(registry).Single(row => row.FeatureIds.Contains("app-themes"));
            Assert.Equal(DiagnosticStatus.Healthy, contributor.Collect("app-themes").Status);
            File.Delete(AppPaths.SettingsFile);
            Directory.CreateDirectory(AppPaths.SettingsFile);

            var error = Record.Exception(() => settings.SetAppTheme(AppThemeDefinition.DarkId));

            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.NotNull(error);
            var failed = contributor.Collect("app-themes");
            Assert.Equal(DiagnosticStatus.Error, failed.Status);
            Assert.Equal(error.GetType().Name, Assert.IsType<string>(failed.Facts["saveError"]));
            Assert.Equal(AppThemeDefinition.DefaultId, failed.Facts["selectedThemeId"]);
            var diagnosticJson = JsonSerializer.Serialize(failed);
            Assert.DoesNotContain(AppPaths.DataDirectory, diagnosticJson);
            Assert.DoesNotContain(AppPaths.SettingsFile, diagnosticJson);
            Assert.True(Directory.Exists(AppPaths.SettingsFile));

            Directory.Delete(AppPaths.SettingsFile);
            File.WriteAllBytes(AppPaths.SettingsFile, saved);
            settings.SetAppTheme(AppThemeDefinition.DarkId);
            Assert.Null(settings.AppThemeSaveError);
            var retried = runtime.Request(settings.Current.AppTheme);
            Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("app-themes").Status);
            runtime.ObserveNative(retried.Revision);
            Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("app-themes").Status);
            runtime.ObserveWeb(retried.Revision, settings.Current.AppTheme);
            var savedAfterRetry = File.ReadAllBytes(AppPaths.SettingsFile);
            var recovered = contributor.Collect("app-themes");
            Assert.Equal(DiagnosticStatus.Healthy, recovered.Status);
            Assert.Null(recovered.Facts["saveError"]);
            Assert.Equal(savedAfterRetry, File.ReadAllBytes(AppPaths.SettingsFile));
        }
        finally
        {
            if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = previousLanguage;
        }
    }

    [Fact]
    public void ThemeSelectionAddsOnlyItsUiAndStorageDependencies()
    {
        var ids = DiagnosticFeatureCatalog.Resolve(["app-themes"]).Select(row => row.Id).ToArray();
        Assert.Equal(new[] { "system", "settings-storage", "ui", "app-themes" }, ids);
    }
}
