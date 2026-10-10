namespace Soulcrest.App.Tests;

/// <summary>
/// Tests that ask Windows which window lies under a point (WindowFromPoint) need a real, interactive desktop.
/// The GitHub build has none: a system window answers every point there (v0.4.0 build, 2026-10-10). They are
/// skipped on the build server and stay mandatory locally, like the OCR tests without a language pack.
/// </summary>
internal static class InteractiveDesktop
{
    internal static bool Missing => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
}
