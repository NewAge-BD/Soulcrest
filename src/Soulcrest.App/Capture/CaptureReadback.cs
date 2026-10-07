using System.Runtime.InteropServices;

namespace Soulcrest.App.Capture;

/// <summary>A busy GPU drops one frame; a device failure still reaches the tracker's error handler.</summary>
internal static class CaptureReadback
{
    internal const int WasStillDrawing = unchecked((int)0x887A000A);
    internal const string Waiting = "GPU beschäftigt – Bild übersprungen, nächster Versuch läuft …";

    internal static bool IsBusy(Exception error) => error is COMException && error.HResult == WasStillDrawing;

    internal static T? TryCapture<T>(Func<T> capture) where T : class
    {
        try { return capture(); }
        catch (Exception error) when (IsBusy(error)) { return null; }
    }
}
