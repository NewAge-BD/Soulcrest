using System.Runtime.InteropServices;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Observes only Alt and Shift transitions delivered by Windows to Soulcrest's own window.
/// Background raw input avoids querying the elevated game's foreground input state. It neither
/// intercepts nor suppresses keyboard messages, and no other key state is retained.
/// All members are used on the window's UI thread.
/// </summary>
internal sealed class OverlayKeyboardInput
{
    internal const int InputMessage = 0x00ff;
    internal const int DeviceChangeMessage = 0x00fe;
    internal const ushort KeyBreak = 0x0001;
    internal const ushort KeyE0 = 0x0002;
    internal const ushort KeyE1 = 0x0004;
    internal const ushort AltKey = 0x12;
    internal const ushort LeftAltKey = 0xa4;
    internal const ushort RightAltKey = 0xa5;
    internal const ushort ShiftKey = 0x10;
    internal const ushort LeftShiftKey = 0xa0;
    internal const ushort RightShiftKey = 0xa1;
    private const uint KeyboardType = 1;
    private const uint InputSink = 0x00000100;
    private const uint DeviceNotify = 0x00002000;
    private const uint Remove = 0x00000001;
    private const uint InputData = 0x10000003;
    private const uint MaximumKeyboardPacketSize = 1024;
    private readonly Dictionary<nint, ModifierState> _devices = [];
    private bool _registered, _altKnown, _shiftKnown;

    internal bool? AltHeld => _altKnown ? _devices.Values.Any(state => state.LeftAlt || state.RightAlt) : null;
    internal bool? ShiftHeld => _shiftKnown ? _devices.Values.Any(state => state.LeftShift || state.RightShift) : null;

    /// <summary>Registers passive background keyboard input on an existing Soulcrest window.</summary>
    internal bool Start(nint target)
    {
        Stop();
        if (target == 0) return false;
        var device = new OverlayRawInputDevice
        {
            UsagePage = 1, Usage = 6, Flags = InputSink | DeviceNotify, Target = target
        };
        _registered = OverlayKeyboardNativeMethods.RegisterRawInputDevices(ref device, 1,
            (uint)Marshal.SizeOf<OverlayRawInputDevice>());
        return _registered;
    }

    /// <summary>Unregisters only this instance's registration and forgets every observed modifier.</summary>
    internal void Stop()
    {
        if (_registered)
        {
            var device = new OverlayRawInputDevice { UsagePage = 1, Usage = 6, Flags = Remove, Target = 0 };
            OverlayKeyboardNativeMethods.RegisterRawInputDevices(ref device, 1,
                (uint)Marshal.SizeOf<OverlayRawInputDevice>());
            _registered = false;
        }
        _devices.Clear();
        _altKnown = _shiftKnown = false;
    }

    /// <summary>The caller still passes WM_INPUT to its base window procedure for Windows cleanup.</summary>
    internal void ProcessMessage(int message, nint lParam)
    {
        if (!_registered) return;
        if (message == DeviceChangeMessage)
        {
            RemoveDevice(lParam);
            return;
        }
        if (message != InputMessage || lParam == 0) return;
        ReadPacket(lParam, OverlayKeyboardNativeMethods.GetRawInputData);
    }

    /// <summary>
    /// RAWINPUT contains a union, so a native keyboard packet can exceed our header/keyboard view.
    /// Ask Windows for its size instead of passing the size of that smaller managed structure.
    /// </summary>
    internal bool ReadPacket(nint inputHandle, OverlayRawInputReader reader)
    {
        if (inputHandle == 0) return false;
        var headerSize = (uint)Marshal.SizeOf<OverlayRawInputHeader>();
        var minimumSize = headerSize + (uint)Marshal.SizeOf<OverlayRawKeyboard>();
        uint requiredSize = 0;
        var queried = reader(inputHandle, InputData, 0, ref requiredSize, headerSize);
        if (queried != 0 || requiredSize < minimumSize || requiredSize > MaximumKeyboardPacketSize)
            return false;

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            var availableSize = requiredSize;
            var copied = reader(inputHandle, InputData, buffer, ref availableSize, headerSize);
            if (copied == uint.MaxValue || copied < minimumSize || copied > requiredSize
                || availableSize < copied || availableSize > requiredSize) return false;

            var header = Marshal.PtrToStructure<OverlayRawInputHeader>(buffer);
            if (header.Type != KeyboardType || header.Size < minimumSize || header.Size > copied)
                return false;
            var keyboard = Marshal.PtrToStructure<OverlayRawKeyboard>(buffer + (int)headerSize);
            var input = new OverlayRawKeyboardInput { Header = header, Keyboard = keyboard };
            return ProcessPacket(input, copied, availableSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Accepts only a complete keyboard packet; mouse/HID or malformed packets are discarded.</summary>
    internal bool ProcessPacket(in OverlayRawKeyboardInput input, uint copied, uint size)
    {
        var minimumSize = (uint)(Marshal.SizeOf<OverlayRawInputHeader>() + Marshal.SizeOf<OverlayRawKeyboard>());
        if (copied == uint.MaxValue || copied < minimumSize || size < copied
            || size > MaximumKeyboardPacketSize || input.Header.Size < minimumSize || input.Header.Size > copied
            || input.Header.Type != KeyboardType) return false;
        return ProcessKeyboard(input.Header.Device, input.Keyboard.VirtualKey, input.Keyboard.MakeCode,
            input.Keyboard.Flags);
    }

    internal bool ProcessKeyboard(nint device, ushort virtualKey, ushort makeCode, ushort flags)
    {
        // E1 belongs to sequences such as Pause, not either modifier. Ignore malformed/fake keys.
        if ((flags & KeyE1) != 0 || virtualKey == 0xff) return false;
        var key = virtualKey switch
        {
            AltKey => (flags & KeyE0) != 0 ? RightAltKey : LeftAltKey,
            ShiftKey when makeCode == 0x36 => RightShiftKey,
            ShiftKey when makeCode == 0x2a => LeftShiftKey,
            LeftAltKey or RightAltKey or LeftShiftKey or RightShiftKey => virtualKey,
            _ => (ushort)0
        };
        if (key == 0) return false;
        _devices.TryGetValue(device, out var state);
        var down = (flags & KeyBreak) == 0;
        switch (key)
        {
            case LeftAltKey: state.LeftAlt = down; _altKnown = true; break;
            case RightAltKey: state.RightAlt = down; _altKnown = true; break;
            case LeftShiftKey: state.LeftShift = down; _shiftKnown = true; break;
            case RightShiftKey: state.RightShift = down; _shiftKnown = true; break;
        }
        if (state.LeftAlt || state.RightAlt || state.LeftShift || state.RightShift) _devices[device] = state;
        else _devices.Remove(device);
        return true;
    }

    internal void RemoveDevice(nint device) => _devices.Remove(device);

    private struct ModifierState
    {
        internal bool LeftAlt, RightAlt, LeftShift, RightShift;
    }
}

internal delegate uint OverlayRawInputReader(nint input, uint command, nint buffer, ref uint size, uint headerSize);

[StructLayout(LayoutKind.Sequential)]
internal struct OverlayRawInputDevice
{
    internal ushort UsagePage, Usage;
    internal uint Flags;
    internal nint Target;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OverlayRawInputHeader
{
    internal uint Type, Size;
    internal nint Device, Parameter;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OverlayRawKeyboard
{
    internal ushort MakeCode, Flags, Reserved, VirtualKey;
    internal uint Message, ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OverlayRawKeyboardInput
{
    internal OverlayRawInputHeader Header;
    internal OverlayRawKeyboard Keyboard;
}

internal static partial class OverlayKeyboardNativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices(ref OverlayRawInputDevice devices, uint count, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputData(nint input, uint command, nint buffer,
        ref uint size, uint headerSize);
}
