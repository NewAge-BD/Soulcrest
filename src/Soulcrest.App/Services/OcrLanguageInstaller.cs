using System.ComponentModel;
using System.Diagnostics;
using Soulcrest.Ocr;

namespace Soulcrest.App.Services;

public sealed class OcrLanguageInstaller
{
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public string? Status { get; private set; }
    public IReadOnlyList<string> Missing { get; private set; } = [];

    /// <summary>When the installed OCR languages were last checked (UTC), null before the first check.</summary>
    public DateTime? CheckedAt { get; private set; }
    public event Action? Changed;

    public void Refresh()
    {
        try
        {
            var languages = WindowsOcrLineReader.AvailableLanguages();
            Missing = new[] { "de-DE", "en-US" }.Where(tag =>
                !languages.Any(installed => installed.StartsWith(tag[..2] + "-", StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        catch (Exception e) { Status = e.Message; }
        CheckedAt = DateTime.UtcNow;
        Changed?.Invoke();
    }

    internal static ProcessStartInfo InstallationCommand(string language)
    {
        if (language is not ("de-DE" or "en-US")) throw new ArgumentOutOfRangeException(nameof(language));
        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
            Arguments = $"/Online /Add-Capability /CapabilityName:Language.OCR~~~{language}~0.0.1.0 /NoRestart /Quiet",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    public async Task InstallAsync(string language)
    {
        var command = InstallationCommand(language);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        Status = "OCR-Sprachpaket wird installiert …";
        Changed?.Invoke();
        try
        {
            var code = await Task.Run(async () =>
            {
                using var process = Process.Start(command) ?? throw new InvalidOperationException("Windows-Installation konnte nicht gestartet werden.");
                await process.WaitForExitAsync();
                return process.ExitCode;
            });
            Refresh();
            Status = code == 3010 ? "OCR-Paket installiert. Windows-Neustart erforderlich."
                : code != 0 ? UiText.F("OCR-Installation fehlgeschlagen (Code {0}). Details: C:\\Windows\\Logs\\DISM\\dism.log", code)
                : Missing.Contains(language) ? "Installation abgeschlossen. Bitte Soulcrest neu starten und erneut prüfen."
                : "OCR-Sprachpaket ist verfügbar.";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Status = "Installation abgebrochen (Administratorfreigabe abgelehnt).";
        }
        catch (Exception e) { Status = e.Message; }
        finally { Interlocked.Exchange(ref _busy, 0); Changed?.Invoke(); }
    }
}
