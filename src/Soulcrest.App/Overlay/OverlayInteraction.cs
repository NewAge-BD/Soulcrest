namespace Soulcrest.App.Overlay;

/// <summary>Read-only keyboard state shared by Soulcrest's own overlay input surfaces.</summary>
internal static class OverlayInteraction
{
    private static readonly OverlayKeyboardInput Keyboard = new();
    private static readonly OverlayModifierHook Hook = new();
    internal static event Action? Changed;

    static OverlayInteraction() => Hook.Changed += () => Changed?.Invoke();

    // Explicit left/right state also covers AltGr; reading the high bit never intercepts or sends input.
    internal static bool IsAltHeld => ResolveHeld(Hook.AltHeld ?? Keyboard.AltHeld, IsAltDown(NativeMethods.GetAsyncKeyState));
    internal static bool IsShiftHeld => ResolveHeld(Hook.ShiftHeld ?? Keyboard.ShiftHeld,
        (NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0);

    internal static bool ResolveHeld(bool? observed, bool queried) => observed == true || queried;

    internal static string State => $"rawAlt={Keyboard.AltHeld}, hookAlt={Hook.AltHeld}, rawShift={Keyboard.ShiftHeld}, hookShift={Hook.ShiftHeld}";

    internal static bool StartKeyboard(nint target)
    {
        var raw = Keyboard.Start(target);
        // This observer has its own message pump, independent of WebView/host HWND changes.
        var hook = Hook.Start();
        Services.LogFile.Append("overlay-input.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} input sources: raw={raw}, observer={hook}, target=0x{target:X}{Environment.NewLine}");
        return raw || hook;
    }

    internal static void StopKeyboard()
    {
        Keyboard.Stop();
        Hook.Stop();
    }

    internal static bool ProcessMessage(int message, nint lParam)
    {
        if (message is not (OverlayKeyboardInput.InputMessage or OverlayKeyboardInput.DeviceChangeMessage)) return false;
        var alt = Keyboard.AltHeld;
        var shift = Keyboard.ShiftHeld;
        Keyboard.ProcessMessage(message, lParam);
        return alt != Keyboard.AltHeld || shift != Keyboard.ShiftHeld;
    }

    internal static bool IsAltDown(Func<int, short> readKey) =>
        (readKey((int)Keys.LMenu) & 0x8000) != 0 || (readKey((int)Keys.RMenu) & 0x8000) != 0
        || (readKey((int)Keys.Menu) & 0x8000) != 0;
}
