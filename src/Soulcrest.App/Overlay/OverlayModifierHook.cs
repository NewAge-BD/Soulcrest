using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Observes Alt and Shift on Soulcrest's own dedicated message thread. WH_KEYBOARD_LL callbacks
/// execute in the installing process; no code is loaded into the game and every input is passed on.
/// No other key state, key history or input text is retained.
/// </summary>
internal sealed class OverlayModifierHook : IDisposable
{
    internal const uint KeyDown = 0x0100, KeyUp = 0x0101, SystemKeyDown = 0x0104, SystemKeyUp = 0x0105;
    internal const uint AltKey = 0x12, LeftAltKey = 0xa4, RightAltKey = 0xa5;
    internal const uint ShiftKey = 0x10, LeftShiftKey = 0xa0, RightShiftKey = 0xa1;
    internal const uint ExtendedKey = 0x01;
    private const int AltKnown = 1, ShiftKnown = 2, LeftAlt = 4, RightAlt = 8, LeftShift = 16, RightShift = 32;
    private const int KeyboardLowLevel = 13;
    private const uint QuitMessage = 0x0012;
    private readonly object _lifecycle = new();
    private readonly HookProcedure _procedure;
    private HookRun? _run;
    private int _modifiers;
    private bool _disposed;

    internal OverlayModifierHook() => _procedure = HookCallback;

    internal bool? AltHeld => AltState(Volatile.Read(ref _modifiers));
    internal bool? ShiftHeld => ShiftState(Volatile.Read(ref _modifiers));

    /// <summary>Runs on the hook thread. Subscribers must enqueue UI work and return immediately.</summary>
    internal event Action? Changed;

    internal bool Start()
    {
        HookRun run;
        lock (_lifecycle)
        {
            if (_disposed) return false;
            if (_run is { Thread.IsAlive: true } existing)
                return Volatile.Read(ref existing.StopRequested) == 0 && Interlocked.CompareExchange(ref existing.Hook, 0, 0) != 0;
            ResetModifiers();
            run = new HookRun();
            run.Thread = new Thread(() => RunMessageLoop(run)) { IsBackground = true, Name = "Soulcrest overlay modifiers" };
            _run = run;
            run.Thread.Start();
        }
        if (!run.Ready.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            Stop();
            return false;
        }
        return run.Ready.Task.Result && Volatile.Read(ref run.StopRequested) == 0;
    }

    internal void Stop()
    {
        HookRun? run;
        lock (_lifecycle)
        {
            run = _run;
            if (run is not null) Volatile.Write(ref run.StopRequested, 1);
        }
        if (run is not null)
        {
            Unhook(run);
            var threadId = Volatile.Read(ref run.ThreadId);
            if (threadId != 0) HookNative.PostThreadMessage(threadId, QuitMessage, 0, 0);
            if (run.Thread != Thread.CurrentThread) run.Thread?.Join(TimeSpan.FromSeconds(2));
        }
        ResetModifiers();
    }

    public void Dispose()
    {
        lock (_lifecycle) _disposed = true;
        Stop();
    }

    private void RunMessageLoop(HookRun run)
    {
        try
        {
            // Create the thread's queue before publishing its id, so Stop can reliably post WM_QUIT.
            HookNative.PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref run.ThreadId, HookNative.GetCurrentThreadId());
            if (Volatile.Read(ref run.StopRequested) != 0) return;
            var hook = HookNative.SetWindowsHookEx(KeyboardLowLevel, _procedure, HookNative.GetModuleHandle(null), 0);
            Interlocked.Exchange(ref run.Hook, hook);
            run.Ready.TrySetResult(hook != 0);
            if (hook == 0) return;
            while (Volatile.Read(ref run.StopRequested) == 0 && HookNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                HookNative.TranslateMessage(in message);
                HookNative.DispatchMessage(in message);
            }
        }
        catch
        {
            // A failed modifier observer must never prevent the application's normal input path.
            run.Ready.TrySetResult(false);
        }
        finally
        {
            Unhook(run);
            Volatile.Write(ref run.ThreadId, 0);
            ResetModifiers();
            run.Ready.TrySetResult(false);
        }
    }

    private nint HookCallback(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && data != 0 && _run is { } run && Volatile.Read(ref run.StopRequested) == 0)
            {
                var key = unchecked((uint)Marshal.ReadInt32(data));
                if (IsModifier(key))
                    ProcessTransition((uint)message, key, unchecked((uint)Marshal.ReadInt32(data, 4)),
                        unchecked((uint)Marshal.ReadInt32(data, 8)));
            }
        }
        catch
        {
            // Exceptions, including subscriber failures, must not escape into the native hook chain.
        }
        return HookNative.CallNextHookEx(0, code, message, data);
    }

    /// <summary>Updates only the two modifier families; repeats do not accumulate and no input is consumed.</summary>
    internal bool ProcessTransition(uint message, uint virtualKey, uint scanCode, uint flags)
    {
        if (message is not (KeyDown or KeyUp or SystemKeyDown or SystemKeyUp)) return false;
        var key = virtualKey switch
        {
            LeftAltKey => LeftAlt,
            RightAltKey => RightAlt,
            AltKey => (flags & ExtendedKey) != 0 ? RightAlt : LeftAlt,
            LeftShiftKey => LeftShift,
            RightShiftKey => RightShift,
            ShiftKey when scanCode == 0x2a => LeftShift,
            ShiftKey when scanCode == 0x36 => RightShift,
            _ => 0
        };
        if (key == 0) return false;
        var known = (key & (LeftAlt | RightAlt)) != 0 ? AltKnown : ShiftKnown;
        var down = message is KeyDown or SystemKeyDown;
        int before, after;
        do
        {
            before = Volatile.Read(ref _modifiers);
            after = (down ? before | key : before & ~key) | known;
        } while (Interlocked.CompareExchange(ref _modifiers, after, before) != before);
        if (AltState(before) != AltState(after) || ShiftState(before) != ShiftState(after)) NotifyChanged();
        return true;
    }

    private static bool IsModifier(uint key) => key is AltKey or LeftAltKey or RightAltKey or ShiftKey or LeftShiftKey or RightShiftKey;
    private static bool? AltState(int state) => (state & AltKnown) == 0 ? null : (state & (LeftAlt | RightAlt)) != 0;
    private static bool? ShiftState(int state) => (state & ShiftKnown) == 0 ? null : (state & (LeftShift | RightShift)) != 0;

    private void ResetModifiers()
    {
        if (Interlocked.Exchange(ref _modifiers, 0) != 0) NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* Subscriber shutdown cannot break the native input chain. */ }
    }

    private static void Unhook(HookRun run)
    {
        var hook = Interlocked.Exchange(ref run.Hook, 0);
        if (hook != 0) HookNative.UnhookWindowsHookEx(hook);
    }

    private sealed class HookRun
    {
        internal Thread? Thread;
        internal nint Hook;
        internal uint ThreadId;
        internal int StopRequested;
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    private static class HookNative
    {
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        internal static extern nint SetWindowsHookEx(int hook, HookProcedure callback, nint module, uint thread);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandle(string? module);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out NativeMessage message, nint window, uint first, uint last, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessage(out NativeMessage message, nint window, uint first, uint last);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(in NativeMessage message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(in NativeMessage message);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    }
}
