using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>A downloadable setup of a release with its published checksum file.</summary>
public sealed record SetupAsset(string Url, string? Checksum, long Size);

/// <summary>
/// A published version newer than the running one, with its patch notes (GitHub release text). Full: setup
/// with the map data; Update: the same without them, for installations whose map data matches BUILD-INFO.
/// </summary>
public sealed record AvailableUpdate(Version Version, string Notes, string Page, SetupAsset? Full, SetupAsset? Update = null,
    string? BuildInfo = null);

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Starting, Failed }

/// <summary>
/// Looks for new versions in the releases of the public repository (user request 2026-10-07): after every
/// start (switchable) and with "Nach Updates suchen" under Info. The only runtime network access besides the
/// loot capture: one request to api.github.com, and the installer download from github.com when the user
/// starts the update. Nothing is sent but the request itself. The installer is checked against the
/// published SHA-256 before it runs.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "NewAge-BD/Soulcrest";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(8);
    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private int _busy;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Soulcrest", Installed?.ToString() ?? "dev"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>"0.1.30" of "0.1.30+abc1234…"; null for a build without a numeric version.</summary>
    public static Version? Installed => ParseVersion(AppPaths.Version.Split('+', 2)[0]);

    public UpdateState State { get; private set; }
    public string? Message { get; private set; }
    public double Progress { get; private set; }
    public DateTime? CheckedAt { get; private set; }

    /// <summary>Newer versions, newest first; the first one is installed.</summary>
    public IReadOnlyList<AvailableUpdate> Available { get; private set; } = [];

    /// <summary>
    /// What "Jetzt aktualisieren" downloads: the small update setup when the installed map data matches the
    /// new version (user request 2026-10-07: not 420 MB every time), else the full setup.
    /// </summary>
    public SetupAsset? Planned { get; private set; }
    public bool PlannedIsFull { get; private set; }

    /// <summary>The notice was closed with "Später" (until the next start or check).</summary>
    public bool Dismissed { get; private set; }

    public event Action? Changed;

    /// <summary>Asks the main window to close so the installer can replace the files.</summary>
    public event Action? ExitRequested;

    /// <summary>After the start: not in the desktop tester (it builds from source) or the self-test.</summary>
    public void StartAutomaticCheck()
    {
        if (!_settings.Current.CheckUpdatesOnStart || SmokeTest.Enabled || AppPaths.SourceRoot is not null || Installed is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, _stop.Token);
                await CheckAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            Set(UpdateState.Checking, "Suche nach Updates …");
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", _stop.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_stop.Token);
            Available = Newer(json, Installed);
            (Planned, PlannedIsFull) = Available.Count == 0 ? (null, false) : await PlanAsync(Available[0]);
            Dismissed = false;
            CheckedAt = DateTime.Now;
            if (Available.Count == 0)
                Set(UpdateState.UpToDate, "Soulcrest ist aktuell.");
            else
                Set(UpdateState.Available, "Update verfügbar.");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogFile.Error("Update-Suche", e);
            Set(UpdateState.Failed, "Update-Suche fehlgeschlagen (keine Verbindung zu GitHub?).");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Shows the notice again ("Version … ansehen" under Info).</summary>
    public void Reopen()
    {
        Dismissed = false;
        SafeEvent.Raise(Changed, "Update");
    }

    public void Dismiss()
    {
        Dismissed = true;
        SafeEvent.Raise(Changed, "Update");
    }

    /// <summary>Downloads the installer of the newest version, checks it and starts it; Soulcrest then closes.</summary>
    public async Task InstallAsync()
    {
        if (Available.FirstOrDefault() is not { } update || Planned is not { } setup)
            return;
        var installerUrl = setup.Url;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            Progress = 0;
            Set(UpdateState.Downloading, "Update wird geladen …");
            var folder = Path.Combine(Path.GetTempPath(), "Soulcrest-Update");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, Path.GetFileName(new Uri(installerUrl).LocalPath));
            string? expected = null;
            if (setup.Checksum is { } checksumUrl)
                expected = ChecksumOf(await _http.GetStringAsync(checksumUrl, _stop.Token));
            if (expected is null)
                throw new InvalidDataException("Keine Prüfsumme zum Installer veröffentlicht.");

            using (var response = await _http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, _stop.Token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(_stop.Token);
                await using var target = File.Create(file);
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReport = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                    done += read;
                    if (total is > 0 && lastReport.ElapsedMilliseconds > 250)
                    {
                        lastReport.Restart();
                        Progress = (double)done / total.Value;
                        SafeEvent.Raise(Changed, "Update");
                    }
                }
            }
            await using (var check = File.OpenRead(file))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, _stop.Token));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    throw new InvalidDataException("Die Prüfsumme des Installers stimmt nicht.");
                }
            }
            Progress = 1;
            Set(UpdateState.Starting, "Installer startet, Soulcrest wird beendet …");
            // Quiet with a progress window; the setup opens Soulcrest again afterwards (/RESTARTAPP).
            Process.Start(new ProcessStartInfo(file, "/SILENT /RESTARTAPP") { UseShellExecute = true });
            ExitRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                      or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogFile.Error("Update", e);
            Set(UpdateState.Failed, e is InvalidDataException ? e.Message : "Update fehlgeschlagen. Details in logs\\errors.log.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Published, non-draft, non-prerelease versions above <paramref name="installed"/>, newest first.</summary>
    internal static IReadOnlyList<AvailableUpdate> Newer(string releasesJson, Version? installed)
    {
        using var document = JsonDocument.Parse(releasesJson);
        var list = new List<AvailableUpdate>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease"))
                continue;
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || !tag.StartsWith('v') || ParseVersion(tag[1..]) is not { } version)
                continue;
            if (installed is not null && version <= installed)
                continue;
            var files = new Dictionary<string, (string Url, long Size)>(StringComparer.OrdinalIgnoreCase);
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (url is not null)
                        files[name] = (url, asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);
                }
            }
            SetupAsset? Setup(string kind)
            {
                var name = files.Keys.FirstOrDefault(n => n.EndsWith($"-{kind}-win-x64.exe", StringComparison.OrdinalIgnoreCase));
                if (name is null)
                    return null;
                return new SetupAsset(files[name].Url, files.TryGetValue(name + ".sha256", out var sum) ? sum.Url : null, files[name].Size);
            }
            list.Add(new AvailableUpdate(version,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                release.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                Setup("Setup"), Setup("Update"),
                files.TryGetValue("BUILD-INFO.json", out var info) ? info.Url : null));
        }
        return [.. list.OrderByDescending(u => u.Version)];
    }

    /// <summary>
    /// The update setup only when the installed map data (next to Soulcrest.exe) is the one the new version
    /// was built with; it would refuse to run otherwise. Any doubt takes the full setup.
    /// </summary>
    private async Task<(SetupAsset?, bool)> PlanAsync(AvailableUpdate update)
    {
        if (update.Update is not null && update.BuildInfo is { } infoUrl)
        {
            try
            {
                var wanted = MapManifestOf(await _http.GetStringAsync(infoUrl, _stop.Token));
                var manifest = Path.Combine(AppContext.BaseDirectory, "mapdata", "manifest.json");
                if (wanted is not null && File.Exists(manifest))
                {
                    await using var stream = File.OpenRead(manifest);
                    var installed = Convert.ToHexString(await SHA256.HashDataAsync(stream, _stop.Token));
                    if (string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase))
                        return (update.Update, false);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                LogFile.Error("Update-Planung", e);
            }
        }
        return (update.Full, true);
    }

    /// <summary>mapManifestSha256 of a BUILD-INFO.json (Build-Installer.ps1).</summary>
    internal static string? MapManifestOf(string buildInfo)
    {
        using var document = JsonDocument.Parse(buildInfo.TrimStart('\uFEFF'));
        return document.RootElement.TryGetProperty("mapManifestSha256", out var value) ? value.GetString() : null;
    }

    /// <summary>"&lt;sha256&gt;  file.exe" (Build-Installer.ps1) → the hash; null when it is not one.</summary>
    internal static string? ChecksumOf(string text)
    {
        var first = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first : null;
    }

    internal static Version? ParseVersion(string text) =>
        Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void Set(UpdateState state, string message)
    {
        State = state;
        Message = message;
        SafeEvent.Raise(Changed, "Update");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
