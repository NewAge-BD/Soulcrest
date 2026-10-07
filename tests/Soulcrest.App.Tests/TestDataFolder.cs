using System.Runtime.CompilerServices;

namespace Soulcrest.App.Tests;

/// <summary>
/// Every test of this assembly works on a temporary data folder, never on the user's
/// %LOCALAPPDATA%\Soulcrest: tests like MapTargetsTests delete files there, and test order decided
/// before whether the redirect of another test class was already in place.
/// </summary>
internal static class TestDataFolder
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOULCREST_DATA")))
            return;
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("SOULCREST_DATA", folder);
    }
}
