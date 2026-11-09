using System.Runtime.InteropServices;

namespace Soulcrest.App.Services;

/// <summary>Loads the bundled Microsoft CRT before native solver dependencies need it.</summary>
internal static class DaevanionNativeRuntime
{
    private static readonly object Gate = new();
    internal static string? LoadedDirectory { get; private set; }

    internal static bool LoadBundledRuntime()
    {
        lock (Gate)
        {
            if (LoadedDirectory is not null) return true;
            if (!OperatingSystem.IsWindows()) return false;
            string[] names = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];
            var directories = ((AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string) ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Append(AppContext.BaseDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                if (!names.All(name => File.Exists(Path.Combine(directory, name)))) continue;
                foreach (var name in names) NativeLibrary.Load(Path.Combine(directory, name));
                LoadedDirectory = Path.GetFullPath(directory);
                return true;
            }
            // Development builds can use the already installed CRT. Published builds are
            // required to include the official files and are checked by the solver smoke test.
            return false;
        }
    }
}
