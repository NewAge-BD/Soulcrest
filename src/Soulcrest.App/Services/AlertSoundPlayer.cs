using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Soulcrest.App.Services;

public sealed record AlertSoundFileChoice(string? Path = null, string? Error = null);

/// <summary>Local notification audio only. Playback never resolves URLs or uploads the selected file.</summary>
public sealed class AlertSoundPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Func<IAlertAudioSession> _createSession;
    private readonly Func<IAlertAudioSession> _createStandardSession;
    private readonly Action<string> _playSystem;
    private readonly Func<Task<AlertSoundFileChoice>>? _chooseFile;
    private IAlertAudioSession? _session;
    private CancellationTokenSource? _request;
    private long _revision;
    private bool _disposed;
    private int _pickerBusy;
    private string? _status;

    public AlertSoundPlayer() : this(() => new WindowsAlertAudioSession(), PlaySystemSound) { }

    internal AlertSoundPlayer(Func<IAlertAudioSession> createSession, Action<string> playSystem,
        Func<Task<AlertSoundFileChoice>>? chooseFile = null, Func<IAlertAudioSession>? createStandardSession = null)
    {
        _createSession = createSession;
        _createStandardSession = createStandardSession ?? (() => new StandardAlertAudioSession());
        _playSystem = playSystem;
        _chooseFile = chooseFile;
    }

    public string? Status { get { lock (_gate) return _status; } }
    public event Action? Changed;

    /// <summary>Start one sound in the background; a newer request replaces earlier playback.</summary>
    public Task<bool> Play(string sound, string? soundFile = null)
    {
        CancellationTokenSource request;
        long revision;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            _request?.Cancel();
            request = new();
            _request = request;
            revision = ++_revision;
        }
        return Task.Run(() => PlayAsync(sound, soundFile, revision, request));
    }

    private async Task<bool> PlayAsync(string sound, string? soundFile, long revision, CancellationTokenSource request)
    {
        var entered = false;
        try
        {
            await _serial.WaitAsync(request.Token).ConfigureAwait(false);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            StopCurrent();
            SetStatus(null, revision);
            if (sound is not ("custom" or "standard"))
            {
                lock (_gate)
                {
                    if (_disposed || revision != _revision) return false;
                    _playSystem(SystemSound(sound));
                }
                return true;
            }
            if (sound == "custom" && (!AlertsService.ValidSoundFile(soundFile) || !File.Exists(soundFile)))
                throw new FileNotFoundException("The selected local MP3 is missing or invalid.");

            var session = sound == "standard" ? _createStandardSession() : _createSession();
            session.Failed += error => LateFailure(session, error, revision);
            lock (_gate)
            {
                if (_disposed || revision != _revision) { session.Dispose(); return false; }
                _session = session;
            }
            await session.StartAsync(sound == "standard" ? "" : Path.GetFullPath(soundFile!), request.Token)
                .WaitAsync(TimeSpan.FromSeconds(15), request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            ReportFailure(error, revision, sound == "custom");
            return false;
        }
        finally
        {
            if (entered) _serial.Release();
            lock (_gate) { if (ReferenceEquals(_request, request)) _request = null; }
            request.Dispose();
        }
    }

    private void LateFailure(IAlertAudioSession session, Exception error, long revision)
    {
        _ = Task.Run(async () =>
        {
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                    if (_disposed || revision != _revision || !ReferenceEquals(_session, session)) return;
                ReportFailure(error, revision, custom: true);
            }
            finally { _serial.Release(); }
        });
    }

    private void ReportFailure(Exception error, long revision, bool custom)
    {
        lock (_gate) if (_disposed || revision != _revision) return;
        LogFile.Error("Alert audio", error);
        StopCurrent();
        var message = custom
            ? UiText.T("Eigener Alert-Ton konnte nicht abgespielt werden. Windows-Hinweiston wird verwendet.")
            : UiText.T("Alert-Ton konnte nicht abgespielt werden.");
        if (custom)
        {
            try
            {
                lock (_gate)
                    if (!_disposed && revision == _revision) _playSystem("exclamation");
            }
            catch (Exception fallbackError) { LogFile.Error("Alert audio fallback", fallbackError); }
        }
        SetStatus(message, revision);
    }

    private void StopCurrent()
    {
        IAlertAudioSession? session;
        lock (_gate) { session = _session; _session = null; }
        if (session is null) return;
        try { session.Dispose(); }
        catch (Exception error) { LogFile.Error("Alert audio cleanup", error); }
    }

    private void SetStatus(string? status, long revision)
    {
        lock (_gate)
        {
            if (_disposed || revision != _revision) return;
            _status = status;
        }
        SafeEvent.Raise(Changed, "Alert audio");
    }

    public async Task<AlertSoundFileChoice> ChooseMp3Async()
    {
        lock (_gate) if (_disposed) return new();
        if (Interlocked.CompareExchange(ref _pickerBusy, 1, 0) != 0)
            return new(Error: UiText.T("Es ist bereits eine Dateiauswahl geöffnet."));
        try
        {
            var choice = await (_chooseFile?.Invoke() ?? ChooseLocalFileAsync()).ConfigureAwait(false);
            lock (_gate) if (_disposed) return new();
            if (choice.Path is not null && !AlertsService.ValidSoundFile(choice.Path))
                return new(Error: UiText.T("MP3-Datei konnte nicht ausgewählt werden."));
            return choice;
        }
        catch (Exception error)
        {
            LogFile.Error("Alert audio picker", error);
            return new(Error: UiText.T("MP3-Datei konnte nicht ausgewählt werden."));
        }
        finally { Volatile.Write(ref _pickerBusy, 0); }
    }

    private static Task<AlertSoundFileChoice> ChooseLocalFileAsync()
    {
        var title = UiText.T("MP3-Datei als Alert-Ton wählen");
        var filter = UiText.T("MP3-Audio|*.mp3");
        // The Windows dialog needs STA. Its own thread keeps the WebView responsive.
        var owner = GetActiveWindow();
        var result = new TaskCompletionSource<AlertSoundFileChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title, Filter = filter, DefaultExt = "mp3", CheckFileExists = true,
                    CheckPathExists = true, Multiselect = false, RestoreDirectory = true
                };
                var selected = dialog.ShowDialog(owner == 0 ? null : new DialogOwner(owner)) == DialogResult.OK;
                result.TrySetResult(selected ? new(Path.GetFullPath(dialog.FileName)) : new());
            }
            catch (Exception error) { result.TrySetException(error); }
        }) { IsBackground = true, Name = "Soulcrest MP3 picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private sealed record DialogOwner(nint Handle) : IWin32Window;
    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    private static string SystemSound(string? sound) => sound is "asterisk" or "beep" or "hand" or "question" ? sound : "exclamation";
    private static void PlaySystemSound(string sound) => (sound switch
    {
        "asterisk" => SystemSounds.Asterisk,
        "beep" => SystemSounds.Beep,
        "hand" => SystemSounds.Hand,
        "question" => SystemSounds.Question,
        _ => SystemSounds.Exclamation
    }).Play();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_revision;
            _request?.Cancel();
        }
        StopCurrent();
        // Pending continuations still release this gate; it owns no native resources.
        GC.SuppressFinalize(this);
    }
}

internal interface IAlertAudioSession : IDisposable
{
    event Action<Exception>? Failed;
    Task StartAsync(string path, CancellationToken cancellation);
}

/// <summary>Windows' supported MP3 decoder, using an explicit local StorageFile rather than a URI.</summary>
internal sealed class WindowsAlertAudioSession : IAlertAudioSession
{
    private readonly object _gate = new();
    private readonly MediaPlayer _player = new() { AutoPlay = false, IsLoopingEnabled = false };
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MediaSource? _source;
    private bool _disposed;

    public WindowsAlertAudioSession()
    {
        try
        {
            _player.CommandManager.IsEnabled = false;
            _player.MediaOpened += MediaOpened;
            _player.MediaFailed += MediaFailed;
            _player.MediaEnded += MediaEnded;
        }
        catch { _player.Dispose(); throw; }
    }

    public event Action<Exception>? Failed;
    public async Task StartAsync(string path, CancellationToken cancellation)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException(cancellation);
            _source = MediaSource.CreateFromStorageFile(file);
            _player.Source = _source;
            _player.Play();
        }
        await _opened.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void MediaOpened(MediaPlayer sender, object args) => _opened.TrySetResult();
    private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var error = new InvalidOperationException($"Windows could not decode the alert MP3 ({args.Error}).", args.ExtendedErrorCode);
        if (!_opened.TrySetException(error)) Failed?.Invoke(error);
    }
    private void MediaEnded(MediaPlayer sender, object args) => _ = Task.Run(Dispose);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _player.MediaOpened -= MediaOpened;
            _player.MediaFailed -= MediaFailed;
            _player.MediaEnded -= MediaEnded;
            _opened.TrySetCanceled();
            try { _player.Pause(); _player.Source = null; }
            catch (Exception error) { LogFile.Error("Alert audio stop", error); }
            try { _player.Dispose(); }
            catch (Exception error) { LogFile.Error("Alert audio player cleanup", error); }
            try { _source?.Dispose(); }
            catch (Exception error) { LogFile.Error("Alert audio source cleanup", error); }
            _source = null;
        }
    }
}
