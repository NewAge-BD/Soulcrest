using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>Atomic JSON persistence (pattern from Grindcrest AtomicFile).</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path))
                return new T();
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start fresh.
            File.Copy(path, path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, Options);
        Retry(() =>
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        });
    }

    /// <summary>
    /// Runs a file write again when the file is held for a moment (virus scanner, backup of the desktop
    /// tester, an editor): five tries within about half a second, then the last error is thrown.
    /// </summary>
    public static void Retry(Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
