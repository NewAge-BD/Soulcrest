namespace Soulcrest.App.Tests;

/// <summary>
/// The instance catalog lists every client definition, including test and regional ones, and stays private
/// (user decision 2026-10-21, <c>scripts/export_public.py</c>). Tests on its real facts run wherever the file
/// exists and are skipped in the public repository, which builds without it.
/// </summary>
internal static class PrivateInstanceCatalog
{
    internal static bool Missing { get; } = !File.Exists(Path.Combine(FindRoot(), "data", "instances", "catalog.json"));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Soulcrest.slnx not found");
    }
}
