using Xunit;

namespace Soulcrest.Testing;

/// <summary>Developer probes are opt-in; desktop capture requires a second, explicit switch.</summary>
public sealed class DiagnosticFactAttribute : FactAttribute
{
    public DiagnosticFactAttribute(bool liveDesktop = false) => Skip = TestPrerequisites.DiagnosticSkipReason(
        Environment.GetEnvironmentVariable("SOULCREST_RUN_DIAGNOSTICS"),
        Environment.GetEnvironmentVariable("SOULCREST_RUN_LIVE_DIAGNOSTICS"), liveDesktop);
}

public sealed class DiagnosticTheoryAttribute : TheoryAttribute
{
    public DiagnosticTheoryAttribute(bool liveDesktop = false) => Skip = TestPrerequisites.DiagnosticSkipReason(
        Environment.GetEnvironmentVariable("SOULCREST_RUN_DIAGNOSTICS"),
        Environment.GetEnvironmentVariable("SOULCREST_RUN_LIVE_DIAGNOSTICS"), liveDesktop);
}

public enum TestRequirement { MapData, PrivateInstanceCatalog, InteractiveDesktop }

/// <summary>Report absent optional inputs as skipped instead of silently passing.</summary>
public sealed class RequiresFactAttribute : FactAttribute
{
    public RequiresFactAttribute(TestRequirement requirement) => Skip = TestPrerequisites.SkipReason(requirement);
}

public sealed class RequiresTheoryAttribute : TheoryAttribute
{
    public RequiresTheoryAttribute(TestRequirement requirement) => Skip = TestPrerequisites.SkipReason(requirement);
}

internal static class TestPrerequisites
{
    internal static string? DiagnosticSkipReason(string? enabled, string? liveEnabled, bool liveDesktop)
    {
        if (enabled != "1") return "Diagnostic: set SOULCREST_RUN_DIAGNOSTICS=1 for an explicit diagnostic run.";
        return liveDesktop && liveEnabled != "1"
            ? "Live desktop diagnostic: additionally set SOULCREST_RUN_LIVE_DIAGNOSTICS=1; may capture the desktop or game window."
            : null;
    }

    internal static string? SkipReason(TestRequirement requirement) => requirement switch
    {
        TestRequirement.MapData when MapDataDirectory is null => "Optional local map data package is absent.",
        TestRequirement.PrivateInstanceCatalog when !File.Exists(Path.Combine(RepositoryRoot, "data", "instances", "catalog.json"))
            => "Private instance catalog is absent; the public build intentionally excludes it.",
        TestRequirement.InteractiveDesktop when Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            => "Native hit-testing requires an interactive Windows desktop; GitHub Actions has none.",
        _ => null
    };

    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("Soulcrest.slnx not found above the test assembly.");
        }
    }

    internal static string? MapDataDirectory
    {
        get
        {
            var candidates = new List<string>();
            var configured = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "mapdata"));
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                candidates.Add(Path.Combine(directory.FullName, "imports", "generated", "mapdata"));
            return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "manifest.json")));
        }
    }

    internal static string RequiredValue(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Explicit diagnostic requires {variable}.");
        return value!;
    }

    internal static void RequiredFlag(string variable) =>
        Assert.True(Environment.GetEnvironmentVariable(variable) == "1", $"Explicit diagnostic requires {variable}=1.");

    internal static string RequiredFile(string variable)
    {
        var path = RequiredValue(variable);
        Assert.True(File.Exists(path), $"Diagnostic input from {variable} does not exist: {path}");
        return path;
    }
}
