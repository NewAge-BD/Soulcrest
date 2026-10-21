using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>A live-desktop switch alone must never enable capture diagnostics.</summary>
public sealed class DiagnosticOptInTests
{
    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(null, "1", false, true)]
    [InlineData("0", "1", true, true)]
    [InlineData("1", null, false, false)]
    [InlineData("1", null, true, true)]
    [InlineData("1", "0", true, true)]
    [InlineData("1", "1", true, false)]
    [InlineData("true", "true", false, true)]
    public void LiveDesktopDiagnosticsRequireBothExplicitSwitches(string? enabled, string? liveEnabled, bool liveDesktop, bool skipped)
    {
        var reason = TestPrerequisites.DiagnosticSkipReason(enabled, liveEnabled, liveDesktop);
        Assert.Equal(skipped, reason is not null);
        if (enabled != "1") Assert.Contains("SOULCREST_RUN_DIAGNOSTICS=1", reason);
        else if (skipped) Assert.Contains("SOULCREST_RUN_LIVE_DIAGNOSTICS=1", reason);
    }
}
