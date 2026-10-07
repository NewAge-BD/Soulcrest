namespace Soulcrest.App.Services;

/// <summary>
/// The diagnosis logs in logs\ (map-tracking.log, pet-scan.log, errors.log). Each stays below 1 MB: a full
/// log becomes name.old.log (one generation, the one before is dropped), so the logs never grow without end
/// (2026-10-07: map-tracking.log had reached 2.4 MB after three days). The diagnosis package takes all.
/// Writing a log never throws: a locked or read-only log must not stop tracking or capture (review 2026-10-07).
/// </summary>
public static class LogFile
{
    internal const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Append(string name, string text) => Append(AppPaths.LogsDirectory, name, text, MaxBytes);

    /// <summary>An exception that was caught so the app keeps running, with where it happened (errors.log).</summary>
    public static void Error(string where, Exception error) =>
        Append("errors.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {error}{Environment.NewLine}");

    internal static void Append(string directory, string name, string text, long maxBytes)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= maxBytes)
                    File.Move(path, Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".old.log"), overwrite: true);
                File.AppendAllText(path, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
